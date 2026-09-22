using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TigerClaw.Shared;

namespace TigerClaw.Dialog
{
    public partial class AddCiWindow : Window
    {
        private int _historyLen = 2;
        private int _remoteHistoryCount;
        private readonly UiStateReader _uiStateReader = new UiStateReader();
        private bool _frontRaised;
        private bool _fullPinyin;

        public AddCiWindow()
        {
            InitializeComponent();
            Loaded += OnWindowLoaded;
        }

        private void OnAddAndCloseClick(object sender, RoutedEventArgs e)
        {
            if (TryAddCoreWord())
            {
                Close();
            }
        }

        private void OnAddAndKeepClick(object sender, RoutedEventArgs e)
        {
            if (TryAddCoreWord())
            {
                StatusText.Text = "已添加：" + (WordTextBox.Text ?? string.Empty);
                WordTextBox.Clear();
                CodeTextBox.Clear();
                _historyLen = 0;
                EnsureInputFocus();
            }
        }

        private bool TryAddCoreWord()
        {
            string code = (CodeTextBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            string text = WordTextBox.Text ?? string.Empty;

            if (code.Length == 0)
            {
                StatusText.Text = "编码为空。";
                CodeTextBox.Focus();
                return false;
            }

            if (text.Length == 0)
            {
                StatusText.Text = "词条为空。";
                WordTextBox.Focus();
                return false;
            }

            bool ok = CorePipeClient.TryAddCi(code, text, 1000, out string error);
            StatusText.Text = ok ? "已添加：" + text : "添加失败：" + error;
            return ok;
        }

        private void OnDeletePinyinClick(object sender, RoutedEventArgs e)
        {
            if (!_fullPinyin) return;
            bool ok = CorePipeClient.TryDeletePinyinWord(CodeTextBox.Text.Trim(), WordTextBox.Text, out string error);
            StatusText.Text = ok ? "已删除用户词：" + WordTextBox.Text : "删除失败：" + error;
        }

        private void OnPinyinManagerClick(object sender, RoutedEventArgs e)
        { new PinyinManagerWindow { Owner = this }.ShowDialog(); }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _fullPinyin = CorePipeClient.IsFullPinyin();
            if (_fullPinyin)
            {
                Title = "虎爪全拼用户词";
                CodeLabel.Content = "拼音";
                CodeTextBox.ToolTip = "每字一个无声调拼音，用空格分隔；ü 写 v，例如：hu zhua";
                DeletePinyinButton.Visibility = Visibility.Visible;
                PinyinManagerButton.Visibility = Visibility.Visible;
                StatusText.Text = "拼音按字分隔，例如：hu zhua";
            }
            PlaceWindow();
            InitText();
            BringToFrontOnce();
            EnsureInputFocus();
        }

        private void OnPrevHistoryClick(object sender, RoutedEventArgs e)
        {
            _historyLen++;
            RefreshNameFromHistory();
            EnsureInputFocus();
        }

        private void OnNextHistoryClick(object sender, RoutedEventArgs e)
        {
            _historyLen--;
            if (_historyLen < 0)
            {
                _historyLen = 0;
            }

            RefreshNameFromHistory();
            EnsureInputFocus();
        }

