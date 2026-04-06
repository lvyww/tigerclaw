using System;
using System.Diagnostics;
using System.Windows;
using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    public partial class App : Application
    {
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
            if (!ProcessInstanceGuard.EnsureNoOtherInstances(RuntimeConstants.OverlayProcessName, currentPid))
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}
