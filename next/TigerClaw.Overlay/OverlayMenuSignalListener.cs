using System;
using System.Threading;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayMenuSignalListener : IDisposable
    {
        private readonly TimeSpan _waitTimeout;
        private readonly EventWaitHandle _showMenuEvent;
        private Thread _thread;
        private volatile bool _running;

        public OverlayMenuSignalListener(TimeSpan waitTimeout)
        {
            _waitTimeout = waitTimeout;

            try
            {
                _showMenuEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RuntimeConstants.ShowMenuEventName);
            }
            catch
            {
                _showMenuEvent = null;
            }
        }

        public event Action SignalReceived;

        public void Start()
        {
            if (_running || _showMenuEvent == null)
            {
                return;
            }

            _running = true;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "TigerClaw.Overlay.MenuSignal"
            };
            _thread.Start();
        }

        private void ThreadMain()
        {
            while (_running)
            {
                try
                {
                    if (_showMenuEvent.WaitOne(_waitTimeout))
                    {
                        SignalReceived?.Invoke();
                    }
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            _running = false;

            try
            {
                _thread?.Join(300);
            }
            catch
            {
            }

            try
            {
                _showMenuEvent?.Dispose();
            }
            catch
            {
            }
        }
    }
}
