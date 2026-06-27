using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Core
{
    internal sealed class OverlayLaunchSupervisor : IDisposable
    {
        private const int CheckIntervalMs = 3000;
        private const int AliveThresholdMs = 6000;
        private const int RetryInitialMs = 10000;
        private const int RetryStepMs = 10000;
        private const int RetryMaxMs = 120000;
        private const int MinAttemptGapMs = 2000;

        private readonly ProcessLauncher _launcher;
        private readonly Timer _timer;
        private readonly object _sync = new object();

        private bool _started;
        private bool _autoRestartEnabled;
        private bool _manualDemand;
        private long _nextAutoAttemptTick;
        private long _lastAttemptTick;
        private int _retryDelayMs = RetryInitialMs;

        public OverlayLaunchSupervisor(ProcessLauncher launcher)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _timer = new Timer(OnTick);
            _nextAutoAttemptTick = long.MaxValue;
        }

        public void Start(bool autoRestartEnabled)
        {
            lock (_sync)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _autoRestartEnabled = autoRestartEnabled;
                _nextAutoAttemptTick = autoRestartEnabled ? 0 : long.MaxValue;
                _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(CheckIntervalMs));
            }
        }

        public void RequestLaunch()
        {
            lock (_sync)
            {
                if (!_started)
                {
                    return;
                }

                _manualDemand = true;
                _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(CheckIntervalMs));
            }
        }

        private void OnTick(object state)
        {
            bool alive = IsOverlayAlive();
            long now = MonotonicClock.GetMilliseconds();
            bool shouldLaunch = false;

            lock (_sync)
            {
                if (!_started)
                {
                    return;
                }

                if (alive)
                {
                    _manualDemand = false;
                    _retryDelayMs = RetryInitialMs;
                    _nextAutoAttemptTick = _autoRestartEnabled ? now + RetryInitialMs : long.MaxValue;
                    return;
                }

                bool canAttemptByGap = _lastAttemptTick == 0 || now - _lastAttemptTick >= MinAttemptGapMs;
                bool autoDue = _autoRestartEnabled && now >= _nextAutoAttemptTick;
                bool manualDue = _manualDemand && canAttemptByGap;
                if (!manualDue && !autoDue)
                {
                    return;
                }

                _manualDemand = false;
                _lastAttemptTick = now;
                _nextAutoAttemptTick = _autoRestartEnabled ? now + _retryDelayMs : long.MaxValue;
                _retryDelayMs = Math.Min(RetryMaxMs, _retryDelayMs + RetryStepMs);
                shouldLaunch = true;
            }

            if (shouldLaunch)
            {
                _launcher.TryLaunchOverlay();
            }
        }

        private static bool IsOverlayAlive()
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(RuntimeConstants.OverlayHeartbeatMmfName))
                using (var view = mmf.CreateViewAccessor(0, sizeof(long) * 2, MemoryMappedFileAccess.Read))
                {
                    long seq = 0;
                    long tick64 = 0;
                    view.Read(0, out seq);
                    view.Read(sizeof(long), out tick64);
                    if (seq <= 0 || tick64 <= 0)
                    {
                        return false;
                    }

                    long now = MonotonicClock.GetMilliseconds();
                    long age = now >= tick64 ? now - tick64 : long.MaxValue;
                    return age <= AliveThresholdMs;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _started = false;
            }

            _timer.Dispose();
        }
    }
}
