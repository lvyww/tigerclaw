using System;
using System.Collections.Generic;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed class SentenceLearningEvent
    {
        public string Id = Guid.NewGuid().ToString("N");
        public long Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public string Mode = "", Code = "", Text = "", Context = "";
        public int RawStart, RawEnd, TextStart, TextEnd;
        public int Levels = 1; // One confirmed correction; ranking may advance 1..3 levels.
        internal SentenceLearningEvent Copy() => (SentenceLearningEvent)MemberwiseClone();
    }

    internal static class SentenceLearning
    {
        internal static bool LegacyPairMode(string mode) =>
            mode != null && (mode.StartsWith("fusion-v1|", StringComparison.Ordinal) ||
                             mode.StartsWith("exact-correction-v1|", StringComparison.Ordinal));

        internal const string PinyinPhraseMode = "full-pinyin-v1";
        internal const string PinyinCharacterMode = "full-pinyin-character-v1";
        // Replay legacy character events into a separate competition bucket without
        // rewriting the receipt journal or losing its undo identities.
        internal static string EffectiveMode(string mode, string text) =>
            mode == PinyinPhraseMode && Characters(text) == 1 ? PinyinCharacterMode : mode;

        internal static string ConfigurationHash(string text)
        {
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (char c in text ?? "") { hash ^= (byte)c; hash *= 1099511628211UL; hash ^= (byte)(c >> 8); hash *= 1099511628211UL; } }
            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static bool IsReservedFile(string name) =>
            name.StartsWith("自学习", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".tigerclaw-learning", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".tigirl-learning", StringComparison.OrdinalIgnoreCase);

        internal static int Characters(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++, count++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return 0;
                }
                else if (char.IsLowSurrogate(text[i])) return 0;
            }
            return count;
        }

        internal static string Context(string prefix) => Context(prefix, prefix.Length);

        internal static string Context(string prefix, int end)
        {
            if ((uint)end > (uint)prefix.Length) throw new ArgumentOutOfRangeException(nameof(end));
            int start = end;
            for (int i = 0; i < 2 && start > 0; i++)
            {
                start--;
                if (char.IsLowSurrogate(prefix[start]) && start > 0) start--;
            }
            return prefix.Substring(start, end - start);
        }

        internal static bool StaticText(string text)
        {
            int count = Characters(text);
            return count > 0 && count <= 16 && !text.Any(c => c < 32 || c == 127 || c == '{' || c == '}' || (c >= 0xe000 && c <= 0xf8ff));
        }

        private static SortedDictionary<int, int> Boundaries(SentenceCandidate candidate, int length)
        {
            var map = new SortedDictionary<int, int> { [0] = 0 };
            var chain = new List<SentencePathBoundary>();
            for (var b = candidate.Boundary; b != null; b = b.Previous) chain.Add(b);
            chain.Reverse();
            int raw = 0, text = 0;
            foreach (var b in chain)
            {
                if (b.RawLength <= raw || b.TextLength <= text || b.RawLength > length || b.TextLength > candidate.Text.Length ||
                    Characters(candidate.Text.Substring(text, b.TextLength - text)) == 0) return null;
                map[b.RawLength] = b.TextLength; raw = b.RawLength; text = b.TextLength;
            }
            return raw == length && text == candidate.Text.Length ? map : null;
        }

        internal static List<SentenceLearningEvent> Diff(string raw, SentenceCandidate before, SentenceCandidate selected, int floor, string mode)
        {
            var result = new List<SentenceLearningEvent>();
            if (before == null || selected == null || before.Text == selected.Text || string.IsNullOrEmpty(mode)) return result;
            var a = Boundaries(before, raw.Length); var b = Boundaries(selected, raw.Length);
            if (a == null || b == null) return result;
            int previous = 0;
            foreach (var edge in a)
            {
                int end = edge.Key;
                if (end == 0 || !b.ContainsKey(end)) continue;
                int first = b[previous], last = b[end];
                string chosen = selected.Text.Substring(first, last - first);
                string old = before.Text.Substring(a[previous], edge.Value - a[previous]);
                if (previous >= floor && chosen != old && StaticText(chosen))
                {
                    result.Add(new SentenceLearningEvent
                    {
                        Mode = mode, Code = raw.Substring(previous, end - previous).ToLowerInvariant(), Text = chosen,
                        Context = Context(selected.Text, first),
                        RawStart = previous, RawEnd = end, TextStart = first, TextEnd = last
                    });
                }
                previous = end;
            }
            return result;
        }

        internal static void PlanCorrectionLevels(List<SentenceLearningEvent> events, SentenceLearningSnapshot snapshot,
            string raw, SentenceCandidate before, SentenceCandidate selected, bool beforeIsCorrected = false)
        {
            if (events == null || events.Count == 0 || before == null || selected == null) return;
            snapshot ??= SentenceLearningSnapshot.Empty;
            // One event per known fragment/context, even if overlapping diff and
            // inner-fragment discovery both found it. Never manufacture clicks.
            var seen = new HashSet<(string, string, string, string)>();
            events.RemoveAll(e => !seen.Add((e.Mode, e.Code, e.Text, e.Context)));
            double required = before.FinalScore - (beforeIsCorrected ? 0 : before.LearningScore);
            double chosen = selected.FinalScore - selected.LearningScore;
            int levels = 1;
            if (double.IsFinite(required) && double.IsFinite(chosen))
                for (; levels < 3; levels++)
                {
                    double left = required + (beforeIsCorrected ? 0 : ProjectedReward(raw, before, events, snapshot, levels));
                    double right = chosen + ProjectedReward(raw, selected, events, snapshot, levels);
                    if (right >= left + 1.0) break;
                }
            foreach (var e in events) e.Levels = levels;
        }

        private static double ProjectedReward(string raw, SentenceCandidate candidate,
            List<SentenceLearningEvent> events, SentenceLearningSnapshot snapshot, int levels)
        {
            var map = Boundaries(candidate, raw.Length);
            if (map == null) return candidate.LearningScore;
            var points = map.ToArray();
            var best = new double[points.Length];
            for (int end = 1; end < points.Length; end++)
            {
                best[end] = best[end - 1];
                for (int start = end - 1; start >= 0; start--)
                {
                    string text = candidate.Text.Substring(points[start].Value, points[end].Value - points[start].Value);
                    if (Characters(text) > 16) break;
                    string code = raw.Substring(points[start].Key, points[end].Key - points[start].Key).ToLowerInvariant();
                    string context = Context(candidate.Text, points[start].Value);
                    best[end] = Math.Max(best[end], best[start] + snapshot.ProjectedScore(events[0].Mode, code, text, context, events, levels));
                }
            }
            return best[^1];
        }

        // Diff establishes trusted typed-code/selected-text boundaries. Only a
        // standalone two-character selection creates a whole-phrase event;
        // longer inputs prefer one unambiguous known fragment over overlap.
        internal static List<SentenceLearningEvent> SelectionEvents(string raw, SentenceCandidate before,
            SentenceCandidate selected, int floor, string mode, SentenceLearningSnapshot snapshot,
            Func<string, bool> supplemental = null)
        {
            if (string.IsNullOrEmpty(mode) || LegacyPairMode(mode)) return new();
            var result = Diff(raw, before, selected, floor, mode);
            if (result.Count == 0) return result;
            SentenceLearningEvent Make(int rs, int re, int ts, int te) => new()
            {
                Mode = mode, Code = raw.Substring(rs, re - rs).ToLowerInvariant(),
                Text = selected.Text.Substring(ts, te - ts), Context = Context(selected.Text, ts),
                RawStart = rs, RawEnd = re, TextStart = ts, TextEnd = te
            };
            if (floor == 0 && raw.Length <= 128 && Characters(selected.Text) == 2 && StaticText(selected.Text))
                return new() { Make(0, raw.Length, 0, selected.Text.Length) };
            var points = Boundaries(selected, raw.Length).ToArray();
            var matches = new List<(int Characters, SentenceLearningEvent Event)>();
            bool Overlaps(int start, int end) => result.Any(e => start < e.RawEnd && end > e.RawStart);
            if ((snapshot != null && !snapshot.IsEmpty) || supplemental != null)
            {
                for (int start = 0; start + 1 < points.Length; start++)
                {
                    var first = points[start];
                    if (first.Key < floor) continue;
                    bool nearby = result.Any(e => first.Key < e.RawEnd && e.RawStart - first.Key < 128 &&
                        (first.Value >= e.TextStart || Characters(selected.Text.Substring(first.Value, e.TextStart - first.Value)) < 16));
                    if (!nearby) continue;
                    for (int end = start + 1; end < points.Length; end++)
                    {
                        var last = points[end];
                        string text = selected.Text.Substring(first.Value, last.Value - first.Value);
                        int characters = Characters(text);
                        if (characters == 0 || characters > 16 || last.Key - first.Key > 128) break;
                        if (!Overlaps(first.Key, last.Key) || !StaticText(text) ||
                            before.Text.Contains(text, StringComparison.Ordinal)) continue;
                        var e = Make(first.Key, last.Key, first.Value, last.Value);
                        if ((snapshot?.Score(mode, e.Code, e.Text, e.Context) ?? 0) <= 0 &&
                            !(supplemental?.Invoke(e.Text) ?? false)) continue;
                        matches.Add((characters, e));
                    }
                }
            }
            if (matches.Count > 0)
            {
                int longest = matches.Max(m => m.Characters);
                var best = matches.Where(m => m.Characters == longest).ToArray();
                if (best.Length == 1 && matches.All(m => m.Event.RawStart >= best[0].Event.RawStart &&
                    m.Event.RawEnd <= best[0].Event.RawEnd))
                {
                    var winner = best[0].Event;
                    result.RemoveAll(e => e.RawStart < winner.RawEnd && e.RawEnd > winner.RawStart);
                    result.Add(winner);
                }
            }
            result.RemoveAll(e => e.Code.Length == 0 || e.Code.Length > 128);
            result.Sort((x, y) => x.RawStart.CompareTo(y.RawStart));
            return result;
        }

        internal static List<SentenceLearningEvent> ReinforceExisting(string raw, SentenceCandidate before,
            SentenceCandidate selected, int floor, string mode, SentenceLearningSnapshot snapshot, Func<string, bool> supplemental = null)
        {
            var result = new List<SentenceLearningEvent>();
            if ((snapshot == null || snapshot.IsEmpty) && supplemental == null) return result;
            if (before == null || selected == null ||
                before.Text == selected.Text || string.IsNullOrEmpty(mode)) return result;
            var a = Boundaries(before, raw.Length); var b = Boundaries(selected, raw.Length);
            if (a == null || b == null) return result;
            int previous = 0;
            foreach (var edge in a)
            {
                int end = edge.Key;
                if (end == 0 || !b.ContainsKey(end)) continue;
                string changed = selected.Text.Substring(b[previous], b[end] - b[previous]);
                string old = before.Text.Substring(a[previous], edge.Value - a[previous]);
                if (previous >= floor && changed != old)
                {
                    var matches = new List<(int rs,int re,int ts,int te,int n,string code,string text,string context)>();
                    foreach (var start in b.Where(x => x.Key >= previous && x.Key < end))
                    foreach (var finish in b.Where(x => x.Key > start.Key && x.Key <= end))
                    {
                        string text = selected.Text.Substring(start.Value, finish.Value - start.Value);
                        int n = Characters(text);
                        if (n == 0 || n > 16 || !StaticText(text) || before.Text.Contains(text, StringComparison.Ordinal)) continue;
                        string code = raw.Substring(start.Key, finish.Key - start.Key).ToLowerInvariant();
                        string context = Context(selected.Text, start.Value);
                        if ((snapshot?.Score(mode, code, text, context) ?? 0) > 0 || (supplemental?.Invoke(text) ?? false))
                            matches.Add((start.Key,finish.Key,start.Value,finish.Value,n,code,text,context));
                    }
                    if (matches.Count > 0)
                    {
                        int longest = matches.Max(x => x.n);
                        var bests = matches.Where(x => x.n == longest).ToArray();
                        if (bests.Length == 1 && matches.All(x => x.rs >= bests[0].rs && x.re <= bests[0].re))
                        {
                            var best = bests[0];
                            result.Add(new SentenceLearningEvent { Mode=mode, Code=best.code, Text=best.text, Context=best.context,
                                RawStart=best.rs, RawEnd=best.re, TextStart=best.ts, TextEnd=best.te });
                        }
                    }
                }
                previous = end;
            }
            if (result.Count != 1) result.Clear();
            return result;
        }

    }

    // Immutable query snapshot. Replay/aggregation happens once on the store
    // worker; candidate generation never scans events or context histories.
    internal sealed partial class SentenceLearningSnapshot
    {
        internal static readonly SentenceLearningSnapshot Empty = new();

        private sealed class Choice
        {
            internal double Weight, Confirmed;
        }

        private sealed class Scores
        {
            internal readonly Dictionary<string, double> Exact = new(StringComparer.Ordinal);
            internal double General, ConfidenceGeneral;
            internal readonly Dictionary<string, double> Weights = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> ConfidenceExact = new(StringComparer.Ordinal);

            internal double ForContext(string context) =>
                Exact.TryGetValue(context, out double exact) ? Math.Max(exact, General) : General;

            // max_t(max(exact_t[context], general_t)) can be pre-aggregated
            // independently for each context and for the general score.
            internal void Include(Scores other)
            {
                General = Math.Max(General, other.General);
                foreach (var entry in other.Exact)
                {
                    if (!Exact.TryGetValue(entry.Key, out double previous) || entry.Value > previous)
                        Exact[entry.Key] = entry.Value;
                }
            }
        }

        private sealed class Summary
        {
            internal readonly Scores Scores = new();
            internal double Weight, Confirmed;
        }

        private sealed class ModeIndex
        {
            internal readonly Dictionary<string, Scores> Texts = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, Scores> Prefixes = new(StringComparer.Ordinal);
            internal KeyValuePair<string, Scores>[] PinyinChoices = Array.Empty<KeyValuePair<string, Scores>>();
            internal void SealPinyin() => PinyinChoices = Texts.OrderByDescending(p => p.Value.ForContext(""))
                .ThenBy(p => p.Key, StringComparer.Ordinal).Take(16).ToArray();
        }

        private readonly Dictionary<string, Dictionary<string, ModeIndex>> _byCode = new(StringComparer.Ordinal);
        private string[] _codes = Array.Empty<string>();
        internal bool IsEmpty => _byCode.Count == 0;

        private const int MaximumCorrectionLevel = 10;

        private static int CorrectionLevel(double weight) =>
            Math.Clamp((int)Math.Floor(weight + 1e-12), 0, MaximumCorrectionLevel);

        private static double GeneralScore(double weight)
        {
            int level = CorrectionLevel(weight);
            return level == 0 ? 0 : 4 + 2 * level; // L1=6 ... L10=24.
        }

        private static double ExactScore(double weight)
        {
            int level = CorrectionLevel(weight);
            return level == 0 ? 0 : 7 + 2 * level; // Same-context protection: L1=9 ... L10=27.
        }

        internal static SentenceLearningSnapshot Build(IEnumerable<SentenceLearningEvent> events, long? at = null)
        {
            _ = at; // Learning is persistent; timestamps are journal metadata only.
            var groups = new Dictionary<(string Code, string Mode, string Context), Dictionary<string, Choice>>();
            foreach (var e in events)
            {
                if (SentenceLearning.LegacyPairMode(e.Mode) || e.Mode.Length == 0 || e.Mode.Length > 512 || e.Code.Length == 0 || e.Code.Length > 128 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2 || e.Levels < 1 || e.Levels > 3) continue;
                var key = (e.Code, Mode: SentenceLearning.EffectiveMode(e.Mode, e.Text), e.Context);
                if (!groups.TryGetValue(key, out var choices))
                    groups[key] = choices = new(StringComparer.Ordinal);

                // Only explicit manual corrections reach this journal. A competing
                // manual correction weakens the old choice in the same context;
                // elapsed wall-clock time never changes learning.
                foreach (var entry in choices)
                    if (entry.Key != e.Text) { entry.Value.Weight *= 0.25; entry.Value.Confirmed *= 0.25; }

                if (!choices.TryGetValue(e.Text, out var target))
                    choices[e.Text] = target = new Choice();
                target.Weight = Math.Min(MaximumCorrectionLevel, target.Weight + e.Levels);
                target.Confirmed = Math.Min(MaximumCorrectionLevel, target.Confirmed + 1);
            }
            if (groups.Count == 0) return Empty;

            var summaries = new Dictionary<(string Code, string Mode, string Text), Summary>();
            foreach (var group in groups)
            {
                foreach (var entry in group.Value)
                {
                    var key = (group.Key.Code, group.Key.Mode, entry.Key);
                    if (!summaries.TryGetValue(key, out var summary)) summaries[key] = summary = new();
                    summary.Scores.Exact[group.Key.Context] = ExactScore(entry.Value.Weight);
                    summary.Weight += entry.Value.Weight;
                    summary.Confirmed += entry.Value.Confirmed;
                    summary.Scores.Weights[group.Key.Context] = entry.Value.Weight;
                    summary.Scores.ConfidenceExact[group.Key.Context] = ExactScore(entry.Value.Confirmed);
                }
            }

            var snapshot = new SentenceLearningSnapshot();
            foreach (var entry in summaries)
            {
                var summary = entry.Value;
                // One explicit correction immediately creates a cross-context
                // fragment preference. Further levels require further explicit
                // corrections; ordinary/automatic top1 commits never add events.
                summary.Scores.General = GeneralScore(summary.Weight);
                summary.Scores.ConfidenceGeneral = GeneralScore(summary.Confirmed);
                if (!snapshot._byCode.TryGetValue(entry.Key.Code, out var modes))
                    snapshot._byCode[entry.Key.Code] = modes = new(StringComparer.Ordinal);
                if (!modes.TryGetValue(entry.Key.Mode, out var index)) modes[entry.Key.Mode] = index = new();
                index.Texts.Add(entry.Key.Text, summary.Scores);
            }
            foreach (var modes in snapshot._byCode.Values)
            {
                foreach (var index in modes.Values)
                {
                    index.SealPinyin();
                    foreach (var entry in index.Texts)
                    {
                        for (int length = 1; length < entry.Key.Length; length++)
                        {
                            string prefix = entry.Key.Substring(0, length);
                            if (!index.Prefixes.TryGetValue(prefix, out var scores)) index.Prefixes[prefix] = scores = new();
                            scores.Include(entry.Value);
                        }
                    }
                }
            }
            snapshot._codes = snapshot._byCode.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            return snapshot;
        }

        internal double PrefixScore(string mode, string code, string text, string context)
        {
            if (code.Length == 0 || text.Length == 0) return 0;
            int first = Array.BinarySearch(_codes, code, StringComparer.Ordinal);
            if (first < 0) first = ~first;
            double score = 0;
            // Preserve the original 64 CODE ROW limit, including rows skipped
            // for mode or equal-code length. Each row uses only indexed lookups,
            // independent of the number of matching texts or contexts.
            for (int i = first; i < _codes.Length && i - first < 64; i++)
            {
                string full = _codes[i];
                if (!full.StartsWith(code, StringComparison.Ordinal)) break;
                if (full.Length <= code.Length) continue;
                if (_byCode[full].TryGetValue(mode, out var index) && index.Prefixes.TryGetValue(text, out var scores))
                    score = Math.Max(score, scores.ForContext(context));
            }
            return score;
        }

        // At most 128 raw boundaries and 16 preferred fragments; no journal scan.
        internal (string Code, string Text, double Score)[] PinyinMatches(string mode, string raw)
        {
            var matches = new List<(string Code, string Text, double Score)>();
            for (int n = 1; n <= Math.Min(raw.Length, 128); n++)
            {
                string code = raw.Substring(0, n);
                if (_byCode.TryGetValue(code, out var modes) && modes.TryGetValue(mode, out var index))
                    foreach (var pair in index.PinyinChoices)
                        matches.Add((code, pair.Key, pair.Value.ForContext("")));
            }
            return matches.OrderByDescending(m => m.Score).ThenByDescending(m => m.Code.Length).ThenBy(m => m.Text, StringComparer.Ordinal).Take(16).ToArray();
        }

        internal (string Code, string Text, double Score)[] PinyinCharacterMatches(string raw)
        {
            if (!_byCode.TryGetValue(raw, out var modes) || !modes.TryGetValue(SentenceLearning.PinyinCharacterMode, out var index))
                return Array.Empty<(string Code, string Text, double Score)>();
            return index.PinyinChoices.Where(p => SentenceLearning.Characters(p.Key) == 1)
                .Select(p => (raw, p.Key, p.Value.ForContext(""))).ToArray();
        }

        internal double ConfidenceScore(string mode, string code, string text, string context)
        {
            if (!_byCode.TryGetValue(code, out var modes) || !modes.TryGetValue(mode, out var index) || !index.Texts.TryGetValue(text, out var scores)) return 0;
            return Math.Max(scores.ConfidenceGeneral, scores.ConfidenceExact.GetValueOrDefault(context));
        }

        internal double ProjectedScore(string mode, string code, string text, string context,
            IReadOnlyList<SentenceLearningEvent> events, int levels)
        {
            mode = SentenceLearning.EffectiveMode(mode, text);
            if (!events.Any(e => e.Code == code && SentenceLearning.EffectiveMode(e.Mode, e.Text) == mode)) return Score(mode, code, text, context);
            Scores scores = null;
            if (_byCode.TryGetValue(code, out var modes) && modes.TryGetValue(mode, out var index)) index.Texts.TryGetValue(text, out scores);
            var weights = scores == null ? new Dictionary<string, double>(StringComparer.Ordinal) : new Dictionary<string, double>(scores.Weights, StringComparer.Ordinal);
            foreach (var e in events)
            {
                if (e.Code != code || SentenceLearning.EffectiveMode(e.Mode, e.Text) != mode) continue;
                double weight = weights.GetValueOrDefault(e.Context);
                if (e.Text == text) weights[e.Context] = Math.Min(MaximumCorrectionLevel, weight + levels);
                else if (weights.ContainsKey(e.Context)) weights[e.Context] = weight * 0.25;
            }
            return Math.Max(GeneralScore(weights.Values.Sum()), ExactScore(weights.GetValueOrDefault(context)));
        }

        internal double Score(string mode, string code, string text, string context)
        {
            return _byCode.TryGetValue(code, out var modes) && modes.TryGetValue(mode, out var index) && index.Texts.TryGetValue(text, out var scores)
                ? scores.ForContext(context) : 0;
        }
    }
}
