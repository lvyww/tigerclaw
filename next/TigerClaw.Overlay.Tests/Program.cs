using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TigerClaw.Overlay;
using TigerClaw.Shared;

internal static class Program
{
    private static int _checks, _cases;
    private static void Require(bool ok, string message)
    { ++_checks; if (!ok) throw new InvalidOperationException(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private sealed class Host : ICandidatePlacementHost
    {
        public CandidatePlacementEnvironment Environment;
        public bool FailMove, FailCapture;
        public Action DuringMove;
        public readonly Win32CandidatePlacementHost Native = new Win32CandidatePlacementHost();
        public Host()
        {
            var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), 2);
            var info = new NativeMethods.MONITORINFOEX();
            Require(NativeMethods.GetMonitorInfo(monitor, info), "monitor unavailable");
            uint dx, dy; Require(NativeMethods.GetDpiForMonitor(monitor, 0, out dx, out dy) == 0, "DPI unavailable");
            Environment = new CandidatePlacementEnvironment
            {
                Dpi = (int)dy, Monitor = monitor.ToInt64(), Owner = 1, Foreground = 1, Focus = 2, ProcessId = 1,
                Work = new CandidateRect(info.rcWork.left, info.rcWork.top, info.rcWork.right, info.rcWork.bottom),
                OwnerBounds = new CandidateRect(100, 100, 600, 600), ForegroundBounds = new CandidateRect(100, 100, 600, 600)
            };
        }
        public bool TryCapture(OverlayUiState state, int x, int y, out CandidatePlacementEnvironment environment)
        {
            environment = Environment; environment.Revision = state.CandidateEnvironmentRevision;
            environment.InstanceId = state.CandidateEnvironmentId;
            return !FailCapture;
        }
        public bool TryGetRect(IntPtr window, out CandidateRect rect) { return Native.TryGetRect(window, out rect); }
        public bool Move(IntPtr window, int x, int y)
        {
            if (FailMove) { FailMove = false; return false; }
            bool success = Native.Move(window, x, y);
            if (DuringMove != null) { var action = DuringMove; DuringMove = null; action(); }
            return success;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Host Host = new Host();
        public readonly CandidateWindowPositioner Positioner;
        public readonly Border Content = new Border { Background = Brushes.White };
        public readonly Window Window;
        public readonly OverlayUiState State;
        public Fixture()
        {
            Positioner = new CandidateWindowPositioner(Host);
            State = new OverlayUiState { IsChinese = true, CandidateVisible = true, InputCode = "ab",
                CandidateEnvironmentRevision = 1, CandidateEnvironmentId = "core-a", CandidateEnvironmentActive = true,
                CaretX = Host.Environment.Work.Left + 100,
                CaretY = Host.Environment.Work.Bottom - Pixels(30) - 12, CaretHeight = Pixels(20) };
            Window = new Window { Width = 200, Height = 30, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = Brushes.Transparent, Content = Content, Opacity = 0 };
            Window.Show(); Pump();
        }
        public int Pixels(int dip) { return (int)Math.Ceiling(dip * Host.Environment.Dpi / 96.0); }
        public CandidateRect Rect()
        { CandidateRect rect; Require(Host.TryGetRect(new WindowInteropHelper(Window).Handle, out rect), "no HWND"); return rect; }
        public bool Prepare(int dipHeight)
        {
            Window.Height = dipHeight; Content.Height = dipHeight; Window.UpdateLayout(); Pump();
            return Positioner.Update(Window, Content, State, true, 200, dipHeight);
        }
        public void Render(int dipHeight)
        {
            Require(Prepare(dipHeight), "valid positioning rejected");
            Window.Opacity = 1;
            Require(Positioner.ConfirmShown(Window, State), "visible frame not accepted");
        }
        public void Above(int height)
        { Require(Positioner.Above && Rect().Top == State.CaretY - State.CaretHeight - 5 - Pixels(height), "above position wrong"); }
        public void Below() { Require(!Positioner.Above && Rect().Top == State.CaretY + 5, "below position wrong"); }
        public void End()
        { Window.Hide(); Positioner.EndComposition(); State.CandidateVisible = false; State.InputCode = ""; }
        public void Begin()
        { State.CandidateVisible = true; State.InputCode = "a"; Window.Opacity = 0; Window.Show(); Pump(); }
        public void Dispose() { Window.Close(); Pump(); }
    }
    private static void Run()
    {
        using (var f = new Fixture())
        {
            f.Render(30); f.Below(); f.Render(150); f.Above(150); f.Render(30); f.Above(30);
            for (int i = 0; i < 100; ++i)
            {
                f.End(); f.Begin(); Require(f.Prepare(30), "next word failed");
                Require(f.Window.Opacity == 0 && f.Rect().Top == f.State.CaretY - f.State.CaretHeight - 5 - f.Pixels(30),
                    "inherited first frame positioned below");
                f.Window.Opacity = 1; Require(f.Positioner.ConfirmShown(f.Window, f.State), "first frame not accepted"); f.Above(30);
            }
            ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); int initial = f.State.CaretY;
            int epsilon = CandidateOrientation.Tolerance(f.State.CaretHeight, f.State.CaretHeight, f.Host.Environment.Dpi);
            for (int delta = 1; delta <= epsilon; ++delta) { f.State.CaretY = initial - delta; f.Render(30); f.Above(30); }
            f.State.CaretY = initial - epsilon - 1; f.Render(30); f.Below();
            f.State.CaretY = initial; f.Render(150); f.State.CaretY += epsilon + 1; f.Render(30); f.Above(30);
            f.State.CaretY -= epsilon + 1; f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); f.End(); ++f.State.CandidateEnvironmentRevision;
            f.Positioner.ObserveState(f.State); Require(!f.Positioner.Above, "idle epoch change kept direction");
            f.Begin(); f.Render(30); f.Below();
            f.Render(150); f.State.CandidateEnvironmentId = "core-b";
            f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); f.Positioner.RefreshCaretAnchorPreservingPlacement(); f.Render(30); f.Above(30);
            f.State.CandidateEnvironmentActive = false;
            Require(!f.Prepare(30) && !f.Positioner.Above, "inactive input retained direction/display");
            f.State.CandidateEnvironmentActive = true; f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); f.Host.FailCapture = true;
            Require(!f.Prepare(30) && f.Positioner.Above, "temporary missing environment erased memory");
            f.Host.FailCapture = false; int original = f.State.CaretY;
            f.State.CaretX = f.State.CaretY = 0;
            Require(!f.Prepare(30) && f.Positioner.Above, "zero caret erased memory");
            f.State.CaretX = 200; f.State.CaretY = original; f.Render(30); f.Above(30); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(30); f.Host.FailMove = true;
            Require(!f.Prepare(150) && !f.Positioner.Above, "failed SetWindowPos taught above");
            f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(30); f.Window.Opacity = 0; Require(f.Prepare(150), "hidden preparation failed");
            Require(!f.Positioner.ConfirmShown(f.Window, f.State) && !f.Positioner.Above, "hidden frame taught above");
            f.Positioner.EndComposition(); f.Window.Opacity = 1;
            Require(!f.Positioner.ConfirmShown(f.Window, f.State), "ended pending frame was accepted");
            f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(30); f.Host.DuringMove = () => f.Positioner.Reset();
            Require(!f.Prepare(150) && !f.Positioner.Above, "reentrant reset overwritten");
            f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); ++f.Host.Environment.OwnerBounds.Top; f.Render(30); f.Below();
            f.Render(150); f.Host.Environment.UncertainOwner = true; f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); f.Host.Environment.StartMenu = true; f.Render(30);
            Require(!f.Positioner.Above && f.Rect().Top == f.Host.Environment.Work.Top + 10, "Start-menu corner regressed");
            f.Host.Environment.StartMenu = false; f.Render(30); f.Below(); ++_cases;
        }
        using (var f = new Fixture())
        {
            f.Render(150); f.End(); f.Begin(); f.Render(18); f.Above(18); f.Render(150); f.Above(150); ++_cases;
        }
        using (var f = new Fixture())
        {
            // Live native host capture, separate from deterministic environment cases.
            CandidatePlacementEnvironment env;
            var native = new Win32CandidatePlacementHost();
            var state = new OverlayUiState { IsChinese = true }; // old-Core compatibility path
            Require(native.TryCapture(state, f.State.CaretX, f.State.CaretY, out env), "native environment capture failed");
            Require(env.Monitor != 0 && env.Dpi > 0, "native environment incomplete"); ++_cases;
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // This executable is an isolated WPF positioning probe, not an IME
            // installation. It sends no input, opens no Core pipe/MMF and creates
            // only its own windows. Failure/reentry hooks affect this process only.
            Run();
            if (args.Length == 1)
            {
                string candidate = File.ReadAllText(Path.Combine(args[0], "next/TigerClaw.Overlay/MainWindow.Candidate.cs"));
                string main = File.ReadAllText(Path.Combine(args[0], "next/TigerClaw.Overlay/MainWindow.xaml.cs"));
                Require(candidate.Contains("_positioner.EndComposition();") && !candidate.Contains("_positioner.Reset();"), "caller restored per-word reset");
                Require(candidate.Contains("_positioner.ConfirmShown(this, positioningState);") && main.Contains("_positioner.ObserveState(_state);"), "publication/lifecycle wiring missing");
            }
            Console.WriteLine("{\"status\":\"passed\",\"wpf_cases\":" + _cases + ",\"checks\":" + _checks +
                ",\"pointer_bits\":" + IntPtr.Size * 8 + ",\"real_wpf_windows\":true,\"synthetic_sizes\":true,\"physical_input_tested\":false}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
