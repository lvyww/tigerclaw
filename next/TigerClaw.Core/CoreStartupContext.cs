using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TigerClaw.Core
{
    internal static class CoreStartupContext
    {
        internal static bool IsAllowed(int sessionId, SecurityIdentifier user, string station, string desktop)
        {
            return sessionId > 0 && user != null &&
                !user.IsWellKnown(WellKnownSidType.LocalSystemSid) &&
                !user.IsWellKnown(WellKnownSidType.LocalServiceSid) &&
                !user.IsWellKnown(WellKnownSidType.NetworkServiceSid) &&
                string.Equals(station, "WinSta0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool CanStart()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                using var process = Process.GetCurrentProcess();
                return IsAllowed(process.SessionId, identity.User,
                    ObjectName(GetProcessWindowStation()), ObjectName(GetThreadDesktop(GetCurrentThreadId())));
            }
            catch
            {
                return false;
            }
        }

        private static string ObjectName(IntPtr handle)
        {
            var name = new StringBuilder(256);
            return handle != IntPtr.Zero && GetUserObjectInformation(handle, 2, name, name.Capacity * 2, out _)
                ? name.ToString() : null;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll")]
        private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index,
            StringBuilder information, int length, out int needed);
    }
}
