using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Globalization;
using System.Threading;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceInputDecoder
    {
        private readonly SentenceNgramModel.QuerySession _modelSession;
        private Dictionary<BeamBucket, (int Revision, List<SentenceCandidate> Values)> _evaluated = new();
        private SentenceLockedPrefix _cachedLockedPrefix;
        private CancellationToken _cancellation;
        private int _resetEpoch, _seenResetEpoch, _disposeRequested, _cachedHistoryFloor;
        private HistoryTrim _historyTrim;

        private sealed record HistoryTrim(string Raw, int CommittedRaw, int Epoch);

        public SentenceDecodeResult Decode(string rawCode, int candidateLimit = 20,
            bool includeEarlyCommitEvidence = false, string requiredTextPrefix = null,
            SentenceLockedPrefix lockedPrefix = null, CancellationToken cancellationToken = default)
        {
            lock (_decodeLock)
            {
                ApplyDeferredReset();
                _cancellation = cancellationToken;
                long started = Stopwatch.GetTimestamp();
                long hits = _isolationPenaltyCacheHits, misses = _isolationPenaltyCacheMisses;
                try
                {
                    CheckCancellation();
                    PrepareLearning();
                    var result = DecodeCached(rawCode, candidateLimit, includeEarlyCommitEvidence,
                        requiredTextPrefix, lockedPrefix);
                    CheckCancellation();
                    RecordDecodePerformance(Stopwatch.GetTimestamp() - started,
                        _isolationPenaltyCacheHits - hits, _isolationPenaltyCacheMisses - misses);
                    return result.CopyForConsumer();
                }
                catch
                {
                    ClearCache(); // A partially expanded generation is never reusable.
                    throw;
                }
                finally
                {
                    _cancellation = default;
                    ApplyDeferredReset();
                    ApplyHistoryTrim();
                }
            }
        }

        private void CheckCancellation()
        {
            _cancellation.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposeRequested) != 0)
                throw new ObjectDisposedException(nameof(SentenceInputDecoder));
        }

        internal SentenceDecodeResult DecodeFull(string rawCode, int candidateLimit = 20,
            bool includeEarlyCommitEvidence = false, string requiredTextPrefix = null)
        {
            lock (_decodeLock)
            {
                ApplyDeferredReset();
                CheckCancellation();
                PrepareLearning();
                bool savedLearning = _learningAffected;
                var savedEvaluated = _evaluated;
                _evaluated = new();
                try
                {
                    string raw = NormalizeRawCode(rawCode);
                    if (raw.Length == 0 || !raw.Any(char.IsLetter)) return SentenceDecodeResult.Empty;
                    _learningAffected = false;
                    var states = CreateStates(raw.Length);
                    int expanded = ExpandRange(raw, states, 0, raw.Length);
                    return Emit(raw, states, candidateLimit, expanded, includeEarlyCommitEvidence,
                        requiredTextPrefix).CopyForConsumer();
                }
                finally
                {
                    _learningAffected = savedLearning;
                    _evaluated = savedEvaluated;
                    ApplyDeferredReset();
                }
            }
        }

        internal void ResetDecodeCache()
        {
            lock (_decodeLock) ClearCache();
        }

        internal void CompleteComposition()
        {
            Interlocked.Increment(ref _resetEpoch);
            if (!Monitor.TryEnter(_decodeLock)) return;
            try { ApplyDeferredReset(); }
            finally { Monitor.Exit(_decodeLock); }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeRequested, 1);
            CompleteComposition();
        }

        private void ApplyDeferredReset()
        {
            int epoch = Volatile.Read(ref _resetEpoch);
            if (epoch != _seenResetEpoch)
            {
                FinishPerformanceSample();
                ClearCache();
                _isolationPenaltyCache?.Clear();
                if (_isolationPenaltyCacheKeys != null) Array.Clear(_isolationPenaltyCacheKeys);
                _isolationPenaltyCacheNext = 0;
                _seenResetEpoch = epoch;
                Volatile.Write(ref _historyTrim, null);
            }
            if (Volatile.Read(ref _disposeRequested) != 0) _modelSession?.Dispose();
        }

        internal void RequestHistoryTrim(string raw, int committedRaw)
        {
            if (committedRaw <= 0) return;
            Volatile.Write(ref _historyTrim, new HistoryTrim(NormalizeRawCode(raw), committedRaw,
                Volatile.Read(ref _resetEpoch)));
            if (!Monitor.TryEnter(_decodeLock)) return;
            try { ApplyHistoryTrim(); }
            finally { Monitor.Exit(_decodeLock); }
        }

        private void ApplyHistoryTrim()
        {
            var request = Volatile.Read(ref _historyTrim);
            if (request == null || request.Epoch != Volatile.Read(ref _resetEpoch) ||
                _cachedStates == null || _cachedRaw == null ||
                !(_cachedRaw.StartsWith(request.Raw, StringComparison.Ordinal) ||
                  request.Raw.StartsWith(_cachedRaw, StringComparison.Ordinal))) return;
            int floor = Math.Max(0, Math.Min(request.CommittedRaw, _cachedRaw.Length) -
                _maxCodeLength - TrailingSelectorSpan(_cachedRaw) - 1);
            if (floor - _cachedHistoryFloor < 64) return;
            for (int position = _cachedHistoryFloor; position < floor; position++)
            {
                if (_cachedStates[position] != null) _evaluated.Remove(_cachedStates[position]);
                _cachedStates[position] = null;
            }
            _cachedHistoryFloor = floor;
        }

        internal int RetainedStatePositions
        {
            get { lock (_decodeLock) return _cachedStates?.Count(bucket => bucket != null) ?? 0; }
        }

        private static bool AsciiLetters(ReadOnlySpan<char> text)
        {
            foreach (char value in text) if (value < 'a' || value > 'z') return false;
            return true;
        }

        private SentenceDecodeResult DecodeCached(string input, int limit, bool evidence,
            string required, SentenceLockedPrefix locked)
        {
            string raw = NormalizeRawCode(input);
            required ??= string.Empty;
            string lockedRaw = locked == null ? string.Empty : NormalizeRawCode(locked.RawCode);
            if (raw.Length == 0 || !raw.Any(char.IsLetter) ||
                (locked != null && (lockedRaw.Length == 0 || !raw.StartsWith(lockedRaw, StringComparison.Ordinal))))
            {
                ClearCache();
                return SentenceDecodeResult.Empty;
            }
            if (!ReferenceEquals(_cachedLockedPrefix, locked)) ClearCache();
            if (Volatile.Read(ref _historyTrim)?.CommittedRaw > raw.Length)
                Volatile.Write(ref _historyTrim, null);
            if (_cachedRaw == raw && _cachedResult != null && _cachedLimit == limit &&
                _cachedIncludesEarlyCommitEvidence == evidence && _cachedRequiredTextPrefix == required)
                return _cachedResult;

            BeamBucket[] states = null;
            int expanded = 0;
            if (_cachedRaw == raw) states = _cachedStates;
            else
            {
                _evaluated.Clear();
                string old = _cachedRaw;
                int oldLength = old?.Length ?? 0;
                bool safeWhole = locked != null || (oldLength > _maxCodeLength + TrailingSelectorSpan(old ?? string.Empty) &&
                    raw.Length > _maxCodeLength + TrailingSelectorSpan(raw));
                if (_cachedStates != null && safeWhole && !_learningAffected)
                {
                    if (raw.Length > oldLength && raw.StartsWith(old, StringComparison.Ordinal) &&
                        AsciiLetters(raw.AsSpan(oldLength)))
                    {
                        int from = Math.Max(lockedRaw.Length, oldLength + 1 - _maxCodeLength - TrailingSelectorSpan(raw));
                        if (from >= _cachedHistoryFloor)
                        {
                            states = ResizeStates(_cachedStates, raw.Length);
                            for (int i = oldLength + 1; i <= raw.Length; i++) states[i] = new BeamBucket();
                            expanded = ExpandRange(raw, states, from, raw.Length, oldLength);
                            // A newly reached learning hint can change the global beam order.
                            if (_learningAffected) states = null;
                        }
                    }
                    else if (raw.Length < oldLength && old.StartsWith(raw, StringComparison.Ordinal) &&
                        raw.Length > _cachedHistoryFloor && AsciiLetters(old.AsSpan(raw.Length)) &&
                        AsciiLetters(raw.AsSpan(raw.Length - 1)))
                    {
                        states = _cachedStates;
                        for (int i = raw.Length + 1; i < states.Length; i++) states[i] = null;
                    }
                }
            }
            if (states == null)
            {
                _learningAffected = false;
                _cachedHistoryFloor = 0;
                _evaluated.Clear();
                states = SeedStates(raw.Length, lockedRaw.Length, locked);
                expanded += ExpandRange(raw, states, lockedRaw.Length, raw.Length);
            }
            var result = Emit(raw, states, limit, expanded, evidence, required);
            _cachedRaw = raw;
            _cachedStates = states;
            _cachedLockedPrefix = locked;
            _cachedResult = result;
            _cachedLimit = limit;
            _cachedIncludesEarlyCommitEvidence = evidence;
            _cachedRequiredTextPrefix = required;
            return result;
        }

        private BeamBucket[] SeedStates(int length, int lockedLength, SentenceLockedPrefix prefix)
        {
            var states = CreateStates(length);
            if (prefix == null) return states;
            states[0] = new BeamBucket();
            var seed = new BeamState
            {
                Text = prefix.Text, Previous2 = Bos, Previous1 = Bos, MaxLexiconRank = 1,
                Boundary = CopyUnlearnedBoundary(prefix.Boundary)
            };
            var elements = StringInfo.GetTextElementEnumerator(prefix.Text);
            while (elements.MoveNext())
            {
                CheckCancellation();
                string target = elements.GetTextElement();
                seed.Score += TransitionScore(seed.Previous2, seed.Previous1, target) + _emittedCharacterReward;
                if (_hasSupplements)
                {
                    seed.SupplementState = _supplementMatcher.Advance(seed.SupplementState, target, out double reward);
                    seed.Score += reward;
                    seed.SupplementScore += reward;
                }
                seed.Previous2 = seed.Previous1;
                seed.Previous1 = target;
            }
            seed.LogMass = seed.Score - seed.SupplementScore;
            states[lockedLength].Add(seed);
            return states;
        }

        private List<SentenceCandidate> EvaluateBucket(BeamBucket bucket, List<BeamState> states)
        {
            if (_evaluated.TryGetValue(bucket, out var cached) && cached.Revision == bucket.Revision)
                return cached.Values;
            var values = new List<SentenceCandidate>(states.Count);
            foreach (var state in states)
            {
                CheckCancellation();
                values.Add(EvaluateState(state));
            }
            _evaluated[bucket] = (bucket.Revision, values);
            return values;
        }

        private void ClearCache()
        {
            _learningAffected = false;
            _cachedRaw = null;
            _cachedStates = null;
            _cachedResult = null;
            _cachedLockedPrefix = null;
            _cachedLimit = 0;
            _cachedIncludesEarlyCommitEvidence = false;
            _cachedRequiredTextPrefix = string.Empty;
            _cachedHistoryFloor = 0;
            Volatile.Write(ref _historyTrim, null);
            _evaluated.Clear();
        }
    }
}
