using System;
using System.Threading;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayStateSource : IDisposable
    {
        private readonly UiStateReader _reader = new UiStateReader();
        private readonly TimeSpan _pollInterval;
        private readonly AutoResetEvent _stopSignal = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _running;
        private long _lastUiSeq;

        public OverlayStateSource(TimeSpan pollInterval)
        {
            _pollInterval = pollInterval;
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
