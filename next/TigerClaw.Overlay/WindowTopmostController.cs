using System;
using System.Windows;

namespace TigerClaw.Overlay
{
    internal static class WindowTopmostController
    {
        public static void ForceTopmostNoActivate(Window window)
        {
            if (window == null)
            {
                return;
            }

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }
}
