using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TigerClaw.Dialog
{
    internal sealed class PinyinManagerWindow : Window
    {
        private sealed class Row
        {
            public string Kind { get; set; }
            public string Type { get { return Kind == "phrase" ? "固定短语" : Kind == "pin" ? "置顶" : Kind == "word" ? "用户词" : "学习记录"; } }
            public string Code { get; set; }
            public string Text { get; set; }
        }
        private readonly DataGrid rows = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, MinHeight = 160 };
        private readonly TextBox code = new TextBox { Margin = new Thickness(0, 4, 0, 4) };
        private readonly TextBox text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 55 };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private bool busy;
        internal PinyinManagerWindow()
        {
            Title = "虎爪拼音：短语与候选管理"; Width = 680; Height = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var grid = new Grid { Margin = new Thickness(16) }; Content = grid;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rows.Columns.Add(new DataGridTextColumn { Header = "类型", Binding = new Binding("Type"), Width = 90 });
            rows.Columns.Add(new DataGridTextColumn { Header = "编码 / 读音", Binding = new Binding("Code"), Width = 140 });
            rows.Columns.Add(new DataGridTextColumn { Header = "内容", Binding = new Binding("Text"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Children.Add(rows);
            var help = new TextBlock { Text = "短语：用字母短码输入邮箱、地址或签名。选中记录后可删除、取消置顶或忘记学习。\n候选快捷键：Ctrl+P 置顶，Ctrl+L 取消置顶，Ctrl+Delete 忘记学习。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
            Grid.SetRow(help, 1); grid.Children.Add(help);
            code.ToolTip = "固定短语使用字母短码，例如 yx；用户词的读音请在加词窗口填写。";
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(code, false);
            Grid.SetRow(code, 2); grid.Children.Add(code); Grid.SetRow(text, 3); grid.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
            AddButton(buttons, "添加固定短语", async () => await Manage("phrase_add"));
            AddButton(buttons, "删除选中记录", async () =>
            {
                var row = rows.SelectedItem as Row;
                if (row == null) { status.Text = "请先选中一条记录。"; return; }
                await Manage(row.Kind == "phrase" ? "phrase_delete" : row.Kind == "pin" ? "unpin" : row.Kind == "word" ? "word_delete" : "forget", row);
            });
            AddButton(buttons, "刷新", async () => await RefreshRows());
            Grid.SetRow(status, 5); grid.Children.Add(status);
            rows.SelectionChanged += (s, e) => { var row = rows.SelectedItem as Row; if (row != null) { code.Text = row.Code; text.Text = row.Text; } };
            Loaded += async (s, e) =>
            {
                busy = true;
                try { await RefreshRows(); }
                catch (Exception ex) { status.Text = "读取失败：" + ex.Message; }
                finally { busy = false; }
            };
        }
        private void AddButton(Panel panel, string title, Func<Task> action)
        {
            var button = new Button { Content = title, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            button.Click += async (s, e) => { if (busy) return; busy = true; try { await action(); } catch (Exception ex) { status.Text = ex.Message; } finally { busy = false; } };
            panel.Children.Add(button);
        }
        private async Task RefreshRows()
        {
            var response = await Task.Run(() => CorePipeClient.GetPinyinPreferences());
            rows.ItemsSource = response.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var fields = line.Split('\t');
                return new Row { Kind = fields[0], Code = fields[1], Text = Encoding.UTF8.GetString(Convert.FromBase64String(fields[2])) };
            }).ToArray();
        }
        private async Task Manage(string action, Row row = null)
        {
            string c = row == null ? code.Text : row.Code, t = row == null ? text.Text : row.Text;
            string error = await Task.Run(() =>
            {
                string message;
                bool ok = action == "word_delete" ? CorePipeClient.TryDeletePinyinWord(c, t, out message) : CorePipeClient.TryManagePinyin(action, c, t, out message);
                return ok ? "" : message;
            });
            status.Text = error.Length > 0 ? "操作失败：" + error : action == "forget" ? "已提交忘记学习；稍后刷新可查看结果。" : "已保存。";
            if (error.Length == 0) await RefreshRows();
        }
    }
}
