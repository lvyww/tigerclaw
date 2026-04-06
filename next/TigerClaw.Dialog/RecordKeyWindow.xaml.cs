using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TigerClaw.Dialog
{
    public partial class RecordKeyWindow : Window
    {
        public RecordedKeyResult Result { get; private set; }

        public RecordKeyWindow()
        {
            InitializeComponent();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = ResolveKey(e);
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0)
            {
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
    }

    public sealed class RecordedKeyResult
    {
        public int VirtualKey { get; set; }

        public string Token { get; set; }

        public string DisplayName { get; set; }
    }
}
