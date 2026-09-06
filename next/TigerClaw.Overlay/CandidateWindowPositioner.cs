using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class CandidateWindowPositioner
    {
        private const int StartMenuOffsetPx = 10;
        private const int CaretGapPx = 5;
        private const int DefaultCaretHeightPx = 20;
        private IntPtr _lastMonitor = IntPtr.Zero;
        private double _screenDpiX = 96.0;
        private double _screenDpiY = 96.0;
        private bool _prevComposing;
        private int _lastTargetXPx = int.MinValue;
        private int _lastTargetYPx = int.MinValue;
        private bool _placeAboveLocked;
        private bool _placeBelowLocked;
        private bool _hasPlacementDecision;
        private bool _lastPlacedAbove;
        private bool _forceMoveForAnchorRefresh;
        private bool _hasCaretAnchor;
        private int _anchorXPx;
        private int _anchorYPx;
        private int _anchorHeightPx;

        public void Reset()
        {
            _prevComposing = false;
            _lastTargetXPx = int.MinValue;
            _lastTargetYPx = int.MinValue;
            _placeAboveLocked = false;
            _placeBelowLocked = false;
            _hasPlacementDecision = false;
            _lastPlacedAbove = false;
            _forceMoveForAnchorRefresh = false;
            ClearCaretAnchor();
        }

        public void RefreshCaretAnchorPreservingPlacement()
        {
            if (!_hasCaretAnchor && !_hasPlacementDecision)
            {
                return;
            }

            if (_hasPlacementDecision)
            {
                _placeAboveLocked = _lastPlacedAbove;
                _placeBelowLocked = !_lastPlacedAbove;
            }
            _forceMoveForAnchorRefresh = true;
            ClearCaretAnchor();
        }

        public bool Update(Window window, FrameworkElement contentRoot, OverlayUiState state, bool nowComposing, double predictedWidthDip, double predictedHeightDip)
        {
            if (window == null || contentRoot == null)
            {
                return false;
            }

            if (!nowComposing)
            {
                _prevComposing = false;
                _lastTargetXPx = int.MinValue;
                _lastTargetYPx = int.MinValue;
                _placeAboveLocked = false;
                _placeBelowLocked = false;
                _hasPlacementDecision = false;
                _lastPlacedAbove = false;
                _forceMoveForAnchorRefresh = false;
                ClearCaretAnchor();
                return false;
            }

            if (!_prevComposing)
            {
                _placeAboveLocked = false;
                _placeBelowLocked = false;
                _hasPlacementDecision = false;
                _lastPlacedAbove = false;
                _forceMoveForAnchorRefresh = false;
                ClearCaretAnchor();
            }

            if (!TryResolveCaretPosition(state, out int caretX, out int caretY, out int caretHeightPx))
            {
                _prevComposing = nowComposing;
                return false;
            }

            if (!_hasCaretAnchor)
            {
                _anchorXPx = caretX;
                _anchorYPx = caretY;
                _anchorHeightPx = Math.Max(1, caretHeightPx);
                _hasCaretAnchor = true;
            }

            caretX = _anchorXPx;
            caretY = _anchorYPx;
            caretHeightPx = _anchorHeightPx;

            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int rawXPx = caretX;
            int rawYPx = caretY + CaretGapPx;
            NativeMethods.POINT caretPoint = new NativeMethods.POINT { X = rawXPx, Y = rawYPx };
            IntPtr monitor = NativeMethods.MonitorFromPoint(caretPoint, 2);
            if (monitor == IntPtr.Zero)
            {
                IntPtr fallbackForeground = NativeMethods.GetForegroundWindow();
                if (fallbackForeground != IntPtr.Zero)
                {
                    monitor = NativeMethods.MonitorFromWindow(fallbackForeground, 2);
                }
            }
            if (monitor == IntPtr.Zero && hwnd != IntPtr.Zero)
            {
                monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            }

            bool monitorChanged = monitor != _lastMonitor;
            GetWorkAreaPx(monitor, monitorChanged, out int workLeftPx, out int workTopPx, out int workRightPx, out int workBottomPx);

            double widthDip = GetCurrentWidthDip(window, contentRoot, predictedWidthDip);
            double heightDip = GetCurrentHeightDip(window, contentRoot, predictedHeightDip);
            int widthPx = Math.Max(1, (int)Math.Ceiling(widthDip * _screenDpiX / 96.0));
            int heightPx = Math.Max(1, (int)Math.Ceiling(heightDip * _screenDpiY / 96.0));

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            bool isStartMenuLike = IsStartMenuLike(foreground);

            int maxXPx = Math.Max(workLeftPx, workRightPx - widthPx - 2);
            int maxYPx = Math.Max(workTopPx, workBottomPx - heightPx - 2);
            int targetXPx = isStartMenuLike ? workLeftPx + StartMenuOffsetPx : rawXPx;
            int targetYPx;
            if (isStartMenuLike)
            {
                targetYPx = workTopPx + StartMenuOffsetPx;
            }
            else
            {
                int caretBottomPx = caretY;
                int caretTopPx = caretBottomPx - Math.Max(1, caretHeightPx);
                bool placeAbove = ShouldPlaceAbove(caretTopPx, caretBottomPx, heightPx, workTopPx, workBottomPx);
                _hasPlacementDecision = true;
                _lastPlacedAbove = placeAbove;
                if (placeAbove)
                {
                    _placeAboveLocked = true;
                    targetYPx = caretTopPx - CaretGapPx - heightPx;
                }
                else
                {
                    targetYPx = caretBottomPx + CaretGapPx;
                }
            }
            int clampedXPx = (int)Clamp(targetXPx, workLeftPx, maxXPx);
            int clampedYPx = (int)Clamp(targetYPx, workTopPx, maxYPx);
            bool outOfBounds = targetXPx != clampedXPx || targetYPx != clampedYPx;
            bool targetUnchanged = _lastTargetXPx == clampedXPx && _lastTargetYPx == clampedYPx;

            int currentXPx = clampedXPx;
            int currentYPx = clampedYPx;
            var rect = new NativeMethods.RECT();
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, ref rect))
            {
                currentXPx = rect.left;
                currentYPx = rect.top;
            }

            double posDelta = Math.Abs(currentXPx - clampedXPx) + Math.Abs(currentYPx - clampedYPx);
            bool shouldMove = (!_prevComposing && nowComposing) ||
                              _forceMoveForAnchorRefresh ||
                              posDelta > 50 ||
                              outOfBounds ||
                              monitorChanged;
            if (targetUnchanged && !outOfBounds && !monitorChanged && posDelta < 1)
            {
                shouldMove = false;
            }

            if (shouldMove && hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    clampedXPx,
                    clampedYPx,
                    0,
                    0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
            }

            _lastTargetXPx = clampedXPx;
            _lastTargetYPx = clampedYPx;
            _prevComposing = nowComposing;
            _forceMoveForAnchorRefresh = false;
            return true;
        }

        private void ClearCaretAnchor()
        {
            _hasCaretAnchor = false;
            _anchorXPx = 0;
            _anchorYPx = 0;
            _anchorHeightPx = DefaultCaretHeightPx;
        }

        private bool ShouldPlaceAbove(int caretTopPx, int caretBottomPx, int windowHeightPx, int workTopPx, int workBottomPx)
        {
            if (_placeAboveLocked)
            {
                return true;
            }

            if (_placeBelowLocked)
            {
                return false;
            }

            bool fitsBelow = caretBottomPx + CaretGapPx + windowHeightPx <= workBottomPx;
            if (fitsBelow)
            {
                return false;
            }

            bool fitsAbove = caretTopPx - CaretGapPx - windowHeightPx >= workTopPx;
            if (fitsAbove)
            {
                return true;
            }

            int spaceAbove = caretTopPx - workTopPx;
            int spaceBelow = workBottomPx - caretBottomPx;
            return spaceAbove > spaceBelow;
        }

        private bool TryResolveCaretPosition(OverlayUiState state, out int x, out int y, out int height)
        {
            x = 0;
            y = 0;
            height = DefaultCaretHeightPx;

            int frontendCaretX = state?.CaretX ?? 0;
            int frontendCaretY = state?.CaretY ?? 0;
            if (!IsUsableCaret(frontendCaretX, frontendCaretY))
            {
                return false;
            }

            x = frontendCaretX;
            y = frontendCaretY;
            if (state != null && state.CaretHeight > 0)
            {
                height = state.CaretHeight;
            }

            return true;
        }

        private void GetWorkAreaPx(IntPtr monitor, bool monitorChanged, out int left, out int top, out int right, out int bottom)
        {
            Rect workArea = SystemParameters.WorkArea;
            left = (int)Math.Round(workArea.Left);
            top = (int)Math.Round(workArea.Top);
            right = (int)Math.Round(workArea.Right);
            bottom = (int)Math.Round(workArea.Bottom);

            if (monitor == IntPtr.Zero)
            {
                NativeMethods.POINT origin = new NativeMethods.POINT { X = 0, Y = 0 };
                monitor = NativeMethods.MonitorFromPoint(origin, 2);
            }

            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var info = new NativeMethods.MONITORINFOEX();
            if (!NativeMethods.GetMonitorInfo(monitor, info))
            {
                return;
            }

            left = info.rcWork.left;
            top = info.rcWork.top;
            right = info.rcWork.right;
            bottom = info.rcWork.bottom;

            if (monitorChanged || _lastMonitor == IntPtr.Zero)
            {
                if (NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) == 0 &&
                    dpiX > 0 &&
                    dpiY > 0)
                {
                    _screenDpiX = dpiX;
                    _screenDpiY = dpiY;
                }
                else
                {
                    _screenDpiX = 96.0;
                    _screenDpiY = 96.0;
                }

                _lastMonitor = monitor;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }

        private static bool IsUsableCaret(int x, int y)
        {
            return !(x == 0 && y == 0) &&
                   x > -30000 &&
                   x < 300000 &&
                   y > -30000 &&
                   y < 300000;
        }

        private static bool IsStartMenuLike(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            string className = GetWindowClassName(hwnd);
            string windowTitle = GetWindowTitle(hwnd);
            return string.Equals(className, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal) &&
                   string.Equals(windowTitle, "\u641C\u7D22", StringComparison.Ordinal);
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private static double GetCurrentWidthDip(Window window, FrameworkElement contentRoot, double predictedWidthDip)
        {
            if (predictedWidthDip > 1)
            {
                return predictedWidthDip;
            }

            if (contentRoot.ActualWidth > 1)
            {
                return contentRoot.ActualWidth;
            }

            if (contentRoot.DesiredSize.Width > 1)
            {
                return contentRoot.DesiredSize.Width;
            }

            if (window.ActualWidth > 1)
            {
                return window.ActualWidth;
            }

            return Math.Max(contentRoot.MinWidth, 120);
        }

        private static double GetCurrentHeightDip(Window window, FrameworkElement contentRoot, double predictedHeightDip)
        {
            if (predictedHeightDip > 1)
            {
                return predictedHeightDip;
            }

            if (contentRoot.ActualHeight > 1)
            {
                return contentRoot.ActualHeight;
            }

            if (contentRoot.DesiredSize.Height > 1)
            {
                return contentRoot.DesiredSize.Height;
            }

            if (window.ActualHeight > 1)
            {
                return window.ActualHeight;
            }

            return Math.Max(contentRoot.MinHeight, 30);
        }
    }
}