        private void OnWordTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_fullPinyin) return;
            if (CorePipeClient.TryConstructCi(WordTextBox.Text ?? string.Empty, 1000, out string code, out _))
            {
                CodeTextBox.Text = code ?? string.Empty;
            }
            else
            {
                CodeTextBox.Clear();
            }
        }

        private void InitText()
        {
            _historyLen = 2;
            RefreshNameFromHistory();
            OnWordTextChanged(null, null);
        }

        private void RefreshNameFromHistory()
        {
            if (!CorePipeClient.TryGetSendHistoryCount(1000, out _remoteHistoryCount, out _))
            {
                _remoteHistoryCount = 0;
            }

            if (_historyLen > _remoteHistoryCount)
            {
                _historyLen = _remoteHistoryCount;
            }

            if (!CorePipeClient.TryGetLastCi(_historyLen, 1000, out string text, out _))
            {
                text = string.Empty;
            }

            WordTextBox.Text = text ?? string.Empty;
            WordTextBox.SelectAll();
        }

        private void EnsureInputFocus()
        {
            _ = Dispatcher.BeginInvoke(new System.Action(() =>
            {
                Activate();
                WordTextBox.Focus();
                Keyboard.Focus(WordTextBox);
                WordTextBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        private void BringToFrontOnce()
        {
            if (_frontRaised)
            {
                return;
            }

            _frontRaised = true;
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                // Retry a few times because dialog startup and foreground ownership are racy.
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        if (WindowState == WindowState.Minimized)
                        {
                            WindowState = WindowState.Normal;
                        }

                        Activate();
                        IntPtr hwnd = new WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            ForceToForeground(hwnd);
                        }

                        if (IsActive)
                        {
                            break;
                        }
                    }
                    catch
                    {
                    }

                    await Task.Delay(80).ConfigureAwait(true);
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private void ForceToForeground(IntPtr hwnd)
        {
            DialogNativeMethods.ShowWindow(hwnd, DialogNativeMethods.SW_SHOWNORMAL);

            IntPtr foreground = DialogNativeMethods.GetForegroundWindow();
            uint foregroundThread = 0;
            if (foreground != IntPtr.Zero)
            {
                foregroundThread = DialogNativeMethods.GetWindowThreadProcessId(foreground, out _);
            }

            uint currentThread = DialogNativeMethods.GetCurrentThreadId();
            bool attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = DialogNativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            try
            {
                // Always do a topmost pulse once to make sure the dialog is visible above
                // currently active windows, then immediately restore non-topmost to avoid
                // long-term z-order competition.
                Topmost = true;
                DialogNativeMethods.SetWindowPos(
                    hwnd,
                    DialogNativeMethods.HWND_TOP,
                    0, 0, 0, 0,
                    DialogNativeMethods.SWP_NOMOVE |
                    DialogNativeMethods.SWP_NOSIZE |
                    DialogNativeMethods.SWP_NOOWNERZORDER |
                    DialogNativeMethods.SWP_SHOWWINDOW);
                DialogNativeMethods.BringWindowToTop(hwnd);
                DialogNativeMethods.SetForegroundWindow(hwnd);
                DialogNativeMethods.SetFocus(hwnd);
                DialogNativeMethods.SetWindowPos(
                    hwnd,
                    DialogNativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    DialogNativeMethods.SWP_NOMOVE |
                    DialogNativeMethods.SWP_NOSIZE |
                    DialogNativeMethods.SWP_NOOWNERZORDER |
                    DialogNativeMethods.SWP_SHOWWINDOW);
                DialogNativeMethods.SetWindowPos(
                    hwnd,
                    DialogNativeMethods.HWND_NOTOPMOST,
                    0, 0, 0, 0,
                    DialogNativeMethods.SWP_NOMOVE |
                    DialogNativeMethods.SWP_NOSIZE |
                    DialogNativeMethods.SWP_NOOWNERZORDER |
                    DialogNativeMethods.SWP_SHOWWINDOW);
                Activate();
                Focus();
            }
            finally
            {
                Topmost = false;
                if (attached)
                {
                    DialogNativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }

        private void PlaceWindow()
        {
            if (_uiStateReader.TryRead(out OverlayUiState uiState, out _, out _) &&
                uiState != null &&
                uiState.CandidateVisible)
            {
                Point p = new Point(uiState.CaretX + 16, uiState.CaretY + 8);
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    p = source.CompositionTarget.TransformFromDevice.Transform(p);
                }

                Rect work = SystemParameters.WorkArea;
                double candidateWidth = EstimateCandidateWidth(uiState);
                Left = Math.Max(work.Left, Math.Min(p.X + candidateWidth, work.Right - Width));
                Top = Math.Max(work.Top, Math.Min(p.Y, work.Bottom - Height));
                return;
            }

            Rect desktopWorkingArea = SystemParameters.WorkArea;
            Top = desktopWorkingArea.Bottom / 2 - Height;
            Left = desktopWorkingArea.Right / 2 - Width / 2;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _uiStateReader.Dispose();
            base.OnClosed(e);
        }

        private static double EstimateCandidateWidth(OverlayUiState uiState)
        {
            if (uiState == null)
            {
                return 160;
            }

            double fontSize = uiState.FontSize <= 0 ? 17 : uiState.FontSize;
            if (fontSize < 3)
            {
                fontSize = 3;
            }
            else if (fontSize > 200)
            {
                fontSize = 200;
            }

            string[] candidates = uiState.Candidates ?? Array.Empty<string>();
            string[] annotations = uiState.CandidateAnnotations ?? Array.Empty<string>();
            bool hideItems = uiState.HideCandidateItems;
            if (hideItems || candidates.Length == 0)
            {
                return 0;
            }

            if (uiState.VerticalCandidates)
            {
                int maxChars = 0;
                for (int i = 0; i < candidates.Length; i++)
                {
                    string line = BuildCandidateText(candidates[i], i < annotations.Length ? annotations[i] : string.Empty);
                    if (uiState.ShowCandidateIndex)
                    {
                        line = (i + 1).ToString() + " " + line;
                    }

                    if (line.Length > maxChars)
                    {
                        maxChars = line.Length;
                    }
                }

                double minWidth = fontSize * 8 * 0.72 + 15;
                return Math.Max(minWidth, (maxChars - 4) * fontSize + 16);
            }

            int totalChars = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (i > 0)
                {
                    totalChars += 2;
                }

                string text = BuildCandidateText(candidates[i], i < annotations.Length ? annotations[i] : string.Empty);
                if (uiState.ShowCandidateIndex)
                {
                    text = (i + 1).ToString() + " " + text;
                }

                totalChars += text.Length;
            }

            double minWidthHorizontal = fontSize * 4 * 0.72 + 15;
            return Math.Max(minWidthHorizontal, (totalChars - 8) * fontSize - candidates.Length * fontSize + 10);
        }

        private static string BuildCandidateText(string candidate, string annotation)
        {
            string text = (candidate ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
            if (!string.IsNullOrEmpty(annotation))
            {
                text += "\u3014" + annotation
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t") + "\u3015";
            }

            return text;
        }
    }
}
