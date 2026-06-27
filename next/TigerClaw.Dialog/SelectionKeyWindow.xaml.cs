using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TigerClaw.Dialog
{
    public partial class SelectionKeyWindow : Window
    {
        private readonly List<SelectionSlotModel> _slots = new List<SelectionSlotModel>();
        private string _originalConfigText = string.Empty;
        private string _defaultConfigText = string.Empty;
        private string _configPath = string.Empty;

        public SelectionKeyWindow()
        {
            InitializeComponent();
            LoadConfig(updateStatus: true);
        }

        private void LoadConfig(bool updateStatus)
        {
            if (!CorePipeClient.TryGetSelectionKeyConfig(1500, out string configText, out string defaultText, out string configPath, out string error))
            {
                StatusText.Text = "状态：读取失败 - " + error;
                return;
            }

            _originalConfigText = NormalizeConfigText(configText);
            _defaultConfigText = NormalizeConfigText(defaultText);
            _configPath = configPath ?? string.Empty;

            BuildSlots(ParseConfigText(_originalConfigText));
            UpdateDirtyState();
            if (updateStatus)
            {
                StatusText.Text = "状态：已读取选重键配置。";
            }
        }

        private void BuildSlots(Dictionary<int, List<string>> map)
        {
            _slots.Clear();
            SlotsPanel.Children.Clear();

            for (int number = 1; number <= 10; number++)
            {
                map.TryGetValue(number, out List<string> tokens);
                var model = new SelectionSlotModel(number, tokens ?? new List<string>());
                _slots.Add(model);
                SlotsPanel.Children.Add(CreateSlotRow(model));
            }
        }

        private UIElement CreateSlotRow(SelectionSlotModel model)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xC7, 0xB3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            border.Child = grid;

            var title = new TextBlock
            {
                Text = model.Number.ToString() + "选",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var tokenBorder = new Border
            {
                MinHeight = 40,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xF0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xC7, 0xB3)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(8)
            };
            Grid.SetColumn(tokenBorder, 1);
            grid.Children.Add(tokenBorder);

            var tokenPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal
            };
            tokenBorder.Child = tokenPanel;
            model.TokenPanel = tokenPanel;
            RefreshSlotList(model);

            var buttonPanel = new StackPanel
            {
                Margin = new Thickness(10, 0, 0, 0),
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            buttonPanel.Children.Add(CreateRowButton("添加", OnAddTokenClick, model));
            buttonPanel.Children.Add(CreateRowButton("删除选中", OnDeleteSelectedClick, model, new Thickness(8, 0, 0, 0)));
            buttonPanel.Children.Add(CreateRowButton("恢复默认", OnResetSlotClick, model, new Thickness(8, 0, 0, 0)));

            return border;
        }

        private Button CreateRowButton(string text, RoutedEventHandler handler, SelectionSlotModel model, Thickness? margin = null)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 74,
                Height = 28,
                Margin = margin ?? new Thickness(0),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Tag = model,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xF0)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xC7, 0xB3)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x33, 0x26))
            };
            button.Click += handler;
            return button;
        }

        private void RefreshSlotList(SelectionSlotModel model)
        {
            model.TokenPanel.Children.Clear();
            foreach (string token in model.Tokens)
            {
                model.TokenPanel.Children.Add(CreateTokenChip(model, token));
            }
        }

        private UIElement CreateTokenChip(SelectionSlotModel model, string token)
        {
            int vk = ParseVirtualKey(token);
            string display = vk > 0 ? SelectionKeyTokenHelper.GetDisplayNameForVirtualKey(vk) : token;
            bool selected = string.Equals(model.SelectedToken, token, StringComparison.OrdinalIgnoreCase);

            var border = new Border
            {
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(6),
                Background = selected
                    ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC7, 0x9D))
                    : new SolidColorBrush(Color.FromRgb(0xFF, 0xFD, 0xF8)),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(0xD9, 0x6A, 0x1B))
                    : new SolidColorBrush(Color.FromRgb(0xD9, 0xC7, 0xB3)),
                BorderThickness = new Thickness(1),
                Tag = new TokenChipTag(model, token)
            };

            var tokenText = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x20)),
                Text = display
            };
            border.Child = tokenText;
            border.MouseLeftButtonUp += OnTokenChipClick;
            return border;
        }

        private void OnTokenChipClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(sender is Border border) || !(border.Tag is TokenChipTag tag))
            {
                return;
            }

            if (string.Equals(tag.Model.SelectedToken, tag.Token, StringComparison.OrdinalIgnoreCase))
            {
                tag.Model.SelectedToken = string.Empty;
            }
            else
            {
                tag.Model.SelectedToken = tag.Token;
            }

            RefreshSlotList(tag.Model);
            UpdateDirtyState();
        }

        private void OnAddTokenClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.Tag is SelectionSlotModel model))
            {
                return;
            }

            var recordWindow = new RecordKeyWindow
            {
                Owner = this
            };
            bool? result = recordWindow.ShowDialog();
            if (result != true || recordWindow.Result == null)
            {
                return;
            }

            string token = recordWindow.Result.Token ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(token) && !model.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                model.Tokens.Add(token);
                model.SelectedToken = token;
                RefreshSlotList(model);
                UpdateDirtyState();
                StatusText.Text = "状态：已添加 " + token + " 到 " + model.Number.ToString() + "选。";
            }
        }

        private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.Tag is SelectionSlotModel model))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(model.SelectedToken))
            {
                StatusText.Text = "状态：请先选择要删除的按键。";
                return;
            }

            string removed = model.SelectedToken;
            model.Tokens.RemoveAll(t => string.Equals(t, removed, StringComparison.OrdinalIgnoreCase));
            model.SelectedToken = string.Empty;
            RefreshSlotList(model);
            UpdateDirtyState();
            StatusText.Text = "状态：已删除 " + removed + "。";
        }

        private void OnResetSlotClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.Tag is SelectionSlotModel model))
            {
                return;
            }

            Dictionary<int, List<string>> defaults = ParseConfigText(_defaultConfigText);
            defaults.TryGetValue(model.Number, out List<string> tokens);
            model.Tokens.Clear();
            foreach (string token in tokens ?? new List<string>())
            {
                if (!model.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    model.Tokens.Add(token);
                }
            }

            model.SelectedToken = string.Empty;
            RefreshSlotList(model);
            UpdateDirtyState();
            StatusText.Text = "状态：" + model.Number.ToString() + "选已恢复默认。";
        }

        private void OnReloadClick(object sender, RoutedEventArgs e)
        {
            if (HasUnsavedChanges())
            {
                MessageBoxResult result = MessageBox.Show(
                    "重载会丢弃当前未保存的修改，是否继续？",
                    "重载选重键",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            LoadConfig(updateStatus: true);
        }

        private void OnResetAllClick(object sender, RoutedEventArgs e)
        {
            Dictionary<int, List<string>> defaults = ParseConfigText(_defaultConfigText);
            foreach (SelectionSlotModel model in _slots)
            {
                defaults.TryGetValue(model.Number, out List<string> tokens);
                model.Tokens.Clear();
                foreach (string token in tokens ?? new List<string>())
                {
                    if (!model.Tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                    {
                        model.Tokens.Add(token);
                    }
                }

                model.SelectedToken = string.Empty;
                RefreshSlotList(model);
            }

            UpdateDirtyState();
            StatusText.Text = "状态：已恢复默认内容，尚未保存。";
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!HasUnsavedChanges())
            {
                StatusText.Text = "状态：没有改动。";
                return;
            }

            if (!ConfirmConflictsBeforeSave())
            {
                return;
            }

            string configText = BuildConfigText();
            if (!CorePipeClient.TrySetSelectionKeyConfig(configText, 2000, out string savedConfigText, out string error))
            {
                StatusText.Text = "状态：保存失败 - " + error;
                return;
            }

            _originalConfigText = NormalizeConfigText(savedConfigText);
            BuildSlots(ParseConfigText(_originalConfigText));
            UpdateDirtyState();
            StatusText.Text = "状态：已保存并重载。";
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (!HasUnsavedChanges())
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                "选重键已修改，是否先保存？",
                "保存选重键",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
            {
                return;
            }

            if (!ConfirmConflictsBeforeSave())
            {
                e.Cancel = true;
                return;
            }

            string configText = BuildConfigText();
            if (!CorePipeClient.TrySetSelectionKeyConfig(configText, 2000, out string savedConfigText, out string error))
            {
                StatusText.Text = "状态：保存失败 - " + error;
                e.Cancel = true;
                return;
            }

            _originalConfigText = NormalizeConfigText(savedConfigText);
            BuildSlots(ParseConfigText(_originalConfigText));
            UpdateDirtyState();
        }

        private bool HasUnsavedChanges()
        {
            return !string.Equals(_originalConfigText, NormalizeConfigText(BuildConfigText()), StringComparison.Ordinal);
        }

        private void UpdateDirtyState()
        {
            bool dirty = HasUnsavedChanges();
            DirtyHintText.Text = dirty ? "有未保存更改" : "所有更改已保存";
            DirtyHintText.Foreground = dirty
                ? new SolidColorBrush(Color.FromRgb(217, 106, 27))
                : new SolidColorBrush(Color.FromRgb(125, 107, 93));
            Title = dirty ? "自定义选重键 · 未保存" : "自定义选重键";
            if (ReloadButton != null)
            {
                ReloadButton.IsEnabled = dirty;
            }

            if (SaveButton != null)
            {
                SaveButton.IsEnabled = dirty;
            }

            if (ResetAllButton != null)
            {
                string current = NormalizeConfigText(BuildConfigText());
                ResetAllButton.IsEnabled = !string.Equals(current, _defaultConfigText, StringComparison.Ordinal);
            }

            if (CloseButton != null)
            {
                CloseButton.IsEnabled = true;
            }
        }

        private string BuildConfigText()
        {
            var lines = new List<string>();
            foreach (SelectionSlotModel slot in _slots.OrderBy(s => s.Number))
            {
                lines.Add(slot.Number.ToString() + "选" + (slot.Tokens.Count > 0 ? " " + string.Join(" ", slot.Tokens) : string.Empty));
            }

            return NormalizeConfigText(string.Join("\r\n", lines));
        }

        private static Dictionary<int, List<string>> ParseConfigText(string configText)
        {
            var map = new Dictionary<int, List<string>>();
            string[] lines = NormalizeConfigText(configText).Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (string rawLine in lines)
            {
                string line = (rawLine ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                if (!TryParseSelectionLabel(parts[0], out int number))
                {
                    continue;
                }

                var tokens = new List<string>();
                for (int i = 1; i < parts.Length; i++)
                {
                    string token = parts[i].Trim();
                    if (token.Length > 0 && !tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                    {
                        tokens.Add(token);
                    }
                }

                map[number] = tokens;
            }

            for (int number = 1; number <= 10; number++)
            {
                if (!map.ContainsKey(number))
                {
                    map[number] = new List<string>();
                }
            }

            return map;
        }

        private static bool TryParseSelectionLabel(string token, out int number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(token) || !token.EndsWith("选", StringComparison.Ordinal))
            {
                return false;
            }

            return int.TryParse(token.Substring(0, token.Length - 1), out number) && number >= 1 && number <= 10;
        }

        private static string NormalizeConfigText(string text)
        {
            text = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(line => (line ?? string.Empty).Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                .ToArray();
            return string.Join("\r\n", lines);
        }

        private bool ConfirmConflictsBeforeSave()
        {
            List<string> conflicts = GetConflictLines();
            if (conflicts.Count == 0)
            {
                return true;
            }

            string message = "以下按键同时绑定到多个选重：\r\n\r\n" +
                             string.Join("\r\n", conflicts) +
                             "\r\n\r\n请先消除冲突后再保存。";
            MessageBox.Show(
                message,
                "选重键冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            StatusText.Text = "状态：存在冲突，未保存。";
            return false;
        }

        private List<string> GetConflictLines()
        {
            var tokenMap = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (SelectionSlotModel slot in _slots)
            {
                foreach (string token in slot.Tokens)
                {
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    if (!tokenMap.TryGetValue(token, out List<int> numbers))
                    {
                        numbers = new List<int>();
                        tokenMap[token] = numbers;
                    }

                    if (!numbers.Contains(slot.Number))
                    {
                        numbers.Add(slot.Number);
                    }
                }
            }

            var conflicts = new List<string>();
            foreach (KeyValuePair<string, List<int>> item in tokenMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (item.Value.Count < 2)
                {
                    continue;
                }

                int vk = ParseVirtualKey(item.Key);
                string display = vk > 0 ? SelectionKeyTokenHelper.GetDisplayNameForVirtualKey(vk) : item.Key;
                string slots = string.Join("、", item.Value.OrderBy(n => n).Select(n => n.ToString() + "选"));
                conflicts.Add(item.Key + " (" + display + ") -> " + slots);
            }

            return conflicts;
        }

        private static int ParseVirtualKey(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return 0;
            }

            string trimmed = token.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(trimmed.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;
            }

            foreach (int vk in Enumerable.Range(0, 256))
            {
                if (string.Equals(SelectionKeyTokenHelper.GetTokenForVirtualKey(vk), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return vk;
                }
            }

            return 0;
        }
    }

    internal sealed class SelectionSlotModel
    {
        public SelectionSlotModel(int number, IEnumerable<string> tokens)
        {
            Number = number;
            Tokens = new List<string>(tokens ?? Array.Empty<string>());
            SelectedToken = string.Empty;
        }

        public int Number { get; }

        public List<string> Tokens { get; }

        public WrapPanel TokenPanel { get; set; }

        public string SelectedToken { get; set; }
    }

    internal sealed class TokenChipTag
    {
        public TokenChipTag(SelectionSlotModel model, string token)
        {
            Model = model;
            Token = token;
        }

        public SelectionSlotModel Model { get; }

        public string Token { get; }
    }
}
