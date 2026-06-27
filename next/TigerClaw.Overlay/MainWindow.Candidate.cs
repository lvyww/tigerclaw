using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TigerClaw.Overlay
{
    public partial class MainWindow
    {
        private void ApplyTheme()
        {
            OverlayThemePalette palette = _themeResolver.Resolve(_state.ThemeName);
            BorderCandi.Background = palette.Background;
            BorderCandi.BorderBrush = palette.Border;
            BorderCandi.BorderThickness = new Thickness(palette.BorderWidth);
            BorderCandi.CornerRadius = palette.Corner;
            ApplyCandidateForeground(palette.Foreground);
        }

        private void ApplyFont()
        {
            double fontSize = ClampFontSize(_state.FontSize);

            var family = _fontResolver.Resolve(_state.FontName);
            if (family != null)
            {
                _currentFontFamily = family;
                CandidateText.FontFamily = family;
            }

            if (_currentFontFamily == null)
            {
                _currentFontFamily = CandidateText.FontFamily;
            }

            CandidateText.FontSize = fontSize;
        }

        private void RenderCandidateText()
        {
            if (!ShouldShowCandidateWindow())
            {
                ResetCandidateRevealState();
                _lastRenderSignature = string.Empty;
                HideCandidate();
                _positioner.Reset();
                return;
            }

            UpdateCandidateRevealState();

            CandidateWindowViewModel viewModel = _candidateTextFormatter.BuildViewModel(_state, _candidateExpanded, _candidateExpanded && _annotationExpanded);
            if (viewModel.Mode == CandidateDisplayMode.Hidden)
            {
                _lastRenderSignature = string.Empty;
                ClearCandidateContent();
                HideCandidateAwaitPosition();
                return;
            }

            PrepareCandidateForShowLayout();

            string nextSignature = BuildCandidateRenderSignature(viewModel);
            if (!string.Equals(_lastRenderSignature, nextSignature, StringComparison.Ordinal))
            {
                ApplyCandidateViewModel(viewModel);
                RefreshCandidateLayout();
                SchedulePositionAfterLayout();
                _lastRenderSignature = nextSignature;
            }
        }

        private void UpdateCandidateRevealState()
        {
            EnsureRevealSession();

            if (_candidateExpanded)
            {
                _candidateExpandTimer.Stop();
            }
            else if (ShouldExpandCandidatesNow(_state))
            {
                _candidateExpanded = true;
                _candidateExpandTimer.Stop();
            }
            else
            {
                ScheduleCandidateExpandTimer();
            }

            if (_annotationExpanded)
            {
                _annotationExpandTimer.Stop();
            }
            else if (ShouldExpandAnnotationsNow(_state))
            {
                _annotationExpanded = true;
                _annotationExpandTimer.Stop();
            }
            else
            {
                ScheduleAnnotationExpandTimer();
            }
        }

        private void UpdatePosition()
        {
            bool nowComposing = ShouldShowCandidateWindowForCurrentReveal();
            if (!nowComposing)
            {
                return;
            }

            bool becameVisible = IsCandidateHidden();
            bool hasPosition = _positioner.Update(this, BorderCandi, _state, true, _predictedWindowWidthDip, _predictedWindowHeightDip);
            if (hasPosition)
            {
                ShowCandidate(becameVisible);
                return;
            }

            HideCandidateAwaitPosition();
        }

        private void RefreshCandidateLayout()
        {
            BorderCandi.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _predictedWindowWidthDip = BorderCandi.DesiredSize.Width;
            _predictedWindowHeightDip = BorderCandi.DesiredSize.Height;
            CandidateText.InvalidateMeasure();
            CandidateText.InvalidateArrange();
            UpdateLayout();
        }

        private void SchedulePositionAfterLayout()
        {
            if (_pendingPositionAfterLayout)
            {
                return;
            }

            _pendingPositionAfterLayout = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _pendingPositionAfterLayout = false;
                UpdatePosition();
            }));
        }

        private void ShowCandidate(bool becameVisible)
        {
            IsHitTestVisible = true;

            CancelCandidateAnimations();
            CandidateTransform.Y = 0.0;

            if (becameVisible)
            {
                Opacity = 1.0;
                CandidateTransform.Y = 0.0;
                WindowTopmostController.ForceTopmostNoActivate(this);
            }
            else if (Opacity < 1.0)
            {
                Opacity = 1.0;
                CandidateTransform.Y = 0.0;
            }
        }

        private void PrepareCandidateForShowLayout()
        {
            if (!IsCandidateHidden())
            {
                return;
            }

            CancelCandidateAnimations();
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }
            Opacity = 1.0;
            CandidateTransform.Y = 0.0;
        }

        private void ApplyCandidateForeground(Brush foreground)
        {
            CandidateText.Foreground = foreground;
        }

        private void ClearCandidateContent()
        {
            CandidateText.Text = string.Empty;
            _predictedWindowWidthDip = 0;
            _predictedWindowHeightDip = 0;
            CandidateText.MinWidth = 0;
        }

        private void ApplyCandidateViewModel(CandidateWindowViewModel viewModel)
        {
            BorderCandi.MinWidth = 0.0;
            BorderCandi.MinHeight = 0.0;
            ApplyCandidateTextStyle(viewModel.Mode, viewModel.IsVertical, CandidateText.FontSize);
            CandidateText.Text = viewModel.DisplayText ?? string.Empty;
        }

        private string BuildCandidateRenderSignature(CandidateWindowViewModel viewModel)
        {
            if (viewModel == null)
            {
                return string.Empty;
            }

            return ((int)viewModel.Mode).ToString(CultureInfo.InvariantCulture) + "|" +
                   (viewModel.IsVertical ? "V" : "H") + "|" +
                   (viewModel.DisplayText ?? string.Empty) + "|" +
                   CandidateText.FontSize.ToString("0.##", CultureInfo.InvariantCulture) + "|" +
                   (_currentFontFamily == null ? string.Empty : _currentFontFamily.Source) + "|" +
                   (_state == null ? string.Empty : _state.ThemeName ?? string.Empty);
        }

        private static bool HasCandidateItems(TigerClaw.Shared.OverlayUiState state)
        {
            return (state?.Candidates?.Length ?? 0) > 0;
        }

        private static bool HasCandidateAnnotations(TigerClaw.Shared.OverlayUiState state)
        {
            if (state?.CandidateAnnotations == null)
            {
                return false;
            }

            for (int i = 0; i < state.CandidateAnnotations.Length; i++)
            {
                if (!string.IsNullOrEmpty(state.CandidateAnnotations[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static int GetCandidateExpandDelayMs(TigerClaw.Shared.OverlayUiState state)
        {
            if (state == null || state.CandidateExpandDelayMs <= 0)
            {
                return 0;
            }

            return state.CandidateExpandDelayMs;
        }

        private static int GetAnnotationExpandDelayMs(TigerClaw.Shared.OverlayUiState state)
        {
            if (state == null || state.AnnotationExpandDelayMs <= 0)
            {
                return 0;
            }

            return state.AnnotationExpandDelayMs;
        }

        private void EnsureRevealSession()
        {
            if (_revealSessionActive)
            {
                return;
            }

            _revealSessionActive = true;
            _revealSessionStartTick = GetRevealTickMs();
            _candidateExpanded = false;
            _annotationExpanded = false;
            _candidateExpandTimer.Stop();
            _annotationExpandTimer.Stop();
        }

        private bool ShouldExpandCandidatesNow(TigerClaw.Shared.OverlayUiState state)
        {
            if (!HasCandidateItems(state))
            {
                return true;
            }

            int delayMs = GetCandidateExpandDelayMs(state);
            return delayMs <= 0 || GetElapsedRevealSessionMs() >= delayMs;
        }

        private bool ShouldExpandAnnotationsNow(TigerClaw.Shared.OverlayUiState state)
        {
            if (!HasCandidateAnnotations(state))
            {
                return true;
            }

            int delayMs = GetAnnotationExpandDelayMs(state);
            return delayMs <= 0 || GetElapsedRevealSessionMs() >= delayMs;
        }

        private void ScheduleCandidateExpandTimer()
        {
            int delayMs = GetCandidateExpandDelayMs(_state);
            if (delayMs <= 0 || _candidateExpanded)
            {
                _candidateExpandTimer.Stop();
                return;
            }

            long remainingMs = Math.Max(1L, delayMs - GetElapsedRevealSessionMs());
            _candidateExpandTimer.Interval = TimeSpan.FromMilliseconds(remainingMs);
            _candidateExpandTimer.Start();
        }

        private void ScheduleAnnotationExpandTimer()
        {
            int delayMs = GetAnnotationExpandDelayMs(_state);
            if (delayMs <= 0 || _annotationExpanded)
            {
                _annotationExpandTimer.Stop();
                return;
            }

            long remainingMs = Math.Max(1L, delayMs - GetElapsedRevealSessionMs());
            _annotationExpandTimer.Interval = TimeSpan.FromMilliseconds(remainingMs);
            _annotationExpandTimer.Start();
        }

        private long GetElapsedRevealSessionMs()
        {
            if (!_revealSessionActive)
            {
                return 0;
            }

            long elapsed = GetRevealTickMs() - _revealSessionStartTick;
            return elapsed < 0 ? 0 : elapsed;
        }

        private static long GetRevealTickMs()
        {
            return unchecked((uint)Environment.TickCount);
        }

        private void ResetCandidateRevealState()
        {
            _candidateExpandTimer.Stop();
            _annotationExpandTimer.Stop();
            _candidateExpanded = false;
            _annotationExpanded = false;
            _revealSessionActive = false;
            _revealSessionStartTick = 0;
        }

        private void ApplyCandidateTextStyle(CandidateDisplayMode mode, bool isVertical, double fontSize)
        {
            CandidateText.Margin = new Thickness(0);
            CandidateText.HorizontalAlignment = HorizontalAlignment.Left;
            CandidateText.VerticalAlignment = VerticalAlignment.Top;
            CandidateText.TextAlignment = TextAlignment.Left;
            CandidateText.TextWrapping = TextWrapping.NoWrap;

            if (mode == CandidateDisplayMode.CodeOnly)
            {
                double horizontalPadding = ToWholeDip(Math.Round(fontSize * CandidateWindowMetrics.CodeOnlyHorizontalPaddingScale));
                CandidateText.Padding = new Thickness(horizontalPadding, CandidateWindowMetrics.CodeOnlyVerticalPaddingDip, horizontalPadding, CandidateWindowMetrics.CodeOnlyVerticalPaddingDip);
                CandidateText.LineHeight = ToWholeDip(fontSize * CandidateWindowMetrics.CodeOnlyLineHeightScale);
                CandidateText.MinWidth = 0;
                return;
            }

            if (isVertical)
            {
                CandidateText.Padding = new Thickness(12, 8, 8, 7);
                CandidateText.LineHeight = ToWholeDip(fontSize * CandidateWindowMetrics.VerticalLineHeightScale);
                CandidateText.MinWidth = ToWholeDip(fontSize * CandidateWindowMetrics.VerticalMinWidthScale + CandidateWindowMetrics.VerticalMinWidthExtraDip);
                return;
            }

            CandidateText.Padding = new Thickness(8, 8, 8, 8);
            CandidateText.LineHeight = ToWholeDip(fontSize * CandidateWindowMetrics.HorizontalLineHeightScale);
            CandidateText.MinWidth = ToWholeDip(fontSize * CandidateWindowMetrics.HorizontalMinWidthScale + CandidateWindowMetrics.HorizontalMinWidthExtraDip);
        }

        private static double ToWholeDip(double value)
        {
            if (value <= 0)
            {
                return 0;
            }

            return Math.Ceiling(value);
        }

        private void HideCandidate()
        {
            ResetCandidateRevealState();
            ClearCandidateContent();
            Opacity = 0.0;
            CandidateTransform.Y = 0.0;
            IsHitTestVisible = false;
            Visibility = Visibility.Hidden;
        }

        private void HideCandidateAwaitPosition()
        {
            CancelCandidateAnimations();
            Opacity = 0.0;
            CandidateTransform.Y = 0.0;
            IsHitTestVisible = false;
            Visibility = Visibility.Hidden;
        }

        private bool ShouldShowCandidateWindow()
        {
            return _candidateTextFormatter.ShouldShowCandidateWindow(_state);
        }

        private bool ShouldShowCandidateWindowForCurrentReveal()
        {
            return _candidateTextFormatter.ShouldShowCandidateWindow(_state, _candidateExpanded);
        }

        private bool IsCandidateHidden()
        {
            return Visibility != Visibility.Visible || Opacity <= 0.0;
        }

        private void CancelCandidateAnimations()
        {
            BeginAnimation(Window.OpacityProperty, null);
            CandidateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        }

        private static double ClampFontSize(double fontSize)
        {
            if (fontSize <= 0)
            {
                fontSize = 17;
            }

            if (fontSize < 3)
            {
                return 3;
            }

            if (fontSize > 200)
            {
                return 200;
            }

            return fontSize;
        }

        private async void Disp_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                return;
            }

            CandidateCycleMode nextMode = GetNextCandidateCycleMode();
            if (await TryApplyCandidateCycleModeAsync(nextMode))
            {
                ApplyUiState(_sessionState.Update(_state));
            }
        }

        private async void Disp_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double nextFontSize = _state.FontSize <= 0 ? 17 : _state.FontSize;
            nextFontSize += e.Delta / 120.0 * 0.5;
            nextFontSize = ClampFontSize(nextFontSize);

            string value = nextFontSize.ToString("0.##", CultureInfo.InvariantCulture);
            CoreRequestResult result = await CorePipeClient.SetConfigValueAsync("\u5b57\u4f53\u5927\u5c0f", value, 1000);
            if (result.Success)
            {
                _state.FontSize = nextFontSize;
                ApplyUiState(_sessionState.Update(_state));
            }
        }

        private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _statusWindow?.ShowContextMenuAtCursor();
            e.Handled = true;
        }

        private CandidateCycleMode GetNextCandidateCycleMode()
        {
            if (_state.HideCandidateItems && _state.ShowInputCodeInCandidateWindow)
            {
                return CandidateCycleMode.Horizontal;
            }

            return _state.VerticalCandidates
                ? CandidateCycleMode.CodeOnly
                : CandidateCycleMode.Vertical;
        }

        private async System.Threading.Tasks.Task<bool> TryApplyCandidateCycleModeAsync(CandidateCycleMode mode)
        {
            SyncNormalCandidateModePreferences();

            switch (mode)
            {
                case CandidateCycleMode.Horizontal:
                    if (!await TrySetConfigValueAsync(ConfigHideCandidates, NoValue))
                    {
                        return false;
                    }

                    if (!await TrySetConfigValueAsync(ConfigVerticalCandidates, NoValue))
                    {
                        return false;
                    }

                    if (!await TrySetConfigValueAsync(ConfigShowInputCodeInCandidateWindow, _horizontalShowCodePreference ? YesValue : NoValue))
                    {
                        return false;
                    }

                    _state.HideCandidateItems = false;
                    _state.VerticalCandidates = false;
                    _state.ShowInputCodeInCandidateWindow = _horizontalShowCodePreference;
                    return true;

                case CandidateCycleMode.Vertical:
                    if (!await TrySetConfigValueAsync(ConfigHideCandidates, NoValue))
                    {
                        return false;
                    }

                    if (!await TrySetConfigValueAsync(ConfigVerticalCandidates, YesValue))
                    {
                        return false;
                    }

                    if (!await TrySetConfigValueAsync(ConfigShowInputCodeInCandidateWindow, _verticalShowCodePreference ? YesValue : NoValue))
                    {
                        return false;
                    }

                    _state.HideCandidateItems = false;
                    _state.VerticalCandidates = true;
                    _state.ShowInputCodeInCandidateWindow = _verticalShowCodePreference;
                    return true;

                case CandidateCycleMode.CodeOnly:
                    if (!await TrySetConfigValueAsync(ConfigShowInputCodeInCandidateWindow, YesValue))
                    {
                        return false;
                    }

                    if (!await TrySetConfigValueAsync(ConfigHideCandidates, YesValue))
                    {
                        return false;
                    }

                    _state.HideCandidateItems = true;
                    _state.ShowInputCodeInCandidateWindow = true;
                    return true;

                default:
                    return false;
            }
        }

        private static async System.Threading.Tasks.Task<bool> TrySetConfigValueAsync(string key, string value)
        {
            CoreRequestResult result = await CorePipeClient.SetConfigValueAsync(key, value, 1000);
            return result.Success;
        }

        private void SyncNormalCandidateModePreferences()
        {
            if (_state.HideCandidateItems)
            {
                return;
            }

            if (_state.VerticalCandidates)
            {
                _verticalShowCodePreference = _state.ShowInputCodeInCandidateWindow;
            }
            else
            {
                _horizontalShowCodePreference = _state.ShowInputCodeInCandidateWindow;
            }
        }
    }
}
