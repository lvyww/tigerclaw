using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
        public string[] TextElements { get; set; }
    }

    internal sealed class SentenceLexiconIndex
    {
        private readonly Dictionary<string, SentenceLexiconCandidate[]> _candidatesByCode;
        private readonly int[] _codeLengths;

        private SentenceLexiconIndex(
            Dictionary<string, SentenceLexiconCandidate[]> candidatesByCode,
            int[] codeLengths)
        {
            _candidatesByCode = candidatesByCode;
            _codeLengths = codeLengths;
        }

        public IReadOnlyList<int> CodeLengths => _codeLengths;

        public SentenceLexiconCandidate[] GetCandidates(string code)
        {
            return code != null && _candidatesByCode.TryGetValue(code, out SentenceLexiconCandidate[] values)
                ? values
                : null;
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
                            TextElements = SplitTextElements(text)
                        });
                    }
                }

                if (allowed.Count > 0)
                {
                    filtered[pair.Key] = allowed.ToArray();
                }
            }

            return new SentenceLexiconIndex(
                filtered,
                filtered.Keys.Select(code => code.Length).Distinct().OrderBy(length => length).ToArray());
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
                candidate.Length < current.Length ||
                (candidate.Length == current.Length && string.CompareOrdinal(candidate, current) < 0))
            {
                return candidate;
            }

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
            ConfidenceCandidates = Array.Empty<SentenceCandidate>(),
            ConfidenceTruncated = false,
            ExpandedStates = 0
        };

        public string RawCode { get; set; }
        public SentenceCandidate[] Candidates { get; set; }
        public SentenceCandidate[] ConfidenceCandidates { get; set; }
        public bool ConfidenceTruncated { get; set; }
        public int ExpandedStates { get; set; }
    }

    internal sealed class SentenceInputDecoder
    {
        private const string Bos = "\x02";
        private const string Eos = "\x03";
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

        private sealed class BeamState
        {
            public double Score;
            public double LogMass;
            public string Text;
            public string SegmentedCode;
            public string Previous2;
            public string Previous1;
            public int SupplementState;
            public double SupplementScore;
            public int MaxLexiconRank;
            public SentencePathBoundary Boundary;
        }

        private sealed class BeamBucket
        {
            private const int AggregateDuringExpansionThreshold = 256;
            private List<BeamState> _pending = new List<BeamState>();
            private Dictionary<string, BeamState> _bestByText;

            public void Add(BeamState item)
            {
                if (item == null)
                {
                    return;
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
                EnsureAggregated();
                int boundedLimit = Math.Max(1, limit);
                var values = _bestByText.Values.ToList();
                truncated = values.Count > boundedLimit;
                if (truncated)
                {
                    values = SelectExactTop(values, boundedLimit);
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

        public SentenceDecodeResult Decode(string rawCode, int candidateLimit = 20)
        {
            lock (_decodeLock)
            {
                return DecodeIncrementalLocked(rawCode, candidateLimit);
            }
        }

        internal SentenceDecodeResult DecodeFull(string rawCode, int candidateLimit = 20)
        {
            string normalized = NormalizeRawCode(rawCode);
            if (normalized.Length == 0 || !normalized.Any(char.IsLetter))
            {
                return SentenceDecodeResult.Empty;
            }

            BeamBucket[] states = CreateStates(normalized.Length);
            int expanded = ExpandRange(normalized, states, 0, normalized.Length);
            return Emit(normalized, states, candidateLimit, expanded);
        }

        internal void ResetDecodeCache()
        {
            lock (_decodeLock)
            {
                ClearCache();
            }
        }

        private SentenceDecodeResult DecodeIncrementalLocked(string rawCode, int candidateLimit)
        {
            string normalized = NormalizeRawCode(rawCode);
            if (normalized.Length == 0 || !normalized.Any(char.IsLetter))
            {
                ClearCache();
                _cachedRaw = normalized ?? string.Empty;
                _cachedResult = SentenceDecodeResult.Empty;
                _cachedLimit = candidateLimit;
                return _cachedResult;
            }

            if (_cachedRaw == normalized &&
                _cachedResult != null &&
                _cachedLimit == candidateLimit)
            {
                return _cachedResult;
            }

            if (_cachedRaw == normalized && _cachedStates != null)
            {
                SentenceDecodeResult trimmed = Emit(normalized, _cachedStates, candidateLimit, 0);
                _cachedResult = trimmed;
                _cachedLimit = candidateLimit;
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
                // A one-key segment is legal only when the whole input is one key.
                // Crossing four keys also changes whether bare segments keep every rank.
                if (oldLength == 1 || length == 1 || (oldLength <= 4) != (length <= 4))
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

            SentenceDecodeResult result = Emit(normalized, states, candidateLimit, expanded);
            _cachedRaw = normalized;
            _cachedStates = states;
            _cachedResult = result;
            _cachedLimit = candidateLimit;
            return result;
        }

        private void ClearCache()
        {
            _cachedRaw = null;
            _cachedStates = null;
            _cachedResult = null;
            _cachedLimit = 0;
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
                SegmentedCode = string.Empty,
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
            bool allowAllRanks = raw.Length <= 4;
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
                    if (consumedEnd <= minimumConsumedEndExclusive)
                    {
                        continue;
                    }
                    if (length > 1 && consumedEnd - position < 2)
                    {
                        continue;
                    }

                    string segmentedPiece = raw.Substring(position, consumedEnd - position);

                    foreach (BeamState item in current)
                    {
                        foreach (SentenceLexiconCandidate candidate in candidates)
                        {
                            bool rankMatches = selectedRank > 0
                                ? candidate.Rank == selectedRank
                                : allowAllRanks || candidate.Rank == 1;
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
                                score -= _rankPenalty * Math.Log(1.0 + candidate.Rank - 1);
                            }

                            states[consumedEnd].Add(new BeamState
                            {
                                Score = score,
                                LogMass = item.LogMass + (score - item.Score - supplementAdded),
                                Text = item.Text + candidate.Text,
                                SegmentedCode = JoinSegmentedCode(
                                    item.SegmentedCode,
                                    segmentedPiece),
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

        private SentenceDecodeResult Emit(string normalized, BeamBucket[] states, int candidateLimit, int expandedStates)
        {
            List<BeamState> completed = states[normalized.Length].Limit(
                _beamWidth,
                out bool confidenceTruncated);
            var result = new List<SentenceCandidate>(completed.Count);
            foreach (BeamState item in completed)
            {
                double score = item.Score + TransitionScore(item.Previous2, item.Previous1, Eos);
                score -= _isolationPenalty.Apply(item.Text, _languageModel);
                result.Add(new SentenceCandidate
                {
                    Text = item.Text,
                    SegmentedCode = item.SegmentedCode,
                    BaseScore = score,
                    FinalScore = score,
                    ConfidenceScore = item.LogMass + (score - item.Score),
                    SupplementScore = item.SupplementScore,
                    Boundary = item.Boundary,
                    MaxLexiconRank = Math.Max(1, item.MaxLexiconRank)
                });
            }

            result.Sort(SentenceCandidate.CompareByLexiconRankThenScore);
            SentenceCandidate[] confidenceCandidates = result.ToArray();
            int limit = Math.Min(result.Count, Math.Max(1, candidateLimit));

            return new SentenceDecodeResult
            {
                RawCode = normalized,
                Candidates = result.Take(limit).ToArray(),
                ConfidenceCandidates = confidenceCandidates,
                ConfidenceTruncated = confidenceTruncated,
                ExpandedStates = expandedStates
            };
        }

        private static bool IsBetterDuplicate(BeamState item, BeamState previous)
        {
            return item.MaxLexiconRank < previous.MaxLexiconRank ||
                   (item.MaxLexiconRank == previous.MaxLexiconRank && item.Score > previous.Score);
        }

        private static List<BeamState> SelectExactTop(List<BeamState> values, int limit)
        {
            var heap = new List<BeamState>(limit);
            foreach (BeamState item in values)
            {
                if (heap.Count < limit)
                {
                    heap.Add(item);
                    SiftWorstUp(heap, heap.Count - 1);
                }
                else if (CompareBeamStates(item, heap[0]) < 0)
                {
                    heap[0] = item;
                    SiftWorstDown(heap, 0);
                }
            }

            heap.Sort(CompareBeamStates);
            return heap;
        }

        private static void SiftWorstUp(List<BeamState> heap, int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (CompareBeamStates(heap[parent], heap[index]) >= 0)
                {
                    return;
                }

                BeamState value = heap[parent];
                heap[parent] = heap[index];
                heap[index] = value;
                index = parent;
            }
        }

        private static void SiftWorstDown(List<BeamState> heap, int index)
        {
            while (true)
            {
                int left = index * 2 + 1;
                if (left >= heap.Count)
                {
                    return;
                }

                int right = left + 1;
                int worse = right < heap.Count && CompareBeamStates(heap[right], heap[left]) > 0
                    ? right
                    : left;
                if (CompareBeamStates(heap[index], heap[worse]) >= 0)
                {
                    return;
                }

                BeamState value = heap[index];
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

        private static string JoinSegmentedCode(string prefix, string piece)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return piece ?? string.Empty;
            }

            if (string.IsNullOrEmpty(piece))
            {
                return prefix;
            }

            return prefix + " " + piece;
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
