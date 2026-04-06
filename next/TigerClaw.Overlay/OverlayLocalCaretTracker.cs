using System;
using System.Windows;
using Accessibility;

namespace TigerClaw.Overlay
{
    internal sealed class OverlayLocalCaretTracker
    {
        private readonly object _lock = new object();
        private LocalCaretSnapshot _accessibleSnapshot;

        public void TryRefreshAccessibleSnapshot()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                ClearAccessibleSnapshot();
                return;
            }

            if (!TryGetAccessibleCaret(foreground, out LocalCaretSnapshot snapshot))
            {
                ClearAccessibleSnapshot();
                return;
            }

            snapshot.Source = "accessible";

            lock (_lock)
            {
                _accessibleSnapshot = snapshot;
            }
        }

        public bool TryGetCachedAccessibleSnapshot(out LocalCaretSnapshot snapshot)
        {
            lock (_lock)
            {
                snapshot = _accessibleSnapshot;
            }

            if (!snapshot.IsValid)
            {
                snapshot = default(LocalCaretSnapshot);
                return false;
            }

            return IsUsable(snapshot);
        }

        private static bool TryGetAccessibleCaret(IntPtr foreground, out LocalCaretSnapshot snapshot)
        {
            snapshot = default(LocalCaretSnapshot);

            NativeMethods.GUITHREADINFO info = NativeMethods.CreateGuiThreadInfo();
            int threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            if (threadId == 0 || !NativeMethods.GetGUIThreadInfo(threadId, ref info))
            {
                return false;
            }

            IntPtr hwndFocus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;
            object accessibleObject = null;
            Guid accessibleGuid = typeof(IAccessible).GUID;
            int hr = NativeMethods.AccessibleObjectFromWindow(hwndFocus, NativeMethods.OBJID_CARET, ref accessibleGuid, ref accessibleObject);
            if (hr < 0 || accessibleObject == null)
            {
                return false;
            }

            try
            {
                var accessible = (IAccessible)accessibleObject;
                accessible.accLocation(out int left, out int top, out int width, out int height, 0);
                if (left == 0 && top == 0 && width == 0 && height == 0)
                {
                    return false;
                }

                if (height <= 4)
                {
                    return false;
                }

                snapshot = new LocalCaretSnapshot
                {
                    IsValid = true,
                    X = left + width,
                    Y = top + height,
                    Width = Math.Max(2, width),
                    Height = Math.Max(20, height)
                };
                return IsUsable(snapshot);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsUsable(LocalCaretSnapshot snapshot)
        {
            return snapshot.IsValid &&
                   !(snapshot.X == 0 && snapshot.Y == 0) &&
                   snapshot.X > -30000 &&
                   snapshot.X < 300000 &&
                   snapshot.Y > -30000 &&
                   snapshot.Y < 300000;
        }

        private void ClearAccessibleSnapshot()
        {
            lock (_lock)
            {
                _accessibleSnapshot = default(LocalCaretSnapshot);
            }
        }
    }

    internal struct LocalCaretSnapshot
    {
        public bool IsValid;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public string Source;
    }
}
