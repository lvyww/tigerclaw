using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace TigerClaw.Shared
{
    // Additive channel: v1 stays unchanged for already deployed WPF/old Overlay.
    internal sealed class UiStateSnapshotPublisher : IDisposable
    {
        private MemoryMappedFile _map;
        private MemoryMappedViewAccessor _view;
        private Mutex _mutex;
        private EventWaitHandle _changed;

        internal UiStateSnapshotPublisher(string legacyName)
        {
            try
            {
                string name = legacyName + ".Snapshot.v2";
                _mutex = new Mutex(false, name + ".Lock");
                _changed = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Changed");
                _map = MemoryMappedFile.CreateOrOpen(name, 128 * 1024);
                _view = _map.CreateViewAccessor();
            }
            catch
            {
                Dispose();
            }
        }

        internal void Publish(long sequence, long tick, byte[] payload)
        {
            if (_view == null) return;
            bool acquired = false;
            try
            {
                // Never block Core on a suspended reader. v1 remains available
                // if this optional fast-channel publication must be skipped.
                try { acquired = _mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                // A writer dying mid-copy leaves an invalid snapshot, not a
                // seemingly committed old sequence with partially new bytes.
                _view.Write(0, 0L);
                _view.Write(8, tick);
                _view.Write(16, payload.Length);
                _view.WriteArray(20, payload, 0, payload.Length);
                Thread.MemoryBarrier();
                _view.Write(0, sequence);
            }
            catch
            {
                // Optional channel failure must not invalidate the v1 update.
            }
            finally
            {
                if (acquired) _mutex.ReleaseMutex();
                // Wake after releasing the gate, including the v1-only fallback.
                _changed.Set();
            }
        }

        public void Dispose()
        {
            _view?.Dispose(); _view = null;
            _map?.Dispose(); _map = null;
            _changed?.Dispose(); _changed = null;
            _mutex?.Dispose(); _mutex = null;
        }
    }
}
