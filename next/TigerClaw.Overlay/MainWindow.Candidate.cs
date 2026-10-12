using TigerClaw.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TigerClaw.Overlay
{
    public partial class MainWindow
    {
        private Brush _candidateSelectionBackground = Brushes.Transparent;
        private bool _candidateAbove;
        private StackPanel _candidateRows;
        private TextBlock _candidateFooter;

        private void ApplyTheme()
        {
            OverlayThemePalette palette = _themeResolver.Resolve(_state.ThemeName);
            var mode = _candidateTextFormatter.GetDisplayMode(_state, _candidateExpanded);
            ApplyCandidatePalette(_state.VerticalCandidates && mode != CandidateDisplayMode.CodeOnly,
                palette, mode == CandidateDisplayMode.CodeOnly);
        }

        private void ApplyCandidatePalette(bool vertical, OverlayThemePalette palette, bool codeOnly)
        {
            BorderCandi.Background = palette.Background;
            BorderCandi.BorderBrush = palette.Border;
            BorderCandi.BorderThickness = new Thickness(palette.BorderWidth);
            BorderCandi.CornerRadius = vertical
                ? (_candidateAbove ? new CornerRadius(10, 10, 5, 5) : new CornerRadius(5, 5, 10, 10))
                : (codeOnly
                    ? palette.Corner : new CornerRadius(5, 10, 10, 5));
            BorderCandi.Opacity = 1;
            ApplyCandidateForeground(palette.Foreground);
            _candidateSelectionBackground = palette.CandidateSelectionBackground;
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

            CandidateWindowViewModel viewModel = _candidateTextFormatter.BuildViewModel(_state, _candidateExpanded,
                _candidateExpanded && _annotationExpanded, _candidateAbove);
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
                bool above = _state.VerticalCandidates && _positioner.IsAbove;
                if (above != _candidateAbove)
                {
                    _candidateAbove = above;
                    _lastRenderSignature = string.Empty;
                    RenderCandidateText();
                }
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
            CandidateText.Inlines.Clear();
            CandidateText.Text = string.Empty;
            if (_candidateFooter != null) _candidateFooter.Text = string.Empty;
            if (_candidateRows != null) _candidateRows.Children.Clear();
            _predictedWindowWidthDip = 0;
            _predictedWindowHeightDip = 0;
            CandidateText.MinWidth = 0;
        }

        private void ApplyCandidateViewModel(CandidateWindowViewModel viewModel)
        {
            BorderCandi.MinWidth = 0.0;
            BorderCandi.MinHeight = 0.0;
            ApplyCandidateTextStyle(viewModel.Mode, viewModel.IsVertical, CandidateText.FontSize);
            bool vertical = viewModel.IsVertical && viewModel.Mode != CandidateDisplayMode.CodeOnly;
            if (CandidateText.Parent is Panel parent) parent.Children.Remove(CandidateText);
            if (_candidateRows != null) _candidateRows.Children.Clear();
            BorderCandi.Child = null;
            if (vertical)
            {
                ApplyVerticalCandidateRows(viewModel);
                return;
            }
            else if (viewModel.Mode != CandidateDisplayMode.CodeOnly)
            {
                ApplyHorizontalCandidateRows(viewModel);
                return;
            }
            else BorderCandi.Child = CandidateText;
            ApplyCandidateDisplayText(viewModel);
        }

        private void ApplyVerticalCandidateRows(CandidateWindowViewModel viewModel)
        {
            if (_candidateRows == null) _candidateRows = new StackPanel();
            _candidateRows.Orientation = Orientation.Vertical;
            _candidateRows.Margin = CandidateText.Padding;
            BorderCandi.Child = _candidateRows;
            string text = viewModel.DisplayText ?? string.Empty;
            int offset = 0;
            if (_candidateFooter == null) _candidateFooter = new TextBlock();
            _candidateFooter.FontFamily = CandidateText.FontFamily;
            _candidateFooter.FontSize = CandidateText.FontSize * 0.65;
            var footerBrush = CandidateText.Foreground.Clone(); footerBrush.Opacity *= 0.5;
            _candidateFooter.Foreground = footerBrush;
            _candidateFooter.TextAlignment = TextAlignment.Center;
            _candidateFooter.Text = viewModel.CodeStart >= 0 ? text.Substring(viewModel.CodeStart, viewModel.CodeLength) : string.Empty;
            _candidateFooter.Visibility = _candidateFooter.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (viewModel.CodeStart == 0)
            {
                offset = viewModel.CodeLength;
                if (offset < text.Length && text[offset] == '\n') ++offset;
                text = text.Substring(offset);
            }
            else if (viewModel.CodeStart > 0) text = text.Substring(0, viewModel.CodeStart - 1);
            if (_candidateAbove) _candidateRows.Children.Add(_candidateFooter);
            var rows = text.Length > 0 ? text.Split('\n') : Array.Empty<string>();
            var grids = new List<Grid>();
            double maxWord = 0, maxNote = 0;
            for (int row = 0; row < rows.Length; ++row)
            {
                int logical = _candidateAbove ? rows.Length - row - 1 : row;
                double size = CandidateText.FontSize * (logical == 0 ? 1 : 0.7);
                var span = viewModel.AnnotationRanges.FirstOrDefault(item => item.Start >= offset && item.Start < offset + rows[row].Length);
                int length = span == null ? rows[row].Length : span.Start - offset;
                string word = rows[row].Substring(0, length);
                int digits = logical > 0 && _state.ShowCandidateIndex ? (logical + 1).ToString(CultureInfo.InvariantCulture).Length : 0;
                var body = new TextBlock { FontFamily = CandidateText.FontFamily, FontSize = size,
                    Foreground = CandidateText.Foreground, TextAlignment = TextAlignment.Center };
                body.Inlines.Add(new Run(word.Substring(0, word.Length - digits)));
                if (digits > 0)
                {
                    var indexBrush = CandidateText.Foreground.Clone(); indexBrush.Opacity *= 0.5;
                    body.Inlines.Add(new Run(word.Substring(word.Length - digits)) { Foreground = indexBrush });
                }
                var grid = new Grid(); grid.Children.Add(body);
                body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                maxWord = Math.Max(maxWord, body.DesiredSize.Width);
                if (span != null)
                {
                    var noteBrush = CandidateText.Foreground.Clone(); noteBrush.Opacity *= 0.7;
                    var note = new TextBlock { Text = rows[row].Substring(length), FontFamily = CandidateText.FontFamily,
                        FontSize = CandidateText.FontSize * 0.5, Foreground = noteBrush, TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center };
                    note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    maxNote = Math.Max(maxNote, note.DesiredSize.Width);
                    grid.Children.Add(note);
                }
                if (viewModel.SelectionStart == offset) grid.Background = _candidateSelectionBackground;
                grids.Add(grid); _candidateRows.Children.Add(grid);
                offset += rows[row].Length + 1;
            }
            double width = maxWord + (maxNote > 0 ? maxNote + 4 : 0);
            bool hasNotes = grids.Any(grid => grid.Children.Count > 1);
            foreach (var grid in grids)
            {
                grid.MinWidth = width;
                ((TextBlock)grid.Children[0]).TextAlignment = hasNotes ? TextAlignment.Left : TextAlignment.Center;
            }
            if (!_candidateAbove) _candidateRows.Children.Add(_candidateFooter);
        }

        private void ApplyHorizontalCandidateRows(CandidateWindowViewModel viewModel)
        {
            if (_candidateRows == null) _candidateRows = new StackPanel();
            _candidateRows.Orientation = Orientation.Horizontal;
            _candidateRows.Margin = CandidateText.Padding;
            BorderCandi.Child = _candidateRows;
            string text = viewModel.DisplayText ?? string.Empty;
            var muted = CandidateText.Foreground.Clone(); muted.Opacity *= 0.7;
            var codeBrush = CandidateText.Foreground.Clone(); codeBrush.Opacity *= 0.5;
            var indexBrush = CandidateText.Foreground.Clone(); indexBrush.Opacity *= 0.5;
            Func<string, double, Brush, TextBlock> block = (value, scale, color) => new TextBlock {
                Text = value, FontFamily = CandidateText.FontFamily, FontSize = CandidateText.FontSize * scale,
                Foreground = color, VerticalAlignment = VerticalAlignment.Center };
            int cursor = 0;
            if (viewModel.CodeStart >= 0)
            {
                _candidateRows.Children.Add(block(text.Substring(viewModel.CodeStart, viewModel.CodeLength), 0.65, codeBrush));
                cursor = viewModel.CodeStart + viewModel.CodeLength;
            }
            for (int i = 0; i < viewModel.CandidateRanges.Count; ++i)
            {
                var span = viewModel.CandidateRanges[i];
                if (span.Start > cursor) _candidateRows.Children.Add(block(text.Substring(cursor, span.Start - cursor), 1, CandidateText.Foreground));
                var note = viewModel.AnnotationRanges.FirstOrDefault(item => item.Start >= span.Start && item.Start < span.Start + span.Length);
                int wordEnd = note == null ? span.Start + span.Length : note.Start;
                int digits = i > 0 && _state.ShowCandidateIndex ? (i + 1).ToString(CultureInfo.InvariantCulture).Length : 0;
                var body = block(string.Empty, i == 0 ? 1 : 0.7, CandidateText.Foreground);
                body.Inlines.Add(new Run(text.Substring(span.Start, wordEnd - span.Start - digits)));
                if (digits > 0) body.Inlines.Add(new Run(text.Substring(wordEnd - digits, digits)) { Foreground = indexBrush });
                var group = new StackPanel { Orientation = Orientation.Horizontal };
                group.Children.Add(body);
                if (note != null) group.Children.Add(block(text.Substring(note.Start, note.Length), 0.5, muted));
                if (viewModel.SelectionStart == span.Start) group.Background = _candidateSelectionBackground;
                _candidateRows.Children.Add(group);
                cursor = span.Start + span.Length;
            }
        }

        private void ApplyCandidateDisplayText(CandidateWindowViewModel viewModel)
        {
            string text = viewModel.DisplayText ?? string.Empty;
            CandidateText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            CandidateText.Inlines.Clear();
            var boundaries = new SortedSet<int> { 0, text.Length };
            var indexRanges = new List<CandidateAnnotationSpan>();
            foreach (var span in viewModel.CandidateRanges.Concat(viewModel.AnnotationRanges))
            { boundaries.Add(span.Start); boundaries.Add(span.Start + span.Length); }
            if (viewModel.CodeStart >= 0)
            { boundaries.Add(viewModel.CodeStart); boundaries.Add(viewModel.CodeStart + viewModel.CodeLength); }
            if (_state.ShowCandidateIndex)
                for (int i = 1; i < viewModel.CandidateRanges.Count; ++i)
                {
                    var span = viewModel.CandidateRanges[i];
                    var note = viewModel.AnnotationRanges.FirstOrDefault(item => item.Start >= span.Start && item.Start < span.Start + span.Length);
                    int end = note == null ? span.Start + span.Length : note.Start;
                    int length = (i + 1).ToString(CultureInfo.InvariantCulture).Length;
                    indexRanges.Add(new CandidateAnnotationSpan { Start = end - length, Length = length });
                    boundaries.Add(end - length); boundaries.Add(end);
                }
            int start = 0;
            foreach (int end in boundaries)
            {
                if (end <= start) continue;
                var run = new Run(text.Substring(start, end - start));
                int candidate = viewModel.CandidateRanges.FindIndex(span => start >= span.Start && end <= span.Start + span.Length);
                bool note = viewModel.AnnotationRanges.Any(span => start >= span.Start && end <= span.Start + span.Length);
                bool index = indexRanges.Any(span => start >= span.Start && end <= span.Start + span.Length);
                bool code = viewModel.CodeStart >= 0 && start >= viewModel.CodeStart && end <= viewModel.CodeStart + viewModel.CodeLength;
                run.FontSize = CandidateText.FontSize * (code ? 0.65 : note ? 0.5 : candidate > 0 ? 0.7 : 1);
                if (note || index || code)
                {
                    var brush = CandidateText.Foreground.Clone(); brush.Opacity *= index || code ? 0.5 : 0.7; run.Foreground = brush;
                }
                if (viewModel.SelectionStart >= 0 && start >= viewModel.SelectionStart && end <= viewModel.SelectionStart + viewModel.SelectionLength)
                    run.Background = _candidateSelectionBackground;
                CandidateText.Inlines.Add(run);
                start = end;
            }
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
                   string.Join(";", viewModel.CandidateRanges.Select(span => span.Start + "," + span.Length)) + "|" +
                   string.Join(";", viewModel.AnnotationRanges.Select(span => span.Start + "," + span.Length)) + "|" +
                   viewModel.CodeStart + ":" + viewModel.CodeLength + "|" +
                   (_state == null ? 0 : _state.CompositionState) + "|" +
                   viewModel.SelectionStart.ToString(CultureInfo.InvariantCulture) + "|" +
                   viewModel.SelectionLength.ToString(CultureInfo.InvariantCulture) + "|" +
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
            bool verticalSurface = mode != CandidateDisplayMode.CodeOnly;
            var palette = _themeResolver.Resolve(_state.ThemeName);
            ApplyCandidatePalette(verticalSurface && isVertical, palette, mode == CandidateDisplayMode.CodeOnly);
            BorderCandi.Margin = new Thickness(verticalSurface ? 4 : 0);
            CandidateText.Margin = new Thickness(0);
            CandidateText.HorizontalAlignment = HorizontalAlignment.Left;
            CandidateText.VerticalAlignment = VerticalAlignment.Top;
            CandidateText.TextAlignment = TextAlignment.Left;
            CandidateText.TextWrapping = TextWrapping.NoWrap;
            CandidateText.Effect = null;

            if (mode == CandidateDisplayMode.CodeOnly)
            {
                double horizontalPadding = ToWholeDip(Math.Round(fontSize * CandidateWindowMetrics.CodeOnlyHorizontalPaddingScale));
                CandidateText.Padding = new Thickness(horizontalPadding, CandidateWindowMetrics.CodeOnlyVerticalPaddingDip, horizontalPadding, CandidateWindowMetrics.CodeOnlyVerticalPaddingDip);
                CandidateText.LineHeight = ToWholeDip(fontSize * CandidateWindowMetrics.CodeOnlyLineHeightScale);
                CandidateText.MinWidth = 0;
                return;
            }

            if (mode != CandidateDisplayMode.CodeOnly)
            {
                CandidateText.Padding = new Thickness(6, _candidateAbove ? 6 : 2.5, 6, _candidateAbove ? 2.5 : 6);
                CandidateText.LineHeight = double.NaN;
                CandidateText.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                CandidateText.MinWidth = 0;
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
                fontSize = 24;
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

            int nextMode = GetNextCandidateCycleMode();
            if (await TryApplyCandidateCycleModeAsync(nextMode))
            {
                ApplyUiState(_sessionState.Update(_state));
            }
        }

        private async void Disp_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double nextFontSize = _state.FontSize <= 0 ? 24 : _state.FontSize;
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

        private int GetNextCandidateCycleMode() => CandidateLayouts.Next(_state.CandidateLayout);

        private async System.Threading.Tasks.Task<bool> TryApplyCandidateCycleModeAsync(int mode)
        {
            CoreRequestResult result = await CorePipeClient.SetConfigValueAsync(
                CandidateLayouts.ConfigKey, CandidateLayouts.Format(mode), 1000);
            if (!result.Success) return false;
            _state.CandidateLayout = mode;
            return true;
        }
    }
}
