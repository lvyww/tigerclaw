using System;
using System.Runtime.InteropServices;

namespace TigerClaw.Core
{
    internal static class NativeMessageBox
    {
        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONINFORMATION = 0x00000040;
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_DEFAULT_DESKTOP_ONLY = 0x00020000;

        public static void ShowWarning(string message, string caption)
        {
            Show(message, caption, MB_OK | MB_ICONWARNING);
        }

        public static void ShowDesktopInformation(string message, string caption)
        {
            Show(message, caption, MB_OK | MB_ICONINFORMATION | MB_DEFAULT_DESKTOP_ONLY);
        }

        private static void Show(string message, string caption, uint type)
        {
            MessageBoxW(IntPtr.Zero, message ?? string.Empty, caption ?? string.Empty, type);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);
    }
}
