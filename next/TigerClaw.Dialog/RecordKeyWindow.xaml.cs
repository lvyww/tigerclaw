using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TigerClaw.Shared;

namespace TigerClaw.Dialog
{
    public partial class RecordKeyWindow : Window
    {
        private readonly bool _recordShortcut;
        private readonly string _defaultShortcut;
        public bool ShortcutCleared { get; private set; }

        public RecordedKeyResult Result { get; private set; }

        public RecordKeyWindow()
            : this(false)
        {
        }

        public RecordKeyWindow(bool recordShortcut, string defaultShortcut = null)
        {
            _recordShortcut = recordShortcut;
            _defaultShortcut = defaultShortcut;
            InitializeComponent();
            if (_recordShortcut)
            {
                Title = "修改快捷键";
                MinWidth = 460;
                PromptText.Text = "请按下组合快捷键";
                InstructionText.Text = "必须包含 Ctrl 或 Alt，可同时按 Shift；不支持 Win 和裸键。";
                AddButton.Content = "确定";
                ClearShortcutButton.Visibility = Visibility.Visible;
                ResetShortcutButton.Visibility = Visibility.Visible;
                ResetShortcutButton.IsEnabled = ShortcutGesture.TryParse(_defaultShortcut, out _);
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = ResolveKey(e);
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0)
            {
                return;
            }

            if (_recordShortcut)
            {
                ModifierKeys modifiers = Keyboard.Modifiers;
                bool control = (modifiers & ModifierKeys.Control) != 0;
                bool alt = (modifiers & ModifierKeys.Alt) != 0;
                bool shift = (modifiers & ModifierKeys.Shift) != 0;
                bool win = (modifiers & ModifierKeys.Windows) != 0;
                if (key == Key.Escape && modifiers == ModifierKeys.None)
                {
                    DialogResult = false;
                    Close();
                    e.Handled = true;
                    return;
                }

                if (!ShortcutGesture.TryCreate(vk, control, alt, shift, win, out ShortcutGesture shortcut))
                {
                    DisplayNameText.Text = "请按 Ctrl 或 Alt 与另一个按键";
                    AddButton.IsEnabled = false;
                    e.Handled = true;
                    return;
                }

                Result = new RecordedKeyResult
                {
                    VirtualKey = vk,
                    Token = shortcut.ToConfigString(),
                    DisplayName = shortcut.ToDisplayString()
                };
                DisplayNameText.Text = Result.DisplayName;
                AddButton.IsEnabled = true;
                e.Handled = true;
                return;
            }

            string token = SelectionKeyTokenHelper.GetTokenForVirtualKey(vk);
            string displayName = SelectionKeyTokenHelper.GetDisplayNameForVirtualKey(vk);
            Result = new RecordedKeyResult
            {
                VirtualKey = vk,
                Token = token,
                DisplayName = displayName
            };

            DisplayNameText.Text = displayName;
            AddButton.IsEnabled = true;
            e.Handled = true;
        }

        private static Key ResolveKey(KeyEventArgs e)
        {
            if (e == null)
            {
                return Key.None;
            }

            if (e.Key == Key.ImeProcessed)
            {
                return e.ImeProcessedKey;
            }

            if (e.Key == Key.System)
            {
                return e.SystemKey;
            }

            if (e.Key == Key.DeadCharProcessed)
            {
                return e.DeadCharProcessedKey;
            }

            return e.Key;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (Result == null)
            {
                return;
            }

            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnClearShortcutClick(object sender, RoutedEventArgs e)
        {
            ShortcutCleared = true;
            DialogResult = true;
            Close();
        }

        private void OnResetShortcutClick(object sender, RoutedEventArgs e)
        {
            if (!ShortcutGesture.TryParse(_defaultShortcut, out ShortcutGesture gesture))
            {
                return;
            }
            Result = new RecordedKeyResult
            {
                Token = gesture.ToConfigString(),
                DisplayName = gesture.ToDisplayString()
            };
            DialogResult = true;
            Close();
        }
    }

    public sealed class RecordedKeyResult
    {
        public int VirtualKey { get; set; }

        public string Token { get; set; }

        public string DisplayName { get; set; }
    }
}
