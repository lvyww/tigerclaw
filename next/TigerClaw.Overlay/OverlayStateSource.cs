using System;
using System.Threading;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayStateSource : IDisposable
    {
        private const int AccessiblePollEveryNLoops = 20;
        private readonly UiStateReader _reader = new UiStateReader();
        private readonly OverlayLocalCaretTracker _localCaretTracker;
        private readonly TimeSpan _pollInterval;
        private readonly AutoResetEvent _stopSignal = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _running;
        private long _lastUiSeq;
        private int _loopCount;

        public OverlayStateSource(TimeSpan pollInterval, OverlayLocalCaretTracker localCaretTracker)
        {
            _pollInterval = pollInterval;
            _localCaretTracker = localCaretTracker;
        }

        public event Action<OverlayUiState, long, long> StateChanged;

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "TigerClaw.Overlay.StateSource"
            };
            _thread.Start();
        }

        private void ThreadMain()
        {
            while (_running)
            {
                _loopCount++;
                bool shouldRefreshAccessibleThisLoop = _loopCount >= AccessiblePollEveryNLoops;
                if (shouldRefreshAccessibleThisLoop)
                {
                    _loopCount = 0;
                }

                try
                {
                    if (_reader.TryReadIfChanged(_lastUiSeq, out OverlayUiState state, out long uiSeq, out long tick64) &&
                        state != null)
                    {
                        _lastUiSeq = uiSeq;
                        StateChanged?.Invoke(state, uiSeq, tick64);
                    }
                }
                catch
                {
                }

                if (shouldRefreshAccessibleThisLoop)
                {
                    try
                    {
                        _localCaretTracker?.TryRefreshAccessibleSnapshot();
                    }
                    catch
                    {
                    }
                }

                if (_stopSignal.WaitOne(_pollInterval))
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _stopSignal.Set();

            try
            {
                _thread?.Join(300);
            }
            catch
            {
            }

            _reader.Dispose();
            _stopSignal.Dispose();
        }
    }
}
