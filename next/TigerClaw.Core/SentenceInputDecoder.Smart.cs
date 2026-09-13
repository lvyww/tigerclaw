using System;
using System.Collections.Generic;
using System.Linq;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceInputDecoder
    {
        internal int SmartMaxCodeLength { get; }
        internal int SmartSelectionMask { get; }

        private bool IsSmartCandidateEligible(SmartSentenceSegment segment, SentenceLexiconCandidate candidate)
        {
            if (segment.Rank != 0)
            {
                return candidate.Rank == segment.Rank;
            }
            return candidate.Rank == 1 || candidate.TextElements.Length <= 1 ||
                segment.CodeEnd - segment.Start >= SmartMaxCodeLength;
        }

        // Key-path validation only: no language-model scoring or Beam work.
        internal bool HasValidSmartSegments(string input)
        {
            string raw = (input ?? string.Empty).ToLowerInvariant();
            if (!SmartSentenceSegmentation.TryParse(raw, SmartMaxCodeLength, out var segments, SmartSelectionMask))
            {
                return false;
            }
            foreach (SmartSentenceSegment segment in segments)
            {
                var candidates = _lexicon.GetCandidates(raw.Substring(segment.Start, segment.CodeEnd - segment.Start));
                if (candidates == null || !candidates.Any(candidate => IsSmartCandidateEligible(segment, candidate)))
                {
                    return false;
                }
            }
            return true;
        }

        // Separate fixed-edge search. Ordinary sentence lattice/rank rules are
        // intentionally not used here; only the scoring and mass helpers are shared.
        private SentenceDecodeResult DecodeSmart(string input, int limit, bool evidence, string requiredPrefix)
        {
            _learningAffected = false;
            string raw = (input ?? string.Empty).ToLowerInvariant();
            var empty = new SentenceDecodeResult
            {
                RawCode = raw,
                Candidates = Array.Empty<SentenceCandidate>(),
                EarlyCommitEvidence = SentenceEarlyCommitEvidence.Empty
            };
            if (!SmartSentenceSegmentation.TryParse(raw, SmartMaxCodeLength, out var segments, SmartSelectionMask))
            {
                return empty;
            }

            var current = new List<BeamState>
            {
                new BeamState
                {
                    Text = string.Empty,
                    Previous2 = Bos,
                    Previous1 = Bos,
                    MaxLexiconRank = 1
                }
            };
            bool truncated = false;
            int expanded = 0;
            foreach (SmartSentenceSegment segment in segments)
            {
                var candidates = _lexicon.GetCandidates(raw.Substring(segment.Start, segment.CodeEnd - segment.Start));
                if (candidates == null)
                {
                    return empty;
                }
                var bucket = new BeamBucket();
                foreach (BeamState item in current)
                {
                    foreach (SentenceLexiconCandidate candidate in candidates)
                    {
                        if (!IsSmartCandidateEligible(segment, candidate))
                        {
                            continue;
                        }
                        double score = item.Score;
                        double supplementAdded = 0;
                        int supplementState = item.SupplementState;
                        string previous2 = item.Previous2, previous1 = item.Previous1;
                        foreach (string target in candidate.TextElements)
                        {
                            score += TransitionScore(previous2, previous1, target);
                            score += _emittedCharacterReward;
                            if (_hasSupplements)
                            {
                                supplementState = _supplementMatcher.Advance(supplementState, target, out double reward);
                                score += reward;
                                supplementAdded += reward;
                            }
                            previous2 = previous1;
                            previous1 = target;
                        }
                        if (segment.Rank == 0)
                        {
                            score -= _rankPenalty * candidate.LogRank;
                        }
                        double singleReward = segments.Count == 1 && segment.Rank == 0 &&
                            candidate.IsOptimalSingleCharacterCode && candidate.TextElements.Length == 1
                            ? _wholeInputSingleCharacterReward : 0;
                        score += singleReward;
                        string text = item.Text + candidate.Text;
                        // Prefix conditioning must be applied before pruning,
                        // otherwise already-committed text could disappear.
                        if (!string.IsNullOrEmpty(requiredPrefix) &&
                            !text.StartsWith(requiredPrefix, StringComparison.Ordinal) &&
                            !requiredPrefix.StartsWith(text, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        double learning = LearningReward(raw, text, segment.End, item, out double learningPotential);
                        double learningAdded = learning - item.LearningScore;
                        score += learningAdded;
                        bucket.Add(new BeamState
                        {
                            Score = score,
                            LogMass = item.LogMass + score - item.Score - supplementAdded - singleReward - learningAdded,
                            Text = text,
                            Previous2 = previous2,
                            Previous1 = previous1,
                            SupplementState = supplementState,
                            SupplementScore = item.SupplementScore + supplementAdded,
                            LearningScore = learning,
                            LearningPotential = learningPotential,
                            MaxLexiconRank = Math.Max(item.MaxLexiconRank, candidate.Rank),
                            Boundary = new SentencePathBoundary
                            {
                                Previous = item.Boundary,
                                TextLength = text.Length,
                                RawLength = segment.End,
                                LearningScore = learning
                            }
                        });
                        expanded++;
                    }
                }
                current = bucket.Limit(_beamWidth, CompareBeamStatesByScoreThenLexiconRank, out bool cut);
                truncated |= cut;
                if (current.Count == 0)
                {
                    return empty;
                }
            }
            SentenceCandidate[] visible = current.Select(EvaluateState)
                .Where(candidate => HasRequiredPrefix(candidate.Text, requiredPrefix))
                .OrderByDescending(candidate => candidate.FinalScore)
                .ThenBy(candidate => candidate.MaxLexiconRank)
                .ThenBy(candidate => candidate.Text, StringComparer.Ordinal)
                .Take(Math.Max(1, limit)).ToArray();
            string display = string.Join(" ", segments.Select(segment =>
                raw.Substring(segment.Start, segment.End - segment.Start).TrimEnd(' ')));
            if (raw.EndsWith(" ", StringComparison.Ordinal))
            {
                display += " ";
            }
            foreach (SentenceCandidate candidate in visible)
            {
                candidate.SegmentedCode = display;
            }

            SentenceEarlyCommitEvidence early = SentenceEarlyCommitEvidence.Empty;
            if (evidence && !_learningAffected)
            {
                var closed = new HashSet<int>(segments.Where(segment => segment.Closed).Select(segment => segment.End));
                var prefixes = BuildPrefixEvidence(visible).Where(prefix => closed.Contains(prefix.RawLength)).ToArray();
                early = new SentenceEarlyCommitEvidence
                {
                    Prefixes = prefixes,
                    ConfidenceTruncated = truncated,
                    NeutralLowConfidence = HasLowConfidenceCompletedGeneration(visible),
                    RawLengths = prefixes.GroupBy(prefix => prefix.Text).ToDictionary(group => group.Key, group => group.First().RawLength),
                    Proposal = string.Empty
                };
            }
            return new SentenceDecodeResult
            {
                RawCode = raw,
                Candidates = visible,
                EarlyCommitEvidence = early,
                LearningAffected = _learningAffected,
                LearningMode = _learningMode,
                ExpandedStates = expanded
            };
        }
    }
}
