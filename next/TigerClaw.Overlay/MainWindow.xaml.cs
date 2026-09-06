using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using TigerClaw.Shared;

namespace TigerClaw.Overlay
{
    public partial class MainWindow : Window
    {
        private const string YesValue = "是";
        private const string NoValue = "否";
        private const string ConfigVerticalCandidates = "竖排候选";
        private const string ConfigHideCandidates = "隐藏候选";
        private const string ConfigShowInputCodeInCandidateWindow = "候选窗显示编码";
        private readonly TypingSoundPlayer _typingSound = new TypingSoundPlayer();
        private readonly OverlaySessionState _sessionState = new OverlaySessionState();
        private readonly CandidateTextFormatter _candidateTextFormatter = new CandidateTextFormatter();
        private readonly OverlayThemeResolver _themeResolver = new OverlayThemeResolver();
        private readonly OverlayFontResolver _fontResolver = new OverlayFontResolver();
        private readonly CandidateWindowPositioner _positioner = new CandidateWindowPositioner();
        private readonly OverlayStateSource _stateSource;
        private readonly OverlayHeartbeatWatcher _heartbeatWatcher = new OverlayHeartbeatWatcher(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
        private readonly OverlayMenuSignalListener _menuSignalListener = new OverlayMenuSignalListener(TimeSpan.FromMilliseconds(50));

        private OverlayHeartbeatBroadcaster _overlayHeartbeat;
        private OverlayUiState _state = new OverlayUiState();
        private StatusWindow _statusWindow;
        private long _lastSoundSeq;
        private bool _soundFeatureDisabled;
        private string _lastRenderSignature = string.Empty;
        private bool _pendingPositionAfterLayout;
        private FontFamily _currentFontFamily;
        private double _predictedWindowWidthDip;
        private double _predictedWindowHeightDip;
        private readonly DispatcherTimer _candidateExpandTimer = new DispatcherTimer(DispatcherPriority.Background);
        private readonly DispatcherTimer _annotationExpandTimer = new DispatcherTimer(DispatcherPriority.Background);
        private bool _candidateExpanded;
        private bool _annotationExpanded;
        private bool _revealSessionActive;
        private long _revealSessionStartTick;
        private bool _horizontalShowCodePreference;
        private bool _verticalShowCodePreference;
        private enum CandidateCycleMode
        {
            Horizontal,
            Vertical,
            CodeOnly
        }

        public MainWindow()
        {
            InitializeComponent();
            _stateSource = new OverlayStateSource(TimeSpan.FromMilliseconds(5));
            _candidateExpandTimer.Tick += CandidateExpandTimer_Tick;
            _annotationExpandTimer.Tick += AnnotationExpandTimer_Tick;

            try
            {
                _overlayHeartbeat = new OverlayHeartbeatBroadcaster();
                _overlayHeartbeat.Start();
            }
            catch
            {
                _overlayHeartbeat = null;
            }

            _stateSource.StateChanged += OnStateChanged;
            _heartbeatWatcher.HeartbeatStale += OnHeartbeatStale;
            _menuSignalListener.SignalReceived += OnShowMenuSignal;
        }

        private void OnStateChanged(OverlayUiState uiState, long uiSeq, long tick64)
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _state = uiState ?? new OverlayUiState();
                    SyncNormalCandidateModePreferences();
                    OverlayUiChangeFlags changes = _sessionState.Update(_state);
                    ApplyUiState(changes);
                }));
            }
            catch
            {
            }
        }

        private void OnHeartbeatStale()
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Close));
            }
            catch
            {
            }
        }

        private void OnShowMenuSignal()
        {
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _statusWindow?.ShowContextMenuAtCursor();
                }));
            }
            catch
            {
            }
        }

        private void ApplyUiState(OverlayUiChangeFlags changes)
        {
            if ((changes & OverlayUiChangeFlags.Sound) != 0)
            {
                TryPlayTypingSound();
            }

            if ((changes & OverlayUiChangeFlags.Status) != 0)
            {
                ApplyStatusWindow();
            }

            if ((changes & OverlayUiChangeFlags.Style) != 0)
            {
                ApplyTheme();
                ApplyFont();
            }

            if ((changes & (OverlayUiChangeFlags.Content | OverlayUiChangeFlags.Style)) != 0)
            {
                RenderCandidateText();
            }

            if ((changes & OverlayUiChangeFlags.CandidateAnchor) != 0)
            {
                _positioner.RefreshCaretAnchorPreservingPlacement();
            }

            if ((changes & (OverlayUiChangeFlags.Content | OverlayUiChangeFlags.Style | OverlayUiChangeFlags.Position)) != 0)
            {
                UpdatePosition();
            }
        }

        private void TryPlayTypingSound()
        {
            if (_soundFeatureDisabled || _state == null)
            {
                return;
            }

            try
            {
                if (!TryGetSoundState(_state, out long soundSeq, out int soundVk, out int soundVolumePercent))
                {
                    return;
                }

                if (soundSeq <= 0 || soundSeq == _lastSoundSeq)
                {
                    return;
                }

                _lastSoundSeq = soundSeq;
                _typingSound.Play(soundVk, soundVolumePercent);
            }
            catch (MissingMethodException)
            {
                _soundFeatureDisabled = true;
            }
            catch (TypeLoadException)
            {
                _soundFeatureDisabled = true;
            }
        }

        private static bool TryGetSoundState(object state, out long soundSeq, out int soundVk, out int soundVolumePercent)
        {
            soundSeq = 0;
            soundVk = 0;
            soundVolumePercent = 0;

            if (state == null)
            {
                return false;
            }

            try
            {
                Type t = state.GetType();
                var seqProp = t.GetProperty("SoundSeq");
                var vkProp = t.GetProperty("SoundVk");
                var volProp = t.GetProperty("SoundVolumePercent");
                if (seqProp == null || vkProp == null || volProp == null)
                {
                    return false;
                }

                object seqObj = seqProp.GetValue(state, null);
                object vkObj = vkProp.GetValue(state, null);
                object volObj = volProp.GetValue(state, null);
                if (seqObj == null || vkObj == null || volObj == null)
                {
                    return false;
                }

                soundSeq = Convert.ToInt64(seqObj, CultureInfo.InvariantCulture);
                soundVk = Convert.ToInt32(vkObj, CultureInfo.InvariantCulture);
                soundVolumePercent = Convert.ToInt32(volObj, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyStatusWindow()
        {
            if (_statusWindow == null)
            {
                return;
            }

            _statusWindow.ApplyStatus(_state.IsOff, _state.IsNativeHook, _state.IsChinese, _state.StatusText, _state.HideStatusBar);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int exStyle = (int)NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_NOACTIVATE);
            Visibility = Visibility.Hidden;
            Opacity = 0;
            IsHitTestVisible = false;

            if (_statusWindow == null)
            {
                _statusWindow = new StatusWindow();
                _statusWindow.Show();
                Rect workArea = SystemParameters.WorkArea;
                _statusWindow.Left = workArea.Right - _statusWindow.Width;
                _statusWindow.Top = workArea.Bottom - _statusWindow.Height;
                ApplyStatusWindow();
            }

            _stateSource.Start();
            _heartbeatWatcher.Start();
            _menuSignalListener.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _statusWindow?.Close();
            }
            catch
            {
            }
            finally
            {
                _statusWindow = null;
            }

            try
            {
                _menuSignalListener.Dispose();
            }
            catch
            {
            }

            try
            {
                _overlayHeartbeat?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _overlayHeartbeat = null;
            }

            try
            {
                _typingSound.Dispose();
            }
            catch
            {
            }

            _heartbeatWatcher.Dispose();
            _stateSource.Dispose();
            base.OnClosed(e);
        }

        private void AnnotationExpandTimer_Tick(object sender, EventArgs e)
        {
            _annotationExpandTimer.Stop();

            if (!_revealSessionActive || !ShouldShowCandidateWindow() || _annotationExpanded)
            {
                return;
            }

            if (GetElapsedRevealSessionMs() < GetAnnotationExpandDelayMs(_state))
            {
                ScheduleAnnotationExpandTimer();
                return;
            }

            _annotationExpanded = true;
            RenderCandidateText();
        }

        private void CandidateExpandTimer_Tick(object sender, EventArgs e)
        {
            _candidateExpandTimer.Stop();

            if (!_revealSessionActive || !ShouldShowCandidateWindow() || _candidateExpanded)
            {
                return;
            }

            if (GetElapsedRevealSessionMs() < GetCandidateExpandDelayMs(_state))
            {
                ScheduleCandidateExpandTimer();
                return;
            }

            _candidateExpanded = true;
            RenderCandidateText();
        }
    }
}


