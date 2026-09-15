using System;

namespace TigerClaw.Core
{
    internal sealed partial class SentenceNgramModel
    {
        private readonly object _lifetimeLock = new();
        private int _leases;
        private bool _mappingClosed;
        private QueryCache _directCache;

        private sealed class QueryCache
        {
            private readonly FixedSizeCache<double> _scores = new(LogProbabilityCacheSize);
            private readonly FixedSizeCache<bool> _observed = new(ObservedBigramCacheSize);

            internal double Score(SentenceNgramModel model, string a, string b, string c, bool unigram)
            {
                ulong key = PackTriple(ResolveScalar(a), ResolveScalar(b), ResolveScalar(c));
                if (!unigram) key |= NoUnigramCacheKeyFlag;
                if (_scores.TryGetValue(key, out double score)) return score;
                score = model.ComputeLogProbability(a, b, c, unigram);
                _scores.Set(key, score);
                return score;
            }

            internal bool Observed(SentenceNgramModel model, string a, string b)
            {
                ulong key = PackPair(ResolveScalar(a), ResolveScalar(b));
                if (_observed.TryGetValue(key, out bool value)) return value;
                value = model.ContainsUInt64(model._bigramOffset, model._bigramCount, key);
                _observed.Set(key, value);
                return value;
            }
        }

        public double LogProbability(string a, string b, string c, bool includeUnigram)
        {
            lock (_lifetimeLock)
            {
                ThrowIfDisposed();
                return (_directCache ??= new()).Score(this, a, b, c, includeUnigram);
            }
        }

        public bool HasObservedBigram(string a, string b)
        {
            lock (_lifetimeLock)
            {
                ThrowIfDisposed();
                return (_directCache ??= new()).Observed(this, a, b);
            }
        }

        internal QuerySession CreateQuerySession()
        {
            lock (_lifetimeLock)
            {
                ThrowIfDisposed();
                var session = new QuerySession(this);
                _leases++;
                return session;
            }
        }

        public void Dispose()
        {
            lock (_lifetimeLock)
            {
                _disposed = true;
                _directCache = null;
                CloseIfUnleased();
            }
        }

        private void ReleaseLease()
        {
            lock (_lifetimeLock)
            {
                _leases--;
                CloseIfUnleased();
            }
        }

        private void CloseIfUnleased()
        {
            if (!_disposed || _leases != 0 || _mappingClosed) return;
            _mappingClosed = true;
            _view.Dispose();
            _mapping.Dispose();
        }

        internal bool MappingClosed
        {
            get { lock (_lifetimeLock) return _mappingClosed; }
        }

        internal sealed class QuerySession : ISentenceBoundaryLanguageModel, IDisposable
        {
            private readonly object _gate = new();
            private SentenceNgramModel _model;
            private QueryCache _cache;

            internal QuerySession(SentenceNgramModel model) { _model = model; }
            ~QuerySession() { Dispose(); }

            public double LogProbability(string a, string b, string c) => LogProbability(a, b, c, true);
            public double LogProbability(string a, string b, string c, bool includeUnigram)
            {
                lock (_gate)
                {
                    if (_model == null) throw new ObjectDisposedException(nameof(QuerySession));
                    return (_cache ??= new()).Score(_model, a, b, c, includeUnigram);
                }
            }

            public bool HasObservedBigram(string a, string b)
            {
                lock (_gate)
                {
                    if (_model == null) throw new ObjectDisposedException(nameof(QuerySession));
                    return (_cache ??= new()).Observed(_model, a, b);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_model == null) return;
                    var model = _model;
                    _model = null;
                    _cache = null;
                    model.ReleaseLease();
                }
                GC.SuppressFinalize(this);
            }
        }
    }
}
