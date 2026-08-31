using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace TigerClaw.Core
{
    internal interface ISentenceLanguageModel
    {
        double LogProbability(string previous2, string previous1, string target);

        bool HasObservedBigram(string previous, string target);
    }

    internal sealed class NeutralSentenceLanguageModel : ISentenceLanguageModel
    {
        public static readonly NeutralSentenceLanguageModel Instance = new NeutralSentenceLanguageModel();

        private NeutralSentenceLanguageModel()
        {
        }

        public double LogProbability(string previous2, string previous1, string target)
        {
            return 0.0;
        }

        public bool HasObservedBigram(string previous, string target)
        {
            return false;
        }
    }

    internal sealed class SentenceLexiconCandidate
    {
        public string Text { get; set; }
        public int Rank { get; set; }
        public double LogRank { get; set; }
        public string[] TextElements { get; set; }
    }

    internal sealed class SentenceLexiconIndex
    {
        private readonly Dictionary<string, SentenceLexiconCandidate[]> _candidatesByCode;
        private readonly int[] _codeLengths;
        private readonly HashSet<string> _properCodePrefixes;

        private SentenceLexiconIndex(
            Dictionary<string, SentenceLexiconCandidate[]> candidatesByCode,
            int[] codeLengths,
            HashSet<string> properCodePrefixes)
        {
            _candidatesByCode = candidatesByCode;
            _codeLengths = codeLengths;
            _properCodePrefixes = properCodePrefixes;
        }

        public IReadOnlyList<int> CodeLengths => _codeLengths;

        public SentenceLexiconCandidate[] GetCandidates(string code)
        {
            return code != null && _candidatesByCode.TryGetValue(code, out SentenceLexiconCandidate[] values)
                ? values
                : null;
        }

        public bool IsProperCodePrefix(string code)
        {
            return !string.IsNullOrEmpty(code) && _properCodePrefixes.Contains(code);
        }

        public static SentenceLexiconIndex Build(
            IDictionary<string, List<string>> source,
            ISet<string> commonCharacters = null)
        {
            ISet<string> common = commonCharacters ?? SentenceCommonCharacters.Top1500;
            var exact = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (KeyValuePair<string, List<string>> pair in source)
                {
                    string code = (pair.Key ?? string.Empty).Trim().ToLowerInvariant();
                    if (code.Length == 0 || pair.Value == null)
                    {
                        continue;
                    }

                    var values = new List<string>();
                    foreach (string rawText in pair.Value)
                    {
                        string text = rawText ?? string.Empty;
                        if (text.Length > 0 && !values.Contains(text))
                        {
                            values.Add(text);
                        }
                    }

                    if (values.Count > 0)
                    {
                        exact[code] = values;
                    }
                }
            }

            var codesByCharacter = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> pair in exact)
            {
                foreach (string text in pair.Value)
                {
                    if (!IsSingleTextElement(text))
                    {
                        continue;
                    }

                    if (!codesByCharacter.TryGetValue(text, out List<string> codes))
                    {
                        codes = new List<string>();
                        codesByCharacter[text] = codes;
                    }

                    if (!codes.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        codes.Add(pair.Key);
                    }
                }
            }

            var primaryBaseCodeByCharacter = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> pair in codesByCharacter)
            {
                string chosen = ChoosePrimaryCode(pair.Key, pair.Value, exact, minimumLength: 2);
                if (!string.IsNullOrEmpty(chosen))
                {
                    primaryBaseCodeByCharacter[pair.Key] = chosen;
                }
            }

            var filtered = new Dictionary<string, SentenceLexiconCandidate[]>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<string>> pair in exact)
            {
                var allowed = new List<SentenceLexiconCandidate>();
                for (int index = 0; index < pair.Value.Count; index++)
                {
                    string text = pair.Value[index];
                    bool allowNonPrimary =
                        pair.Key.Length == 1 ||
                        !IsSingleTextElement(text) ||
                        !IsCommonSingleCharacter(text, common);
                    if (allowNonPrimary ||
                        (primaryBaseCodeByCharacter.TryGetValue(text, out string primaryCode) &&
                         string.Equals(primaryCode, pair.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        allowed.Add(new SentenceLexiconCandidate
                        {
                            Text = text,
                            Rank = index + 1,
                            LogRank = Math.Log(index + 1.0),
                            TextElements = SplitTextElements(text)
                        });
                    }
                }

                if (allowed.Count > 0)
                {
                    filtered[pair.Key] = allowed.ToArray();
                }
            }

            var properCodePrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string code in filtered.Keys)
            {
                for (int length = 1; length < code.Length; length++)
                {
                    properCodePrefixes.Add(code.Substring(0, length));
                }
            }

            return new SentenceLexiconIndex(
                filtered,
                filtered.Keys.Select(code => code.Length).Distinct().OrderBy(length => length).ToArray(),
                properCodePrefixes);
        }

        private static string ChoosePrimaryCode(
            string character,
            IEnumerable<string> codes,
            IDictionary<string, List<string>> exact,
            int minimumLength)
        {
            string bestFirst = null;
            string bestAny = null;
            foreach (string code in codes)
            {
                if (string.IsNullOrEmpty(code) || code.Length < minimumLength || !exact.TryGetValue(code, out List<string> candidates))
                {
                    continue;
                }

                bestAny = ChooseShorter(bestAny, code);
                if (candidates.Count > 0 && string.Equals(candidates[0], character, StringComparison.Ordinal))
                {
                    bestFirst = ChooseShorter(bestFirst, code);
                }
            }

            return bestFirst ?? bestAny;
        }

        private static string ChooseShorter(string current, string candidate)
        {
            if (string.IsNullOrEmpty(current) ||
                candidate.Length < current.Length)
            {
                return candidate;
            }

            // `source` is built in code-table priority order. For equal-length
            // codes, retain the first code from that order instead of choosing
            // lexicographically; otherwise an alternate code such as `ladc`
            // can incorrectly displace the higher-priority `ldac` for 燕.
            return current;
        }

        private static bool IsCommonSingleCharacter(string text, ISet<string> commonCharacters)
        {
            return commonCharacters != null &&
                   commonCharacters.Count > 0 &&
                   commonCharacters.Contains(text);
        }

        private static bool IsSingleTextElement(string text)
        {
            return !string.IsNullOrEmpty(text) && new StringInfo(text).LengthInTextElements == 1;
        }

        private static string[] SplitTextElements(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<string>();
            }

            var elements = new List<string>();
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                elements.Add(enumerator.GetTextElement());
            }
            return elements.ToArray();
        }
    }

    internal sealed class SentenceCandidate
    {
        public string Text { get; set; }
        public string SegmentedCode { get; set; }
        public double BaseScore { get; set; }
        public double FinalScore { get; set; }
        public double ConfidenceScore { get; set; }
        public double SupplementScore { get; set; }
        public int MaxLexiconRank { get; set; }
        public SentencePathBoundary Boundary { get; set; }

        public static int CompareByLexiconRankThenScore(SentenceCandidate left, SentenceCandidate right)
        {
            int rank = left.MaxLexiconRank.CompareTo(right.MaxLexiconRank);
            if (rank != 0)
            {
                return rank;
            }

            int score = right.FinalScore.CompareTo(left.FinalScore);
            return score != 0 ? score : string.CompareOrdinal(left.Text, right.Text);
        }
    }

    internal sealed class SentencePathBoundary
    {
        public SentencePathBoundary Previous { get; set; }
        public int TextLength { get; set; }
        public int RawLength { get; set; }
    }

    internal sealed class SentenceDecodeResult
    {
        public static readonly SentenceDecodeResult Empty = new SentenceDecodeResult
        {
            RawCode = string.Empty,
            Candidates = Array.Empty<SentenceCandidate>(),
            EarlyCommitEvidence = SentenceEarlyCommitEvidence.Empty,
            ExpandedStates = 0
        };

        public string RawCode { get; set; }
        public SentenceCandidate[] Candidates { get; set; }
        public SentenceEarlyCommitEvidence EarlyCommitEvidence { get; set; }
        public int ExpandedStates { get; set; }
    }

    internal sealed class SentenceEarlyCommitEvidence
    {
        public static readonly SentenceEarlyCommitEvidence Empty = new SentenceEarlyCommitEvidence
        {
            Proposal = string.Empty,
            ProposalShare = 0.0,
            RawLengths = new Dictionary<string, int>(StringComparer.Ordinal),
            ConfidenceTruncated = false,
            IgnoreNeuralConstraint = false
        };

        public string Proposal { get; set; }
        public double ProposalShare { get; set; }
        public Dictionary<string, int> RawLengths { get; set; }
        public bool ConfidenceTruncated { get; set; }
        public bool IgnoreNeuralConstraint { get; set; }
    }

    internal sealed class SentenceDecodePerformanceSample
    {
        public int DecodeCalls { get; set; }
        public double DecodeTotalMilliseconds { get; set; }
        public double DecodeMaximumMilliseconds { get; set; }
        public long IsolationCacheHits { get; set; }
        public long IsolationCacheMisses { get; set; }
    }

    internal sealed class SentenceInputDecoder
    {
        private const string Bos = "\x02";
        private const string Eos = "\x03";
        private const int IsolationPenaltyCacheCapacity = 8192;
        private readonly SentenceLexiconIndex _lexicon;
        private readonly ISentenceLanguageModel _languageModel;
        private readonly int _beamWidth;
        private readonly double _rankPenalty;
        private readonly SentenceIsolationPenalty _isolationPenalty;
        private readonly bool _scoreSentenceBoundaries;
        private readonly double _emittedCharacterReward;
        private readonly SentenceSupplementMatcher _supplementMatcher;
        private readonly bool _hasSupplements;
        private readonly int _maxCodeLength;
        private readonly object _decodeLock = new object();
        private string _cachedRaw;
        private BeamBucket[] _cachedStates;
        private SentenceDecodeResult _cachedResult;
        private int _cachedLimit;
        private bool _cachedIncludesEarlyCommitEvidence;
        private string _cachedRequiredTextPrefix = string.Empty;
        private readonly Dictionary<string, double> _isolationPenaltyCache;
        private readonly string[] _isolationPenaltyCacheKeys;
        private int _isolationPenaltyCacheNext;
        private int _performanceDecodeCalls;
        private long _performanceDecodeTotalTicks;
        private long _performanceDecodeMaximumTicks;
        private long _isolationPenaltyCacheHits;
        private long _isolationPenaltyCacheMisses;
        private long _performanceIsolationCacheHits;
        private long _performanceIsolationCacheMisses;
        private SentenceDecodePerformanceSample _lastPerformanceSample;

        private sealed class BeamState
        {
            public double Score;
            public double LogMass;
            public string Text;
            public string Previous2;
            public string Previous1;
            public int SupplementState;
            public double SupplementScore;
            public int MaxLexiconRank;
            public SentencePathBoundary Boundary;
        }

        private sealed class EarlyCommitCandidate
        {
            public SentenceCandidate Candidate;
            public double ConfidenceScore;
        }

        private sealed class BeamBucket
        {
            private const int AggregateDuringExpansionThreshold = 256;
            private List<BeamState> _pending = new List<BeamState>();
            private Dictionary<string, BeamState> _bestByText;
            private bool _wasTruncated;
            private bool _isFrozen;

            public void Add(BeamState item)
            {
                if (item == null)
                {
                    return;
                }

                if (_isFrozen)
                {
                    _isFrozen = false;
                    EnsureAggregated();
                }

                if (_bestByText == null)
                {
                    _pending.Add(item);
                    if (_pending.Count < AggregateDuringExpansionThreshold)
                    {
                        return;
                    }

                    EnsureAggregated();
                    return;
                }

                AddAggregated(item);
            }

            public List<BeamState> Limit(int limit, out bool truncated)
            {
                if (_isFrozen)
                {
                    truncated = _wasTruncated;
                    return _pending;
                }

                EnsureAggregated();
                int boundedLimit = Math.Max(1, limit);
                var values = _bestByText.Values.ToList();
                bool truncatedNow = values.Count > boundedLimit;
                truncated = _wasTruncated || truncatedNow;
                _wasTruncated = truncated;
                if (truncatedNow)
                {
                    values = SelectExactTop(values, boundedLimit, CompareBeamStates);
                }
                else
                {
                    values.Sort(CompareBeamStates);
                }

                // The lattice cache lives for the whole composition. Freeze a
                // processed position back to a compact list so dictionaries do
                // not remain resident at every raw-code offset. A later
                // incremental rebuild will aggregate it again only if needed.
                _pending = values;
                _bestByText = null;
                _isFrozen = true;
                return values;
            }

            private void EnsureAggregated()
            {
                if (_bestByText != null)
                {
                    return;
                }

                _bestByText = new Dictionary<string, BeamState>(
                    Math.Max(1, _pending.Count),
                    StringComparer.Ordinal);
                foreach (BeamState item in _pending)
                {
                    AddAggregated(item);
                }
                _pending = null;
            }

            private void AddAggregated(BeamState item)
            {
                if (!_bestByText.TryGetValue(item.Text, out BeamState previous))
                {
                    _bestByText[item.Text] = item;
                    return;
                }

                double combinedMass = LogSumExp(previous.LogMass, item.LogMass);
                if (IsBetterDuplicate(item, previous))
                {
                    item.LogMass = combinedMass;
                    _bestByText[item.Text] = item;
                }
                else
                {
                    previous.LogMass = combinedMass;
                }
            }
        }

        public SentenceInputDecoder(
            SentenceLexiconIndex lexicon,
            ISentenceLanguageModel languageModel,
            int beamWidth = 2000,
            double rankPenalty = 0.03,
            SentenceIsolationPenalty isolationPenalty = null,
            bool scoreSentenceBoundaries = true,
            double emittedCharacterReward = 0.0,
            SentenceSupplementMatcher supplementMatcher = null)
        {
            _lexicon = lexicon ?? throw new ArgumentNullException(nameof(lexicon));
            _languageModel = languageModel ?? NeutralSentenceLanguageModel.Instance;
            _beamWidth = Math.Max(1, beamWidth);
            _rankPenalty = Math.Max(0.0, rankPenalty);
            _isolationPenalty = isolationPenalty ?? SentenceIsolationPenalty.CreateDefault();
            if (_isolationPenalty.Enabled)
            {
                _isolationPenaltyCache = new Dictionary<string, double>(
                    IsolationPenaltyCacheCapacity,
                    StringComparer.Ordinal);
                _isolationPenaltyCacheKeys = new string[IsolationPenaltyCacheCapacity];
            }
            _scoreSentenceBoundaries = scoreSentenceBoundaries;
            _emittedCharacterReward = Math.Max(0.0, emittedCharacterReward);
            _supplementMatcher = supplementMatcher ?? SentenceSupplementMatcher.Empty;
            _hasSupplements = !_supplementMatcher.IsEmpty;
            int maxCodeLength = 1;
            foreach (int length in _lexicon.CodeLengths)
            {
                if (length > maxCodeLength)
                {
                    maxCodeLength = length;
                }
            }

            _maxCodeLength = maxCodeLength;
        }

        public SentenceDecodeResult Decode(
            string rawCode,
            int candidateLimit = 20,
            bool includeEarlyCommitEvidence = false,
            string requiredTextPrefix = null)
        {
            lock (_decodeLock)
            {
                long started = Stopwatch.GetTimestamp();
                long isolationHitsBefore = _isolationPenaltyCacheHits;
                long isolationMissesBefore = _isolationPenaltyCacheMisses;
                SentenceDecodeResult result = DecodeIncrementalLocked(
                    rawCode,
                    candidateLimit,
                    includeEarlyCommitEvidence,
                    requiredTextPrefix);
                RecordDecodePerformance(
                    Stopwatch.GetTimestamp() - started,
                    _isolationPenaltyCacheHits - isolationHitsBefore,
                    _isolationPenaltyCacheMisses - isolationMissesBefore);
                return result;
            }
        }

        internal SentenceDecodeResult DecodeFull(
            string rawCode,
            int candidateLimit = 20,
            bool includeEarlyCommitEvidence = false,
            string requiredTextPrefix = null)
        {
            string normalized = NormalizeRawCode(rawCode);
            if (normalized.Length == 0 || !normalized.Any(char.IsLetter))
            {
                return SentenceDecodeResult.Empty;
            }

            BeamBucket[] states = CreateStates(normalized.Length);
            int expanded = ExpandRange(normalized, states, 0, normalized.Length);
            return Emit(
                normalized,
                states,
                candidateLimit,
                expanded,
                includeEarlyCommitEvidence,
                requiredTextPrefix);
        }

        internal void ResetDecodeCache()
        {
            lock (_decodeLock)
            {
                ClearCache();
            }
        }

        internal void CompleteComposition()
        {
            // Completion can happen while the asynchronous Beam worker still
            // owns the decoder. Diagnostics must never put that work back on
            // the TSF key path, so a busy decoder simply carries its aggregate
            // into the next completion sample.
            if (!Monitor.TryEnter(_decodeLock))
            {
                return;
            }

            try
            {
                FinishPerformanceSample();
            }
            finally
            {
                Monitor.Exit(_decodeLock);
            }
        }

        internal SentenceDecodePerformanceSample GetCurrentPerformanceSample()
        {
            lock (_decodeLock)
            {
                return CreatePerformanceSample();
            }
        }

        internal SentenceDecodePerformanceSample GetLastPerformanceSample()
        {
            lock (_decodeLock)
            {
                return CopyPerformanceSample(_lastPerformanceSample);
            }
        }

        internal bool HasCompleteCandidate(string rawCode, string requiredTextPrefix = null)
        {
            string normalized = NormalizeRawCode(rawCode);
            if (normalized.Length == 0 || !normalized.Any(char.IsLetter))
            {
                return false;
            }

            string required = requiredTextPrefix ?? string.Empty;
            var states = new HashSet<int>[normalized.Length + 1];
            for (int index = 0; index < states.Length; index++)
            {
                states[index] = new HashSet<int>();
            }
            states[0].Add(0);

            for (int position = 0; position < normalized.Length; position++)
            {
                if (states[position].Count == 0)
                {
                    continue;
                }

                foreach (int codeLength in _lexicon.CodeLengths)
                {
                    int codeEnd = position + codeLength;
                    if (codeEnd > normalized.Length ||
                        (position > 0 && IsShortSymbolCode(normalized[position])))
                    {
                        continue;
                    }

                    SentenceLexiconCandidate[] candidates =
                        _lexicon.GetCandidates(normalized.Substring(position, codeLength));
                    if (candidates == null || candidates.Length == 0)
                    {
                        continue;
                    }

                    int consumedEnd = ReadCodeSuffix(normalized, codeEnd, out int selectedRank);
                    bool wholeInputEdge = position == 0 && consumedEnd == normalized.Length;
                    if (normalized.Length > 1 && consumedEnd - position < 2)
                    {
                        continue;
                    }

                    foreach (int matchedPrefixLength in states[position])
                    {
                        foreach (SentenceLexiconCandidate candidate in candidates)
                        {
                            bool rankMatches = selectedRank > 0
                                ? candidate.Rank == selectedRank
                                : wholeInputEdge || candidate.Rank == 1;
                            if (!rankMatches || !TryAdvanceRequiredPrefix(
                                required,
                                matchedPrefixLength,
                                candidate.Text,
                                out int nextMatchedPrefixLength))
                            {
                                continue;
                            }

                            states[consumedEnd].Add(nextMatchedPrefixLength);
                        }
                    }
                }
            }

            return states[normalized.Length].Contains(required.Length);
        }

        internal bool IsProperCodePrefix(string code)
        {
            return _lexicon.IsProperCodePrefix(NormalizeRawCode(code));
        }

        private static bool TryAdvanceRequiredPrefix(
            string required,
            int matchedLength,
            string text,
            out int nextMatchedLength)
        {
            nextMatchedLength = matchedLength;
            if (matchedLength >= required.Length)
            {
                return true;
            }

            string candidateText = text ?? string.Empty;
            int compareLength = Math.Min(candidateText.Length, required.Length - matchedLength);
            if (compareLength == 0 || string.CompareOrdinal(
                required,
                matchedLength,
                candidateText,
                0,
                compareLength) != 0)
            {
                return false;
            }

            nextMatchedLength = Math.Min(required.Length, matchedLength + candidateText.Length);
            return true;
        }

        private SentenceDecodeResult DecodeIncrementalLocked(
            string rawCode,
            int candidateLimit,
            bool includeEarlyCommitEvidence,
            string requiredTextPrefix)
        {
            string normalized = NormalizeRawCode(rawCode);
            string normalizedRequiredPrefix = requiredTextPrefix ?? string.Empty;
            if (normalized.Length == 0 || !normalized.Any(char.IsLetter))
            {
                ClearCache();
                _cachedRaw = normalized ?? string.Empty;
                _cachedResult = SentenceDecodeResult.Empty;
                _cachedLimit = candidateLimit;
                _cachedIncludesEarlyCommitEvidence = includeEarlyCommitEvidence;
                _cachedRequiredTextPrefix = normalizedRequiredPrefix;
                return _cachedResult;
            }

            if (_cachedRaw == normalized &&
                _cachedResult != null &&
                _cachedLimit == candidateLimit &&
                _cachedIncludesEarlyCommitEvidence == includeEarlyCommitEvidence &&
                string.Equals(
                    _cachedRequiredTextPrefix,
                    normalizedRequiredPrefix,
                    StringComparison.Ordinal))
            {
                return _cachedResult;
            }

            if (_cachedRaw == normalized && _cachedStates != null)
            {
                SentenceDecodeResult trimmed = Emit(
                    normalized,
                    _cachedStates,
                    candidateLimit,
                    0,
                    includeEarlyCommitEvidence,
                    normalizedRequiredPrefix);
                _cachedResult = trimmed;
                _cachedLimit = candidateLimit;
                _cachedIncludesEarlyCommitEvidence = includeEarlyCommitEvidence;
                _cachedRequiredTextPrefix = normalizedRequiredPrefix;
                return trimmed;
            }

            int length = normalized.Length;
            BeamBucket[] states = null;
            int expanded = 0;
            string oldRaw = _cachedRaw;
            BeamBucket[] oldStates = _cachedStates;
            if (oldStates != null && !string.IsNullOrEmpty(oldRaw))
            {
                int oldLength = oldRaw.Length;
                // Whole-input one-key edges and implicit non-first ranks may
                // become segmented after an append. Rebuild the small
                // four-code prefix so formerly legal states cannot leak.
                if (oldLength <= 4 || length <= 4)
                {
                    states = null;
                }
                else if (length > oldLength && normalized.StartsWith(oldRaw, StringComparison.Ordinal))
                {
                    int maxConsume = _maxCodeLength + TrailingSelectorSpan(normalized);
                    int fromPos = Math.Max(0, oldLength + 1 - maxConsume);
                    states = ResizeStates(oldStates, length);
                    for (int index = oldLength + 1; index <= length; index++)
                    {
                        states[index] = new BeamBucket();
                    }

                    expanded = ExpandRange(
                        normalized,
                        states,
                        fromPos,
                        length,
                        minimumConsumedEndExclusive: oldLength);
                }
                else if (length < oldLength && oldRaw.StartsWith(normalized, StringComparison.Ordinal))
                {
                    states = oldStates;
                    for (int index = length + 1; index <= oldLength && index < states.Length; index++)
                    {
                        states[index] = null;
                    }
                }
            }

            if (states == null)
            {
                states = CreateStates(length);
                expanded = ExpandRange(normalized, states, 0, length);
            }

            SentenceDecodeResult result = Emit(
                normalized,
                states,
                candidateLimit,
                expanded,
                includeEarlyCommitEvidence,
                normalizedRequiredPrefix);
            _cachedRaw = normalized;
            _cachedStates = states;
            _cachedResult = result;
            _cachedLimit = candidateLimit;
            _cachedIncludesEarlyCommitEvidence = includeEarlyCommitEvidence;
            _cachedRequiredTextPrefix = normalizedRequiredPrefix;
            return result;
        }

        private void ClearCache()
        {
            _cachedRaw = null;
            _cachedStates = null;
            _cachedResult = null;
            _cachedLimit = 0;
            _cachedIncludesEarlyCommitEvidence = false;
            _cachedRequiredTextPrefix = string.Empty;
        }

        private double TransitionScore(string previous2, string previous1, string target)
        {
            bool isBoundary =
                string.Equals(previous2, Bos, StringComparison.Ordinal) ||
                string.Equals(previous1, Bos, StringComparison.Ordinal) ||
                string.Equals(target, Eos, StringComparison.Ordinal);
            if (!_scoreSentenceBoundaries && isBoundary)
            {
                var ngram = _languageModel as SentenceNgramModel;
                if (ngram != null)
                {
                    return ngram.LogProbability(previous2, previous1, target, includeUnigram: false);
                }
            }

            return _languageModel.LogProbability(previous2, previous1, target);
        }

        private static BeamBucket[] CreateStates(int length)
        {
            var states = new BeamBucket[length + 1];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new BeamBucket();
            }

            states[0].Add(new BeamState
            {
                Score = 0.0,
                LogMass = 0.0,
                Text = string.Empty,
                Previous2 = Bos,
                Previous1 = Bos,
                MaxLexiconRank = 1
            });
            return states;
        }

        private static BeamBucket[] ResizeStates(BeamBucket[] states, int length)
        {
            if (states.Length >= length + 1)
            {
                return states;
            }

            var resized = new BeamBucket[length + 1];
            Array.Copy(states, resized, states.Length);
            return resized;
        }

        private static int TrailingSelectorSpan(string raw)
        {
            int index = raw.Length - 1;
            while (index >= 0)
            {
                char mark = raw[index];
                if (char.IsDigit(mark) || mark == ';' || mark == '\'')
                {
                    index--;
                }
                else
                {
                    break;
                }
            }

            return raw.Length - 1 - index;
        }

        private int ExpandRange(
            string raw,
            BeamBucket[] states,
            int fromPos,
            int length,
            int minimumConsumedEndExclusive = -1)
        {
            int expandedStates = 0;
            for (int position = fromPos; position < length; position++)
            {
                List<BeamState> current = states[position].Limit(_beamWidth, out _);
                if (current.Count == 0)
                {
                    continue;
                }

                foreach (int codeLength in _lexicon.CodeLengths)
                {
                    int codeEnd = position + codeLength;
                    if (codeEnd > length)
                    {
                        continue;
                    }

                    // Codes beginning with the short-symbol markers are valid
                    // sentence entries only at the beginning of the sentence.
                    // In the middle of a sentence these entries would otherwise
                    // become ordinary segmentation paths, even though the
                    // corresponding shortcuts are intended as leading codes.
                    if (position > 0 && IsShortSymbolCode(raw[position]))
                    {
                        continue;
                    }

                    SentenceLexiconCandidate[] candidates = _lexicon.GetCandidates(raw.Substring(position, codeLength));
                    if (candidates == null || candidates.Length == 0)
                    {
                        continue;
                    }

                    int selectedRank;
                    int consumedEnd = ReadCodeSuffix(raw, codeEnd, out selectedRank);
                    bool wholeInputEdge = position == 0 && consumedEnd == length;
                    if (consumedEnd <= minimumConsumedEndExclusive)
                    {
                        continue;
                    }
                    if (length > 1 && consumedEnd - position < 2)
                    {
                        continue;
                    }

                    foreach (BeamState item in current)
                    {
                        foreach (SentenceLexiconCandidate candidate in candidates)
                        {
                            bool rankMatches = selectedRank > 0
                                ? candidate.Rank == selectedRank
                                : wholeInputEdge || candidate.Rank == 1;
                            if (!rankMatches)
                            {
                                continue;
                            }

                            double score = item.Score;
                            double supplementAdded = 0.0;
                            int supplementState = item.SupplementState;
                            string previous2 = item.Previous2;
                            string previous1 = item.Previous1;
                            string[] textElements = candidate.TextElements;
                            for (int elementIndex = 0; elementIndex < textElements.Length; elementIndex++)
                            {
                                string target = textElements[elementIndex];
                                score += TransitionScore(previous2, previous1, target);
                                score += _emittedCharacterReward;
                                if (_hasSupplements)
                                {
                                    supplementState = _supplementMatcher.Advance(
                                        supplementState,
                                        target,
                                        out double supplementReward);
                                    score += supplementReward;
                                    supplementAdded += supplementReward;
                                }
                                previous2 = previous1;
                                previous1 = target;
                            }

                            if (selectedRank == 0)
                            {
                                score -= _rankPenalty * candidate.LogRank;
                            }

                            states[consumedEnd].Add(new BeamState
                            {
                                Score = score,
                                LogMass = item.LogMass + (score - item.Score - supplementAdded),
                                Text = item.Text + candidate.Text,
                                Previous2 = previous2,
                                Previous1 = previous1,
                                SupplementState = supplementState,
                                SupplementScore = item.SupplementScore + supplementAdded,
                                MaxLexiconRank = Math.Max(item.MaxLexiconRank, candidate.Rank),
                                Boundary = new SentencePathBoundary
                                {
                                    Previous = item.Boundary,
                                    TextLength = item.Text.Length + candidate.Text.Length,
                                    RawLength = consumedEnd
                                }
                            });
                            expandedStates++;
                        }
                    }
                }
            }

            return expandedStates;
        }

        private int ReadCodeSuffix(string raw, int codeEnd, out int selectedRank)
        {
            selectedRank = 0;
            if (codeEnd >= raw.Length)
            {
                return codeEnd;
            }

            char mark = raw[codeEnd];
            if (mark == ';')
            {
                selectedRank = 2;
                return codeEnd + 1;
            }

            if (mark == '\'')
            {
                selectedRank = 3;
                return codeEnd + 1;
            }

            if (char.IsDigit(mark))
            {
                int digitEnd = codeEnd;
                while (digitEnd < raw.Length && char.IsDigit(raw[digitEnd]))
                {
                    digitEnd++;
                }

                string token = raw.Substring(codeEnd, digitEnd - codeEnd);
                selectedRank = token == "0"
                    ? 10
                    : int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
                return digitEnd;
            }

            return codeEnd;
        }

        private static bool IsShortSymbolCode(char value)
        {
            return value == ';' || value == '/' || value == '[';
        }

        private SentenceDecodeResult Emit(
            string normalized,
            BeamBucket[] states,
            int candidateLimit,
            int expandedStates,
            bool includeEarlyCommitEvidence,
            string requiredTextPrefix)
        {
            List<BeamState> completed = states[normalized.Length].Limit(
                _beamWidth,
                out bool confidenceTruncated);
            var result = new List<SentenceCandidate>(completed.Count);
            foreach (BeamState item in completed)
            {
                result.Add(EvaluateState(item));
            }

            SentenceCandidate[] visible = SelectExactTopCandidates(
                result,
                Math.Max(1, candidateLimit));
            foreach (SentenceCandidate candidate in visible)
            {
                candidate.SegmentedCode = BuildSegmentedCode(normalized, candidate.Boundary);
            }

            SentenceEarlyCommitEvidence earlyCommitEvidence = SentenceEarlyCommitEvidence.Empty;
            if (includeEarlyCommitEvidence)
            {
                // The full-code path only needs the already-selected visible
                // candidates; widening to the full beam-limited result only
                // matters once an incomplete tail is merged in below, so
                // defer that cost instead of paying it on every keystroke.
                earlyCommitEvidence = BuildEarlyCommitEvidence(
                    normalized,
                    states,
                    visible,
                    result,
                    confidenceTruncated,
                    requiredTextPrefix);
            }

            return new SentenceDecodeResult
            {
                RawCode = normalized,
                Candidates = visible,
                EarlyCommitEvidence = earlyCommitEvidence,
                ExpandedStates = expandedStates
            };
        }

        private SentenceCandidate EvaluateState(BeamState item)
        {
            double endingAdjustment = TransitionScore(item.Previous2, item.Previous1, Eos) -
                ApplyIsolationPenalty(item.Text);
            double score = item.Score + endingAdjustment;
            return new SentenceCandidate
            {
                Text = item.Text,
                BaseScore = score,
                FinalScore = score,
                ConfidenceScore = item.LogMass + endingAdjustment,
                SupplementScore = item.SupplementScore,
                Boundary = item.Boundary,
                MaxLexiconRank = Math.Max(1, item.MaxLexiconRank)
            };
        }

        private double ApplyIsolationPenalty(string text)
        {
            if (!_isolationPenalty.Enabled || string.IsNullOrEmpty(text))
            {
                return 0.0;
            }

            if (_isolationPenaltyCache != null &&
                _isolationPenaltyCache.TryGetValue(text, out double cached))
            {
                _isolationPenaltyCacheHits++;
                return cached;
            }

            _isolationPenaltyCacheMisses++;
            double penalty = _isolationPenalty.Apply(text, _languageModel);
            string oldKey = _isolationPenaltyCacheKeys[_isolationPenaltyCacheNext];
            if (oldKey != null)
            {
                _isolationPenaltyCache.Remove(oldKey);
            }
            _isolationPenaltyCache[text] = penalty;
            _isolationPenaltyCacheKeys[_isolationPenaltyCacheNext] = text;
            _isolationPenaltyCacheNext =
                (_isolationPenaltyCacheNext + 1) % IsolationPenaltyCacheCapacity;
            return penalty;
        }

        private void RecordDecodePerformance(long elapsedTicks, long isolationHits, long isolationMisses)
        {
            _performanceDecodeCalls++;
            _performanceDecodeTotalTicks += Math.Max(0L, elapsedTicks);
            _performanceDecodeMaximumTicks = Math.Max(
                _performanceDecodeMaximumTicks,
                Math.Max(0L, elapsedTicks));
            _performanceIsolationCacheHits += Math.Max(0L, isolationHits);
            _performanceIsolationCacheMisses += Math.Max(0L, isolationMisses);
        }

        private void FinishPerformanceSample()
        {
            if (_performanceDecodeCalls == 0)
            {
                return;
            }

            SentenceDecodePerformanceSample sample = CreatePerformanceSample();
            _lastPerformanceSample = sample;

            _performanceDecodeCalls = 0;
            _performanceDecodeTotalTicks = 0;
            _performanceDecodeMaximumTicks = 0;
            _performanceIsolationCacheHits = 0;
            _performanceIsolationCacheMisses = 0;
        }

        private SentenceDecodePerformanceSample CreatePerformanceSample()
        {
            return new SentenceDecodePerformanceSample
            {
                DecodeCalls = _performanceDecodeCalls,
                DecodeTotalMilliseconds = TicksToMilliseconds(_performanceDecodeTotalTicks),
                DecodeMaximumMilliseconds = TicksToMilliseconds(_performanceDecodeMaximumTicks),
                IsolationCacheHits = _performanceIsolationCacheHits,
                IsolationCacheMisses = _performanceIsolationCacheMisses
            };
        }

        private static SentenceDecodePerformanceSample CopyPerformanceSample(
            SentenceDecodePerformanceSample sample)
        {
            if (sample == null)
            {
                return null;
            }

            return new SentenceDecodePerformanceSample
            {
                DecodeCalls = sample.DecodeCalls,
                DecodeTotalMilliseconds = sample.DecodeTotalMilliseconds,
                DecodeMaximumMilliseconds = sample.DecodeMaximumMilliseconds,
                IsolationCacheHits = sample.IsolationCacheHits,
                IsolationCacheMisses = sample.IsolationCacheMisses
            };
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
        }

        private SentenceEarlyCommitEvidence BuildEarlyCommitEvidence(
            string normalized,
            BeamBucket[] states,
            IEnumerable<SentenceCandidate> completed,
            IEnumerable<SentenceCandidate> fullBeamResult,
            bool completedTruncated,
            string requiredTextPrefix)
        {
            bool confidenceTruncated = completedTruncated;
            bool usesIncompleteTail = false;
            var candidatesByText = new Dictionary<string, EarlyCommitCandidate>(StringComparer.Ordinal);
            AddEarlyCommitCandidates(completed, requiredTextPrefix, candidatesByText);

            int maximumTailLength = Math.Min(_maxCodeLength - 1, normalized.Length - 1);
            for (int tailLength = 1; tailLength <= maximumTailLength; tailLength++)
            {
                int consumedLength = normalized.Length - tailLength;
                string tail = normalized.Substring(consumedLength);
                if (!IsIncompleteCodeTail(tail))
                {
                    continue;
                }

                List<BeamState> partial = states[consumedLength].Limit(
                    _beamWidth,
                    out bool partialTruncated);
                if (partial.Count == 0)
                {
                    continue;
                }

                usesIncompleteTail = true;
                confidenceTruncated |= partialTruncated;
                foreach (BeamState item in partial)
                {
                    AddEarlyCommitCandidate(
                        EvaluateState(item),
                        requiredTextPrefix,
                        candidatesByText);
                }
            }

            if (candidatesByText.Count == 0)
            {
                return new SentenceEarlyCommitEvidence
                {
                    Proposal = string.Empty,
                    ProposalShare = 0.0,
                    RawLengths = new Dictionary<string, int>(StringComparer.Ordinal),
                    ConfidenceTruncated = confidenceTruncated,
                    IgnoreNeuralConstraint = false
                };
            }

            List<EarlyCommitCandidate> orderedCandidates = candidatesByText.Values.ToList();
            if (usesIncompleteTail)
            {
                orderedCandidates.Sort(CompareEarlyCommitConfidence);
            }
            else
            {
                orderedCandidates.Sort((left, right) =>
                    SentenceCandidate.CompareByLexiconRankThenScore(
                        left.Candidate,
                        right.Candidate));
            }
            string proposal = BuildConfidenceProposal(
                orderedCandidates,
                0.995,
                out double proposalShare);
            bool ignoreNeuralConstraint = false;
            if (usesIncompleteTail && orderedCandidates.Count > 0)
            {
                // Only the incomplete-tail merge needs the true full-beam
                // top, since that is the only case this flag can flip.
                SentenceCandidate fullTop = FindConfidenceTop(
                    fullBeamResult.Where(
                        candidate => HasRequiredPrefix(candidate.Text, requiredTextPrefix)));
                ignoreNeuralConstraint = !string.Equals(
                    orderedCandidates[0].Candidate.Text,
                    fullTop?.Text,
                    StringComparison.Ordinal);
            }
            return new SentenceEarlyCommitEvidence
            {
                Proposal = proposal,
                ProposalShare = proposalShare,
                RawLengths = BuildRawLengthsForProposal(proposal, orderedCandidates),
                ConfidenceTruncated = confidenceTruncated,
                IgnoreNeuralConstraint = ignoreNeuralConstraint
            };
        }

        private bool IsIncompleteCodeTail(string tail)
        {
            if (string.IsNullOrEmpty(tail) || !tail.All(char.IsLetter) ||
                !_lexicon.IsProperCodePrefix(tail))
            {
                return false;
            }

            return tail.Length < 2 || _lexicon.GetCandidates(tail) == null;
        }

        private static void AddEarlyCommitCandidates(
            IEnumerable<SentenceCandidate> candidates,
            string requiredTextPrefix,
            IDictionary<string, EarlyCommitCandidate> candidatesByText)
        {
            foreach (SentenceCandidate candidate in candidates)
            {
                AddEarlyCommitCandidate(candidate, requiredTextPrefix, candidatesByText);
            }
        }

        private static void AddEarlyCommitCandidate(
            SentenceCandidate candidate,
            string requiredTextPrefix,
            IDictionary<string, EarlyCommitCandidate> candidatesByText)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Text) ||
                !HasRequiredPrefix(candidate.Text, requiredTextPrefix))
            {
                return;
            }

            if (!candidatesByText.TryGetValue(candidate.Text, out EarlyCommitCandidate previous))
            {
                candidatesByText[candidate.Text] = new EarlyCommitCandidate
                {
                    Candidate = candidate,
                    ConfidenceScore = candidate.ConfidenceScore
                };
                return;
            }

            double combinedMass = LogSumExp(previous.ConfidenceScore, candidate.ConfidenceScore);
            if (candidate.ConfidenceScore > previous.Candidate.ConfidenceScore)
            {
                previous.Candidate = candidate;
            }
            previous.ConfidenceScore = combinedMass;
        }

        private static bool HasRequiredPrefix(string text, string requiredTextPrefix)
        {
            return string.IsNullOrEmpty(requiredTextPrefix) ||
                (!string.IsNullOrEmpty(text) &&
                 text.StartsWith(requiredTextPrefix, StringComparison.Ordinal));
        }

        private static int CompareEarlyCommitConfidence(
            EarlyCommitCandidate left,
            EarlyCommitCandidate right)
        {
            int confidence = right.ConfidenceScore.CompareTo(left.ConfidenceScore);
            return confidence != 0
                ? confidence
                : string.CompareOrdinal(left.Candidate.Text, right.Candidate.Text);
        }

        private static SentenceCandidate FindConfidenceTop(
            IEnumerable<SentenceCandidate> candidates)
        {
            SentenceCandidate best = null;
            foreach (SentenceCandidate candidate in candidates)
            {
                if (best == null ||
                    candidate.ConfidenceScore > best.ConfidenceScore ||
                    (candidate.ConfidenceScore == best.ConfidenceScore &&
                     string.CompareOrdinal(candidate.Text, best.Text) < 0))
                {
                    best = candidate;
                }
            }
            return best;
        }

        private static string BuildConfidenceProposal(
            IEnumerable<EarlyCommitCandidate> candidates,
            double threshold,
            out double proposalShare)
        {
            EarlyCommitCandidate[] values = candidates.ToArray();
            if (values.Length == 0)
            {
                proposalShare = 0.0;
                return string.Empty;
            }

            double max = values.Max(candidate => candidate.ConfidenceScore);
            double total = 0.0;
            var prefixMass = new Dictionary<string, double>(StringComparer.Ordinal);
            var prefixTextElements = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (EarlyCommitCandidate candidate in values)
            {
                double weight = Math.Exp(candidate.ConfidenceScore - max);
                total += weight;
                int[] textEnds = GetTextElementEndOffsets(candidate.Candidate.Text);
                for (int index = 0; index < textEnds.Length; index++)
                {
                    string prefix = candidate.Candidate.Text.Substring(0, textEnds[index]);
                    prefixMass.TryGetValue(prefix, out double previousMass);
                    prefixMass[prefix] = previousMass + weight;
                    prefixTextElements[prefix] = index + 1;
                }
            }

            string proposal = string.Empty;
            int proposalTextElements = 0;
            proposalShare = 0.0;
            foreach (KeyValuePair<string, double> item in prefixMass)
            {
                int textElements = prefixTextElements[item.Key];
                double share = item.Value / total;
                if (share >= threshold && textElements > proposalTextElements)
                {
                    proposal = item.Key;
                    proposalTextElements = textElements;
                    proposalShare = share;
                }
            }
            return proposal;
        }

        private static Dictionary<string, int> BuildRawLengthsForProposal(
            string proposal,
            IEnumerable<EarlyCommitCandidate> orderedCandidates)
        {
            var rawLengths = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(proposal))
            {
                return rawLengths;
            }

            var prefixByTextLength = new Dictionary<int, string>();
            foreach (int end in GetTextElementEndOffsets(proposal))
            {
                prefixByTextLength[end] = proposal.Substring(0, end);
            }
            foreach (EarlyCommitCandidate candidate in orderedCandidates)
            {
                SentencePathBoundary boundary = candidate.Candidate.Boundary;
                while (boundary != null)
                {
                    if (prefixByTextLength.TryGetValue(boundary.TextLength, out string prefix) &&
                        candidate.Candidate.Text.StartsWith(prefix, StringComparison.Ordinal) &&
                        !rawLengths.ContainsKey(prefix))
                    {
                        rawLengths[prefix] = boundary.RawLength;
                    }
                    boundary = boundary.Previous;
                }
                if (rawLengths.Count == prefixByTextLength.Count)
                {
                    break;
                }
            }
            return rawLengths;
        }

        private static int[] GetTextElementEndOffsets(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Array.Empty<int>();
            }

            var ends = new List<int>();
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                ends.Add(enumerator.ElementIndex + enumerator.GetTextElement().Length);
            }
            return ends.ToArray();
        }

        private static bool IsBetterDuplicate(BeamState item, BeamState previous)
        {
            return item.MaxLexiconRank < previous.MaxLexiconRank ||
                   (item.MaxLexiconRank == previous.MaxLexiconRank && item.Score > previous.Score);
        }

        private static SentenceCandidate[] SelectExactTopCandidates(
            List<SentenceCandidate> values,
            int limit)
        {
            int boundedLimit = Math.Max(1, limit);
            if (values.Count <= boundedLimit)
            {
                values.Sort(SentenceCandidate.CompareByLexiconRankThenScore);
                return values.ToArray();
            }
            return SelectExactTop(
                values,
                boundedLimit,
                SentenceCandidate.CompareByLexiconRankThenScore).ToArray();
        }

        private static List<T> SelectExactTop<T>(
            List<T> values,
            int limit,
            Comparison<T> comparison)
        {
            var heap = new List<T>(limit);
            foreach (T item in values)
            {
                if (heap.Count < limit)
                {
                    heap.Add(item);
                    SiftWorstUp(heap, heap.Count - 1, comparison);
                }
                else if (comparison(item, heap[0]) < 0)
                {
                    heap[0] = item;
                    SiftWorstDown(heap, 0, comparison);
                }
            }

            heap.Sort(comparison);
            return heap;
        }

        private static void SiftWorstUp<T>(List<T> heap, int index, Comparison<T> comparison)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (comparison(heap[parent], heap[index]) >= 0)
                {
                    return;
                }

                T value = heap[parent];
                heap[parent] = heap[index];
                heap[index] = value;
                index = parent;
            }
        }

        private static void SiftWorstDown<T>(List<T> heap, int index, Comparison<T> comparison)
        {
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= heap.Count)
                {
                    return;
                }

                int right = left + 1;
                int worse = right < heap.Count && comparison(heap[right], heap[left]) > 0
                    ? right
                    : left;
                if (comparison(heap[index], heap[worse]) >= 0)
                {
                    return;
                }

                T value = heap[index];
                heap[index] = heap[worse];
                heap[worse] = value;
                index = worse;
            }
        }

        private static double LogSumExp(double left, double right)
        {
            double max = Math.Max(left, right);
            return max + Math.Log(Math.Exp(left - max) + Math.Exp(right - max));
        }

        private static int CompareBeamStates(BeamState left, BeamState right)
        {
            int rank = left.MaxLexiconRank.CompareTo(right.MaxLexiconRank);
            if (rank != 0)
            {
                return rank;
            }

            int compared = right.Score.CompareTo(left.Score);
            return compared != 0 ? compared : string.CompareOrdinal(left.Text, right.Text);
        }

        private static string BuildSegmentedCode(string raw, SentencePathBoundary boundary)
        {
            if (string.IsNullOrEmpty(raw) || boundary == null)
            {
                return string.Empty;
            }

            var ends = new List<int>();
            while (boundary != null)
            {
                ends.Add(boundary.RawLength);
                boundary = boundary.Previous;
            }
            ends.Reverse();

            var pieces = new string[ends.Count];
            int start = 0;
            for (int index = 0; index < ends.Count; index++)
            {
                int end = ends[index];
                pieces[index] = raw.Substring(start, end - start);
                start = end;
            }
            return string.Join(" ", pieces);
        }

        private static string NormalizeRawCode(string rawCode)
        {
            if (string.IsNullOrEmpty(rawCode))
            {
                return string.Empty;
            }

            return new string(rawCode
                .Where(character => !char.IsWhiteSpace(character))
                .Select(char.ToLowerInvariant)
                .ToArray());
        }
    }
}
