using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayHeartbeatWatcher : IDisposable
    {
        private readonly TimeSpan _pollInterval;
        private readonly long _staleAfterMs;
        private readonly AutoResetEvent _stopSignal = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _running;
        private bool _staleRaised;

        public OverlayHeartbeatWatcher(TimeSpan pollInterval, TimeSpan staleAfter)
        {
            _pollInterval = pollInterval;
            _staleAfterMs = Math.Max(1, (long)staleAfter.TotalMilliseconds);
        }

        public event Action HeartbeatStale;

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
                Name = "TigerClaw.Overlay.HeartbeatWatcher"
            };
            _thread.Start();
        }

        private void ThreadMain()
        {
            while (_running)
            {
                bool stale = true;
                if (TryReadHeartbeat(out _, out long tick64))
                {
                    long now = MonotonicClock.GetMilliseconds();
                    long age = tick64 == 0 ? long.MaxValue : now - tick64;
                    stale = age > _staleAfterMs;
                }

                if (stale)
                {
                    if (!_staleRaised)
                    {
                        _staleRaised = true;
                        HeartbeatStale?.Invoke();
                    }
                }
                else
                {
                    _staleRaised = false;
                }

                if (_stopSignal.WaitOne(_pollInterval))
                {
                    break;
                }
            }
        }

        private static bool TryReadHeartbeat(out long seq, out long tick64)
        {
            seq = 0;
            tick64 = 0;

            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(RuntimeConstants.HeartbeatMmfName))
                using (var view = mmf.CreateViewAccessor())
                {
                    view.Read(0, out seq);
                    view.Read(sizeof(long), out tick64);
                    return true;
                }
            }
            catch
            {
                return false;
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

            _stopSignal.Dispose();
        }
    }
}
