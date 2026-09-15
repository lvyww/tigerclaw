using System;
using System.Globalization;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed partial class InputMethodEngine
    {
        private bool GroupEligible(string text, int rank, SentencePathBoundary boundary, bool explicitRank)
        {
            return explicitRank || rank <= 1 || (_state.GetSentenceAllowDuplicateSingleCharacters() &&
                (boundary?.Previous != null || new StringInfo(text ?? string.Empty).LengthInTextElements == 1));
        }

        private SentenceCandidate GetEmptyCodeAutoCommitCandidate(out bool requiresUniquenessCheck)
        {
            requiresUniquenessCheck = false;
            string raw = _sentenceRawBuffer.ToString();
            if (_sentenceDecodeResult.LearningAffected || !IsSentenceEmptyCodeAutoCommitActive() ||
                _sentenceAutoCommitSuspended || _sentenceResultLexiconVersion != _state.LexiconVersion ||
                _sentenceDecodeResult.RawCode != raw) return null;
            var visible = _sentenceDecodeResult.Candidates ?? Array.Empty<SentenceCandidate>();
            var visibleTop = visible.FirstOrDefault(candidate => candidate != null);
            bool explicitRank = raw.Any(mark => char.IsDigit(mark) || mark == ';' || mark == '\'');
            var chosen = visible.FirstOrDefault(candidate => candidate != null &&
                GroupEligible(candidate.Text, candidate.MaxLexiconRank, candidate.Boundary, explicitRank));
            if (string.IsNullOrEmpty(chosen?.Text) || chosen.Text.Length <= _sentenceCommittedText.Length ||
                !chosen.Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal)) return null;
            var complete = _sentenceDecodeResult.ConfidenceCandidates ??
                visible.Where(candidate => candidate != null).Select(candidate => new SentenceConfidenceCandidate(candidate)).ToArray();
            var eligible = complete.Where(candidate => candidate != null &&
                candidate.Text.StartsWith(_sentenceCommittedText, StringComparison.Ordinal) &&
                GroupEligible(candidate.Text, candidate.MaxLexiconRank, candidate.Boundary, explicitRank)).ToArray();
            // Hidden entries compete but are never selected by this shortcut.
            if (!eligible.Any(candidate => candidate.Text == chosen.Text)) return null;
            if (eligible.Length > 1 && !IsStrongSentenceEmptyCodeCandidate(chosen, eligible, visibleTop)) return null;
            requiresUniquenessCheck = eligible.Length == 1;
            return chosen;
        }

        private bool IsStrongSentenceEmptyCodeCandidate(SentenceCandidate candidate,
            SentenceConfidenceCandidate[] eligibleCandidates, SentenceCandidate visibleTop)
        {
            var evidence = _sentenceDecodeResult.EarlyCommitEvidence ?? SentenceEarlyCommitEvidence.Empty;
            if (evidence.ConfidenceTruncated || evidence.Prefixes == null || evidence.Prefixes.Length == 0 ||
                candidate == null || visibleTop == null || candidate.Text != visibleTop.Text) return false;
            double maximum = eligibleCandidates.Max(item => item.ConfidenceScore);
            double total = 0.0, candidateMass = 0.0;
            foreach (var item in eligibleCandidates)
            {
                double mass = Math.Exp(item.ConfidenceScore - maximum);
                total += mass;
                if (item.Text == candidate.Text && item.Boundary?.RawLength == candidate.Boundary?.RawLength)
                    candidateMass += mass;
            }
            return total > 0.0 && candidateMass / total >= SentenceEarlyCommitStrongShare;
        }
    }
}
