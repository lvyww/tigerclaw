// Flat-scan oracle for the persistent manual-correction level policy.
// Its independent storage/query algorithm is retained; never used by Core.
using System;
using System.Collections.Generic;
using System.Linq;
using TigerClaw.Core;

namespace TigerClaw.Core.Tests
{
    internal sealed class SentenceLearningReference
    {
        internal static readonly SentenceLearningReference Empty = Build(Array.Empty<SentenceLearningEvent>());
        private sealed class Choice
        {
            public string Mode, Text, Context;
            public double Weight;
        }
        private readonly Dictionary<string, List<Choice>> _byCode = new(StringComparer.Ordinal);
        private string[] _codes = Array.Empty<string>();
        internal bool IsEmpty => _byCode.Count == 0;

        private static int Level(double weight) => Math.Clamp((int)Math.Floor(weight + 1e-12), 0, 10);
        private static double General(double weight) { int n = Level(weight); return n == 0 ? 0 : 4 + 2 * n; }
        private static double Exact(double weight) { int n = Level(weight); return n == 0 ? 0 : 7 + 2 * n; }

        internal static SentenceLearningReference Build(IEnumerable<SentenceLearningEvent> events, long? at = null)
        {
            _ = at;
            var snapshot = new SentenceLearningReference();
            foreach (var e in events)
            {
                if (e.Mode.Length == 0 || e.Mode.Length > 512 || e.Code.Length == 0 || e.Code.Length > 128 || !SentenceLearning.StaticText(e.Text) ||
                    (e.Context.Length > 0 && SentenceLearning.Characters(e.Context) == 0) || SentenceLearning.Characters(e.Context) > 2) continue;
                if (!snapshot._byCode.TryGetValue(e.Code, out var choices)) snapshot._byCode[e.Code] = choices = new();
                Choice target = null;
                foreach (var c in choices)
                {
                    if (c.Mode != e.Mode || c.Context != e.Context) continue;
                    if (c.Text == e.Text) target = c; else c.Weight *= 0.25;
                }
                if (target == null) { target = new Choice { Mode = e.Mode, Text = e.Text, Context = e.Context }; choices.Add(target); }
                target.Weight = Math.Min(10, target.Weight + 1);
            }
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
            double exact = 0, aggregate = 0;
            foreach (var c in choices)
            {
                if (c.Mode != mode || c.Text != text) continue;
                if (c.Context == context) exact = Exact(c.Weight);
                aggregate += c.Weight;
            }
            return Math.Max(exact, General(aggregate));
        }
    }
}
