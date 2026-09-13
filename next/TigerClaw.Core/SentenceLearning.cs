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

        internal static string Context(string prefix)
        {
            int start = prefix.Length;
            for (int i = 0; i < 2 && start > 0; i++)
            {
                start--;
                if (char.IsLowSurrogate(prefix[start]) && start > 0) start--;
            }
            return prefix.Substring(start);
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
                        Context = Context(selected.Text.Substring(0, first)),
                        RawStart = previous, RawEnd = end, TextStart = first, TextEnd = last
                    });
                }
                previous = end;
            }
            return result;
        }
    }

    // Immutable query snapshot. Automatic use never calls Build with a new
    // event; only explicit Tab corrections acknowledged by TSF produce events.
    internal sealed class SentenceLearningSnapshot
    {
        internal static readonly SentenceLearningSnapshot Empty = Build(Array.Empty<SentenceLearningEvent>());
        private sealed class Choice
        {
            public string Mode, Text, Context;
            public double Weight;
            public int Count;
            public long Time;
        }
        private readonly Dictionary<string, List<Choice>> _byCode = new(StringComparer.Ordinal);
        private string[] _codes = Array.Empty<string>();
        internal bool IsEmpty => _byCode.Count == 0;
        internal static SentenceLearningSnapshot Build(IEnumerable<SentenceLearningEvent> events, long? at = null)
        {
            long now = at ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var snapshot = new SentenceLearningSnapshot();
            foreach (var e in events)
            {
                if (e.Mode.Length == 0 || e.Mode.Length > 512 || e.Code.Length == 0 || e.Code.Length > 128 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2) continue;
                if (!snapshot._byCode.TryGetValue(e.Code, out var choices)) snapshot._byCode[e.Code] = choices = new();
                Choice target = null; long time = Math.Min(now, e.Time);
                foreach (var c in choices)
                {
                    if (c.Mode != e.Mode || c.Context != e.Context) continue;
                    c.Weight *= Math.Pow(2, -Math.Max(0, time - c.Time) / (30.0 * 86400)); c.Time = Math.Max(c.Time, time);
                    if (c.Text == e.Text) target = c; else c.Weight *= 0.25;
                }
                if (target == null) { target = new Choice { Mode = e.Mode, Text = e.Text, Context = e.Context, Time = time }; choices.Add(target); }
                target.Weight = Math.Min(3, target.Weight + 1); target.Count = Math.Min(3, target.Count + 1);
            }
            foreach (var choices in snapshot._byCode.Values) foreach (var c in choices)
                c.Weight *= Math.Pow(2, -Math.Max(0, now - c.Time) / (30.0 * 86400));
            snapshot._codes = snapshot._byCode.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            return snapshot;
        }
        internal double PrefixScore(string mode, string code, string text, string context)
        {
            if (code.Length == 0 || text.Length == 0) return 0;
            int first = Array.BinarySearch(_codes, code, StringComparer.Ordinal); if (first < 0) first = ~first;
            double score = 0;
            for (int i = first; i < _codes.Length && i < first + 64; i++)
            {
                string full = _codes[i]; if (!full.StartsWith(code, StringComparison.Ordinal)) break;
                if (full.Length <= code.Length) continue;
                foreach (var c in _byCode[full])
                    if (c.Mode == mode && c.Text.Length > text.Length && c.Text.StartsWith(text, StringComparison.Ordinal))
                        score = Math.Max(score, Score(mode, full, c.Text, context));
            }
            return score;
        }
        internal double Score(string mode, string code, string text, string context)
        {
            if (!_byCode.TryGetValue(code, out var choices)) return 0;
            double exact = 0, aggregate = 0; int count = 0;
            var contexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in choices)
            {
                if (c.Mode != mode || c.Text != text) continue;
                if (c.Context == context) exact = Math.Min(10, 6 * Math.Min(1, c.Weight) + 2 * Math.Max(0, c.Weight - 1));
                aggregate += c.Weight; count += c.Count;
                if (c.Context.Length > 0 && c.Weight >= 0.1) contexts.Add(c.Context);
            }
            double general = SentenceLearning.Characters(text) > 1 && count >= 3 && contexts.Count >= 2 ? 2 * Math.Min(1, aggregate / 3) : 0;
            return Math.Max(exact, general);
        }
    }
}
