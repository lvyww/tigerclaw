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

    }

    // Immutable query snapshot. Replay/aggregation happens once on the store
    // worker; candidate generation never scans events or context histories.
    internal sealed partial class SentenceLearningSnapshot
    {
        internal static readonly SentenceLearningSnapshot Empty = new();

        private sealed class Choice
        {
            internal double Weight;
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
        }

        private sealed class ModeIndex
        {
            internal readonly Dictionary<string, Scores> Texts = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, Scores> Prefixes = new(StringComparer.Ordinal);
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
                if (e.Mode.Length == 0 || e.Mode.Length > 512 || e.Code.Length == 0 || e.Code.Length > 128 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2) continue;
                var key = (e.Code, e.Mode, e.Context);
                if (!groups.TryGetValue(key, out var choices))
                    groups[key] = choices = new(StringComparer.Ordinal);

                // Only explicit manual corrections reach this journal. A competing
                // manual correction weakens the old choice in the same context;
                // elapsed wall-clock time never changes learning.
                foreach (var entry in choices)
                    if (entry.Key != e.Text) entry.Value.Weight *= 0.25;

                if (!choices.TryGetValue(e.Text, out var target))
                    choices[e.Text] = target = new Choice();
                target.Weight = Math.Min(MaximumCorrectionLevel, target.Weight + 1);
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
