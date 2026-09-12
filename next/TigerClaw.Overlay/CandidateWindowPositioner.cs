using System;
using System.Text;
using System.Windows;
using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    internal sealed class CandidateWindowPositioner
    {
        private const int DefaultCaretHeightPx = 20;
        private readonly ICandidatePlacementHost _host;
        private CandidateOrientation _orientation, _pendingOrientation;
        private CandidatePlacementEnvironment _environment, _pendingEnvironment;
        private bool _hasEnvironment, _pending, _hasCaretAnchor;
        private int _anchorXPx, _anchorYPx, _anchorHeightPx;
        private int _pendingX, _pendingY;
        private long _revision, _pendingRevision;
        private OverlayUiState _pendingState;
        private bool _observed, _nativeHook, _off, _chinese, _active;
        private long _coreRevision, _owner;
        private string _instanceId;
        private int _ownerProcess;

        public CandidateWindowPositioner() : this(new Win32CandidatePlacementHost()) { }
        internal CandidateWindowPositioner(ICandidatePlacementHost host)
        { _host = host ?? throw new ArgumentNullException(nameof(host)); }
        internal bool Above => _orientation.Above;

        public static bool CanDisplay(OverlayUiState state)
        {
            return state != null && !state.IsOff && state.IsChinese &&
                (state.CandidateEnvironmentRevision == 0 || state.CandidateEnvironmentActive);
        }
        public void ObserveState(OverlayUiState state)
        {
            if (state == null) { Reset(); _observed = false; return; }
            if ((_observed && !SameStateEnvironment(state)) || !CanDisplay(state)) Reset();
            _observed = true;
            _coreRevision = state.CandidateEnvironmentRevision;
            _instanceId = state.CandidateEnvironmentId;
            _owner = state.CandidateOwnerHwnd;
            _ownerProcess = state.CandidateOwnerProcessId;
            _nativeHook = state.IsNativeHook;
            _off = state.IsOff;
            _chinese = state.IsChinese;
            _active = state.CandidateEnvironmentActive;
        }
        private bool SameStateEnvironment(OverlayUiState state)
        {
            return state != null && string.Equals(_instanceId, state.CandidateEnvironmentId, StringComparison.Ordinal) &&
                _coreRevision == state.CandidateEnvironmentRevision &&
                _owner == state.CandidateOwnerHwnd && _ownerProcess == state.CandidateOwnerProcessId &&
                _nativeHook == state.IsNativeHook && _off == state.IsOff && _chinese == state.IsChinese &&
                _active == state.CandidateEnvironmentActive;
        }
        // Normal hidden/idle transitions keep the accepted direction but release
        // the previous composition's anchor and any unconfirmed frame.
        public void EndComposition()
        {
            ++_revision;
            _pending = false;
            _pendingState = null;
            _hasCaretAnchor = false;
        }
        public void Reset()
        {
            EndComposition();
            _orientation.Reset();
            _hasEnvironment = false;
        }
        public void RefreshCaretAnchorPreservingPlacement()
        {
            // Partial/automatic commit updates the anchor, not the environment.
            EndComposition();
        }
        public bool Update(Window window, FrameworkElement contentRoot, OverlayUiState state,
            bool nowComposing, double predictedWidthDip, double predictedHeightDip)
        {
            ObserveState(state);
            _pending = false;
            if (!nowComposing) { EndComposition(); return false; }
            if (window == null || contentRoot == null || !CanDisplay(state)) return false;
            int caretX = state.CaretX, caretY = state.CaretY;
            if (!IsUsableCaret(caretX, caretY)) return false;
            int caretHeight = state.CaretHeight > 0 ? state.CaretHeight : DefaultCaretHeightPx;
            CandidatePlacementEnvironment environment;
            if (!_host.TryCapture(state, caretX, caretY, out environment)) return false;

            bool environmentChanged = !_hasEnvironment || !_environment.Equals(environment);
            int tolerance = CandidateOrientation.Tolerance(caretHeight, _anchorHeightPx, environment.Dpi);
            // Retain the composition X anchor. Actual Y follows the current
            // caret; only direction uses the stable jitter reference. A real
            // line/host move also refreshes X, as does an automatic-commit anchor.
            if (!_hasCaretAnchor || environmentChanged ||
                Math.Abs((long)caretY - _anchorYPx) > tolerance)
            {
                _anchorXPx = caretX;
                _anchorYPx = caretY;
                _anchorHeightPx = caretHeight;
                _hasCaretAnchor = true;
            }
            int width, height;
            if (!TryPixels(GetCurrentWidthDip(window, contentRoot, predictedWidthDip), environment.Dpi, out width) ||
                !TryPixels(GetCurrentHeightDip(window, contentRoot, predictedHeightDip), environment.Dpi, out height)) return false;
            var next = _orientation;
            if (environment.UncertainOwner) next.Reset(); // Display without inheriting an unknown host.
            int x, y;
            if (environment.StartMenu)
            {
                next.Reset(); // A special corner location is not an above decision.
                var work = environment.Work;
                x = CandidateOrientation.Clamp((long)work.Left + 10, work.Left, Math.Max((long)work.Left, (long)work.Right - width - 2));
                y = CandidateOrientation.Clamp((long)work.Top + 10, work.Top, Math.Max((long)work.Top, (long)work.Bottom - height - 2));
            }
            else if (!next.TryPlace(_anchorXPx, caretY, caretHeight, environment, width, height, out x, out y)) return false;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            CandidateRect current;
            long revision = _revision;
            // Do not use the old 50-pixel movement gate to suppress a necessary
            // side/size change. The small stable-Y band handles layout jitter.
            if (!_host.TryGetRect(hwnd, out current) || current.Left != x || current.Top != y)
            {
                if (!_host.Move(hwnd, x, y)) return false;
            }
            if (revision != _revision || !SameStateEnvironment(state) || !CanDisplay(state)) return false;
            _pendingOrientation = next;
            _pendingEnvironment = environment;
            _pendingState = state;
            _pendingRevision = revision;
            _pendingX = x;
            _pendingY = y;
            _pending = true;
            return true;
        }
        // Called only after ShowCandidate. Hidden/failed/reentrant frames cannot
        // teach the next word a direction which was never successfully shown.
        public bool ConfirmShown(Window window, OverlayUiState state)
        {
            if (!_pending || _pendingRevision != _revision || !ReferenceEquals(state, _pendingState) ||
                !SameStateEnvironment(state) || !CanDisplay(state) || window == null ||
                !window.IsVisible || window.Opacity <= 0) return false;
            CandidateRect rect;
            if (!_host.TryGetRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out rect) ||
                rect.Left != _pendingX || rect.Top != _pendingY) return false;
            _orientation = _pendingOrientation;
            _environment = _pendingEnvironment;
            _hasEnvironment = true;
            _pending = false;
            _pendingState = null;
            return true;
        }
        private static bool TryPixels(double dip, int dpi, out int pixels)
        {
            double scaled = Math.Ceiling(dip * dpi / 96.0);
            pixels = 0;
            if (double.IsNaN(scaled) || double.IsInfinity(scaled) || scaled < 1 || scaled > int.MaxValue) return false;
            pixels = (int)scaled;
            return true;
        }
        private static bool IsUsableCaret(int x, int y)
        {
            return !(x == 0 && y == 0) && x > -30000 && x < 300000 && y > -30000 && y < 300000;
        }
        private static double GetCurrentWidthDip(Window window, FrameworkElement contentRoot, double predicted)
        {
            if (predicted > 1) return predicted;
            if (contentRoot.ActualWidth > 1) return contentRoot.ActualWidth;
            if (contentRoot.DesiredSize.Width > 1) return contentRoot.DesiredSize.Width;
            if (window.ActualWidth > 1) return window.ActualWidth;
            return Math.Max(contentRoot.MinWidth, 120);
        }
        private static double GetCurrentHeightDip(Window window, FrameworkElement contentRoot, double predicted)
        {
            if (predicted > 1) return predicted;
            if (contentRoot.ActualHeight > 1) return contentRoot.ActualHeight;
            if (contentRoot.DesiredSize.Height > 1) return contentRoot.DesiredSize.Height;
            if (window.ActualHeight > 1) return window.ActualHeight;
            return Math.Max(contentRoot.MinHeight, 30);
        }
    }

    internal interface ICandidatePlacementHost
    {
        bool TryCapture(OverlayUiState state, int x, int y, out CandidatePlacementEnvironment environment);
        bool TryGetRect(IntPtr window, out CandidateRect rect);
        bool Move(IntPtr window, int x, int y);
    }
    internal sealed class Win32CandidatePlacementHost : ICandidatePlacementHost
    {
        public bool TryCapture(OverlayUiState state, int x, int y, out CandidatePlacementEnvironment environment)
        {
            environment = default(CandidatePlacementEnvironment);
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            uint processId;
            uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out processId);
            // A newer Core's owner must still be the foreground host; otherwise a
            // delayed MMF snapshot must not show candidates over another app.
            if (state.CandidateEnvironmentRevision != 0 && state.CandidateOwnerHwnd != 0 &&
                (unchecked((uint)foreground.ToInt64()) != unchecked((uint)state.CandidateOwnerHwnd) ||
                 (state.CandidateOwnerProcessId != 0 && processId != (uint)state.CandidateOwnerProcessId))) return false;
            var gui = new NativeMethods.GUITHREADINFO();
            gui.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.GUITHREADINFO));
            bool uncertain = thread == 0 || !NativeMethods.GetGUIThreadInfo(thread, ref gui);
            CandidateRect bounds, focusBounds;
            if (!TryGetRect(foreground, out bounds)) uncertain = true;
            IntPtr focus = gui.hwndFocus != IntPtr.Zero ? gui.hwndFocus : foreground;
            if (!TryGetRect(focus, out focusBounds)) uncertain = true;
            IntPtr monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, 2);
            var info = new NativeMethods.MONITORINFOEX();
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, info)) return false;
            uint dpiX, dpiY;
            if (NativeMethods.GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) != 0 || dpiY == 0 || dpiY > 65535)
            { dpiY = 96; uncertain = true; }
            var className = new StringBuilder(256);
            var title = new StringBuilder(256);
            NativeMethods.GetClassName(foreground, className, className.Capacity);
            NativeMethods.GetWindowText(foreground, title, title.Capacity);
            environment = new CandidatePlacementEnvironment
            {
                InstanceId = state.CandidateEnvironmentId, UncertainOwner = uncertain,
                Revision = state.CandidateEnvironmentRevision, Owner = state.CandidateOwnerHwnd,
                ProcessId = (int)processId, Foreground = foreground.ToInt64(), Focus = focus.ToInt64(),
                Monitor = monitor.ToInt64(), Dpi = (int)dpiY,
                Work = new CandidateRect(info.rcWork.left, info.rcWork.top, info.rcWork.right, info.rcWork.bottom),
                OwnerBounds = focusBounds, ForegroundBounds = bounds,
                StartMenu = string.Equals(className.ToString(), "Windows.UI.Core.CoreWindow", StringComparison.Ordinal) &&
                    string.Equals(title.ToString(), "\u641C\u7D22", StringComparison.Ordinal)
            };
            return environment.Work.Right > environment.Work.Left && environment.Work.Bottom > environment.Work.Top;
        }
        public bool TryGetRect(IntPtr window, out CandidateRect rect)
        {
            var native = new NativeMethods.RECT();
            bool ok = NativeMethods.GetWindowRect(window, ref native);
            rect = new CandidateRect(native.left, native.top, native.right, native.bottom);
            return ok;
        }
        public bool Move(IntPtr window, int x, int y)
        {
            return NativeMethods.SetWindowPos(window, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
        }
    }
}
