using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Dialog
{
    public partial class App : Application
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private long _lastSeq;
        private long _lastUpdateTick64;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool bypassRegistrationCheck = TsfRegistrationGuard.ShouldBypassRegistrationCheck(AppDomain.CurrentDomain.BaseDirectory);
            if (!bypassRegistrationCheck && !TsfRegistrationGuard.IsTsfRegistered(out _))
            {
                string installScript = TsfRegistrationGuard.FindInstallScriptPath(AppDomain.CurrentDomain.BaseDirectory);
                string message = TsfRegistrationGuard.BuildNotRegisteredMessage(installScript);
                try
                {
                    MessageBox.Show(message, RuntimeConstants.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                }
                Shutdown();
                return;
            }

            int currentPid = Process.GetCurrentProcess().Id;
            if (!ProcessInstanceGuard.EnsureNoOtherInstances(RuntimeConstants.DialogProcessName, currentPid))
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            _timer.Tick += OnTick;
            _timer.Start();

            bool showAddCi = e?.Args?.Any(a => string.Equals(a, "--addci", StringComparison.OrdinalIgnoreCase)) == true;
            Window window = showAddCi ? (Window)new AddCiWindow() : new ConfigWindow();
            MainWindow = window;
            window.Show();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (TryReadHeartbeat(out long seq))
            {
                if (seq != _lastSeq)
                {
                    _lastSeq = seq;
                    _lastUpdateTick64 = MonotonicClock.GetMilliseconds();
                }
            }

            long age = _lastUpdateTick64 == 0 ? long.MaxValue : MonotonicClock.GetMilliseconds() - _lastUpdateTick64;
            if (age > 10000)
            {
                Shutdown();
            }
        }

        private static bool TryReadHeartbeat(out long seq)
        {
            seq = 0;

            try
            {
                using (MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(RuntimeConstants.HeartbeatMmfName))
                using (MemoryMappedViewAccessor view = mmf.CreateViewAccessor())
                {
                    view.Read(0, out seq);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
