using System;
using System.Diagnostics;
using System.Threading;

namespace TigerClaw.Core
{
    // No idle timer: residency follows configuration/schema eligibility only.
    internal sealed class SentenceServiceLifecycle : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Action<CancellationToken> _load;
        private readonly Action _release;
        private readonly Func<SentenceRerankRequest, CancellationToken, double[]> _score;
        private readonly Action<long, string, double[]> _completed;
        private readonly Thread _worker;
        private bool _enabled;
        private bool _loadPending;
        private bool _releasePending;
        private bool _mayBeLoaded;
        private bool _stopping;
        private long _epoch;
        private SentenceRerankRequest _pending;
        private CancellationTokenSource _activeCancellation;

        internal SentenceServiceLifecycle(Action<CancellationToken> load, Action release,
            Func<SentenceRerankRequest, CancellationToken, double[]> score,
            Action<long, string, double[]> completed)
        {
            _load = load;
            _release = release;
            _score = score;
            _completed = completed;
            _worker = new Thread(Run) { IsBackground = true, Name = "TigerClaw sentence reranker" };
            _worker.Start();
        }

        internal void SetEnabled(bool enabled)
        {
            lock (_lock)
            {
                if (_stopping || _enabled == enabled) return;
                _enabled = enabled;
                _epoch++;
                _pending = null;
                _activeCancellation?.Cancel();
                _loadPending = enabled;
                if (!enabled && _mayBeLoaded) _releasePending = true;
                Monitor.PulseAll(_lock);
            }
        }

        internal void Request(SentenceRerankRequest request)
        {
            lock (_lock)
            {
                if (_stopping || !_enabled) return;
                _pending = request;
                Monitor.PulseAll(_lock);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _stopping = true;
                _enabled = false;
                _epoch++;
                _pending = null;
                _loadPending = false;
                _releasePending |= _mayBeLoaded;
                _activeCancellation?.Cancel();
                Monitor.PulseAll(_lock);
            }
            if (Thread.CurrentThread != _worker) _worker.Join(1500);
        }

        private void Run()
        {
            while (true)
            {
                bool release, load;
                long epoch;
                SentenceRerankRequest request;
                CancellationToken token;
                lock (_lock)
                {
                    while (!_stopping && !_releasePending && !_loadPending && _pending == null)
                        Monitor.Wait(_lock);
                    if (_stopping && !_releasePending) return;
                    release = _releasePending;
                    load = !release && _loadPending;
                    epoch = _epoch;
                    request = !release && !load ? _pending : null;
                    if (release) _releasePending = false;
                    else
                    {
                        _mayBeLoaded = true;
                        if (load) _loadPending = false;
                        else _pending = null;
                        _activeCancellation = new CancellationTokenSource();
                    }
                    token = _activeCancellation?.Token ?? CancellationToken.None;
                }
                try
                {
                    if (release)
                    {
                        _release();
                        lock (_lock) _mayBeLoaded = false;
                    }
                    else if (load) _load(token);
                    else if (request != null)
                    {
                        double[] scores = _score(request, token);
                        bool accept;
                        lock (_lock) accept = !_stopping && _enabled && epoch == _epoch && !token.IsCancellationRequested;
                        // Never call Engine while holding our lock (Engine also calls Request).
                        if (accept && scores != null) _completed?.Invoke(request.Generation, request.RawCode, scores);
                    }
                }
                catch (Exception error)
                {
                    Debug.WriteLine("[Sentence] lifecycle operation failed: " + error.Message);
                    if (release)
                    {
                        lock (_lock) _releasePending = true;
                        // Retry failed cleanup, never a residency/idle timeout.
                        Thread.Sleep(250);
                    }
                }
                finally
                {
                    lock (_lock)
                    {
                        _activeCancellation?.Dispose();
                        _activeCancellation = null;
                    }
                }
            }
        }
    }
}
