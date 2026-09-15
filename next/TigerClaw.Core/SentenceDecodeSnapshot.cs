using System;
using System.Collections.Generic;

namespace TigerClaw.Core
{
    internal interface ISentenceBoundaryLanguageModel : ISentenceLanguageModel
    {
        double LogProbability(string previous2, string previous1, string target, bool includeUnigram);
    }

    // Immutable confidence data, independent of the mutable display/Qwen menu.
    internal sealed class SentenceConfidenceCandidate
    {
        internal string Text { get; }
        internal double ConfidenceScore { get; }
        internal int MaxLexiconRank { get; }
        internal SentencePathBoundary Boundary { get; }

        internal SentenceConfidenceCandidate(SentenceCandidate candidate)
        {
            Text = candidate.Text;
            ConfidenceScore = candidate.ConfidenceScore;
            MaxLexiconRank = candidate.MaxLexiconRank;
            Boundary = candidate.Boundary;
        }
    }

    // Full ordinal equality resolves collisions without allocating each prefix.
    internal readonly struct SentencePrefixKey : IEquatable<SentencePrefixKey>
    {
        private readonly string _text;
        private readonly int _length;
        internal int RawLength { get; }

        internal SentencePrefixKey(string text, int length, int rawLength)
        {
            _text = text ?? string.Empty;
            _length = length;
            RawLength = rawLength;
        }

        internal string MaterializeText() => _text.Substring(0, _length);
        public bool Equals(SentencePrefixKey other) => RawLength == other.RawLength &&
            _length == other._length && _text.AsSpan(0, _length).SequenceEqual(other._text.AsSpan(0, other._length));
        public override bool Equals(object value) => value is SentencePrefixKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RawLength,
            string.GetHashCode(_text.AsSpan(0, _length), StringComparison.Ordinal));
    }

    internal sealed class SentenceEvidenceLookup
    {
        private readonly Dictionary<SentencePrefixKey, SentencePrefixEvidence> _prefixes = new();
        private readonly HashSet<SentencePrefixKey> _visible = new();

        internal SentenceEvidenceLookup(SentencePrefixEvidence[] prefixes, SentenceCandidate[] candidates)
        {
            foreach (var prefix in prefixes ?? Array.Empty<SentencePrefixEvidence>())
            {
                if (prefix == null || string.IsNullOrEmpty(prefix.Text)) continue;
                _prefixes.TryAdd(new SentencePrefixKey(prefix.Text, prefix.Text.Length, prefix.RawLength), prefix);
            }
            foreach (var candidate in candidates ?? Array.Empty<SentenceCandidate>())
            {
                if (string.IsNullOrEmpty(candidate?.Text)) continue;
                for (var boundary = candidate.Boundary; boundary != null; boundary = boundary.Previous)
                {
                    if (boundary.TextLength <= 0 || boundary.TextLength > candidate.Text.Length) continue;
                    _visible.Add(new SentencePrefixKey(candidate.Text, boundary.TextLength, boundary.RawLength));
                }
            }
        }

        internal SentencePrefixEvidence Find(string text, int rawLength) =>
            !string.IsNullOrEmpty(text) && _prefixes.TryGetValue(new SentencePrefixKey(text, text.Length, rawLength), out var value)
                ? value : null;
        internal bool IsVisible(SentencePrefixEvidence prefix) => prefix != null &&
            !string.IsNullOrEmpty(prefix.Text) && _visible.Contains(new SentencePrefixKey(prefix.Text, prefix.Text.Length, prefix.RawLength));
    }
}
