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
        internal SentenceLearningEvent Copy() => (SentenceLearningEvent)MemberwiseClone();
    }

    internal static class SentenceLearning
    {
        internal static string ConfigurationHash(string text)
        {
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (char c in text ?? "") { hash ^= (byte)c; hash *= 1099511628211UL; hash ^= (byte)(c >> 8); hash *= 1099511628211UL; } }
            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }
        internal static bool IsReservedFile(string name) =>
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

        internal static SentenceLearningEvent[] Reinforce(
            string raw,
            SentenceCandidate selected,
            int floor,
            string mode,
            SentenceLearningSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(raw) || selected == null || string.IsNullOrEmpty(selected.Text) ||
                string.IsNullOrEmpty(mode) || snapshot == null || snapshot.IsEmpty)
            {
                return Array.Empty<SentenceLearningEvent>();
            }
            SortedDictionary<int, int> boundaries = Boundaries(selected, raw.Length);
            if (boundaries == null)
            {
                return Array.Empty<SentenceLearningEvent>();
            }
            KeyValuePair<int, int>[] points = boundaries.ToArray();
            var result = new List<SentenceLearningEvent>();
            for (int endIndex = 1; endIndex < points.Length; endIndex++)
            {
                int rawEnd = points[endIndex].Key;
                int textEnd = points[endIndex].Value;
                if (rawEnd <= floor)
                {
                    continue;
                }
                double bestScore = 0.0;
                int bestRawStart = -1, bestTextStart = -1;
                string bestText = null, bestContext = null;
                for (int startIndex = endIndex - 1; startIndex >= 0; startIndex--)
                {
                    int rawStart = points[startIndex].Key;
                    int textStart = points[startIndex].Value;
                    if (rawStart < floor)
                    {
                        continue;
                    }
                    string fragment = selected.Text.Substring(textStart, textEnd - textStart);
                    if (Characters(fragment) > 16)
                    {
                        break;
                    }
                    string code = raw.Substring(rawStart, rawEnd - rawStart).ToLowerInvariant();
                    string context = Context(selected.Text, textStart);
                    double score = snapshot.Score(mode, code, fragment, context);
                    if (score > bestScore + 1e-12)
                    {
                        bestScore = score;
                        bestRawStart = rawStart;
                        bestTextStart = textStart;
                        bestText = fragment;
                        bestContext = context;
                    }
                }
                // Once a preference is already near full maturity, avoid
                // appending another journal row on every normal top1 commit.
                // Natural decay can later bring it below this threshold, at
                // which point a fresh stable use reinforces it again.
                if (bestScore <= 0.0 || bestScore >= 11.0 || bestRawStart < 0)
                {
                    continue;
                }
                result.Add(new SentenceLearningEvent
                {
                    Mode = mode,
                    Code = raw.Substring(bestRawStart, rawEnd - bestRawStart).ToLowerInvariant(),
                    Text = bestText,
                    Context = bestContext,
                    RawStart = bestRawStart,
                    RawEnd = rawEnd,
                    TextStart = bestTextStart,
                    TextEnd = textEnd
                });
            }
            return result.ToArray();
        }
    }

    // Immutable query snapshot. Replay/aggregation happens once on the store
    // worker; candidate generation never scans events or context histories.
    internal sealed partial class SentenceLearningSnapshot
    {
        internal static readonly SentenceLearningSnapshot Empty = new();

        private sealed class Choice
        {
            internal double Weight;
            internal int Count;
            internal long Time;
        }

        private sealed class Scores
        {
            internal readonly Dictionary<string, double> Exact = new(StringComparer.Ordinal);
            internal double General;

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
            internal double Weight;
            internal int Count, KnownContexts;
        }

        private sealed class ModeIndex
        {
            internal readonly Dictionary<string, Scores> Texts = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, Scores> Prefixes = new(StringComparer.Ordinal);
        }

        private readonly Dictionary<string, Dictionary<string, ModeIndex>> _byCode = new(StringComparer.Ordinal);
        private string[] _codes = Array.Empty<string>();
        internal bool IsEmpty => _byCode.Count == 0;

        internal static SentenceLearningSnapshot Build(IEnumerable<SentenceLearningEvent> events, long? at = null)
        {
            long now = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Competitors share code, mode AND context. Unrelated contexts no
            // longer make journal replay quadratic for the same code/text.
            var groups = new Dictionary<(string Code, string Mode, string Context), Dictionary<string, Choice>>();
            foreach (var e in events)
            {
                if (e.Mode.Length == 0 || e.Mode.Length > 512 || e.Code.Length == 0 || e.Code.Length > 128 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2) continue;
                var key = (e.Code, e.Mode, e.Context);
                if (!groups.TryGetValue(key, out var choices))
                    groups[key] = choices = new(StringComparer.Ordinal);
                long time = Math.Min(now, e.Time);
                foreach (var entry in choices)
                {
                    var c = entry.Value;
                    c.Weight *= Math.Pow(2, -Math.Max(0, time - c.Time) / (30.0 * 86400));
                    c.Time = Math.Max(c.Time, time);
                    if (entry.Key != e.Text) c.Weight *= 0.25;
                }
                if (!choices.TryGetValue(e.Text, out var target))
                    choices[e.Text] = target = new Choice { Time = time };
                // One confirmation equals supplemental corpus weight 1000.
                // exp(3.5) is the weight where 9 + 2*ln(weight) reaches 16.
                target.Weight = Math.Min(Math.Exp(3.5), target.Weight + 1);
                target.Count = Math.Min(3, target.Count + 1);
            }
            if (groups.Count == 0) return Empty;

            var summaries = new Dictionary<(string Code, string Mode, string Text), Summary>();
            foreach (var group in groups)
            {
                foreach (var entry in group.Value)
                {
                    var c = entry.Value;
                    double weight = c.Weight * Math.Pow(2, -Math.Max(0, now - c.Time) / (30.0 * 86400));
                    var key = (group.Key.Code, group.Key.Mode, entry.Key);
                    if (!summaries.TryGetValue(key, out var summary)) summaries[key] = summary = new();
                    summary.Scores.Exact[group.Key.Context] = Math.Clamp(9 + 2 * Math.Log(Math.Max(0.001, weight)), 0, 16);
                    summary.Weight += weight;
                    summary.Count = Math.Min(3, summary.Count + c.Count);
                    // A context occurs once per summary. Empty is unknown, not
                    // sentence-start evidence; expired weak contexts do not count.
                    if (group.Key.Context.Length > 0 && weight >= 0.1) summary.KnownContexts++;
                }
            }

            var snapshot = new SentenceLearningSnapshot();
            foreach (var entry in summaries)
            {
                var summary = entry.Value;
                summary.Scores.General = SentenceLearning.Characters(entry.Key.Text) > 1 && summary.Count >= 3 && summary.KnownContexts >= 2
                    ? 2 * Math.Min(1, summary.Weight / 3) : 0;
                if (!snapshot._byCode.TryGetValue(entry.Key.Code, out var modes))
                    snapshot._byCode[entry.Key.Code] = modes = new(StringComparer.Ordinal);
                if (!modes.TryGetValue(entry.Key.Mode, out var index)) modes[entry.Key.Mode] = index = new();
                index.Texts.Add(entry.Key.Text, summary.Scores);
            }
            foreach (var modes in snapshot._byCode.Values)
            {
                foreach (var index in modes.Values)
                {
                    foreach (var entry in index.Texts)
                    {
                        // Only proper text prefixes: exact text is NOT a hint.
                        // UTF-16 prefixes preserve the previous ordinal contract.
                        // StaticText bounds this to at most 31 prefixes per text.
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

        internal double Score(string mode, string code, string text, string context)
        {
            return _byCode.TryGetValue(code, out var modes) && modes.TryGetValue(mode, out var index) && index.Texts.TryGetValue(text, out var scores)
                ? scores.ForContext(context) : 0;
        }
    }
}
