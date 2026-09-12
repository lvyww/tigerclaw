using System;
using System.Threading;

namespace TigerClaw.Core
{
    // Independent of compositions, reveal clocks and candidate contents. An epoch
    // survives MMF coalescing: losing an intermediate off/focus update is harmless.
    internal sealed class CandidateEnvironmentTracker
    {
        private readonly object _sync = new object();
        private long _revision = 1;
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        private bool _hasMode, _nativeHook, _active, _chinese;
        public long Revision => Interlocked.Read(ref _revision);
        public void Invalidate() { Interlocked.Increment(ref _revision); }
        public void ObserveMode(bool nativeHook, bool active, bool chinese)
        {
            lock (_sync)
            {
                if (_hasMode && (_nativeHook != nativeHook || _active != active || _chinese != chinese))
                {
                    Invalidate();
                }
                _hasMode = true;
                _nativeHook = nativeHook;
                _active = active;
                _chinese = chinese;
            }
        }
    }
}
