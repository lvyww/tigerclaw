using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayHeartbeatBroadcaster : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Timer _timer;
        private long _sequence;

        public OverlayHeartbeatBroadcaster()
        {
            _mmf = MemoryMappedFile.CreateOrOpen(RuntimeConstants.OverlayHeartbeatMmfName, sizeof(long) * 2);
            _view = _mmf.CreateViewAccessor();
            _timer = new Timer(OnTick);
        }

        public void Start()
        {
            WriteHeartbeat();
            _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }

        private void OnTick(object state)
        {
            WriteHeartbeat();
        }

        private void WriteHeartbeat()
        {
            long seq = Interlocked.Increment(ref _sequence);
            long tick64 = MonotonicClock.GetMilliseconds();
            _view.Write(0, seq);
            _view.Write(sizeof(long), tick64);
            _view.Flush();
        }

        public void Dispose()
        {
            _timer.Dispose();
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
