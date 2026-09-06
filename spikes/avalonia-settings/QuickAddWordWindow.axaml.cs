using Avalonia.Controls;
using System;
using System.Threading.Tasks;

namespace TigerClawSettingsSpike;

public partial class QuickAddWordWindow : Window
{
    private readonly TigerClawHostCli _host = new();

    public QuickAddWordWindow() : this(new QuickAddWordRequest(string.Empty, string.Empty))
    {
    }

    public QuickAddWordWindow(QuickAddWordRequest request)
    {
        InitializeComponent();
        CodeInput.Text = request.Code;
        WordInput.Text = request.Text;
        Opened += (_, _) => WordInput.Focus();
    }

    private async void AddAndClose_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await TryAddAsync())
        {
            Close();
        }
    }

    private async void AddAndKeep_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await TryAddAsync())
        {
            WordInput.Text = string.Empty;
            WordInput.Focus();
        }
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async Task<bool> TryAddAsync()
    {
        string code = CodeInput.Text?.Trim() ?? string.Empty;
        string text = WordInput.Text?.Trim() ?? string.Empty;
        if (code.Length == 0 || text.Length == 0)
        {
            StatusText.Text = code.Length == 0 ? "请输入编码。" : "请输入词条。";
            return false;
        }

        try
        {
            await Task.Run(() => _host.AddUserEntry(code, text));
            StatusText.Text = "已添加；输入法下一次按键即会自动重载。";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "添加失败：" + ex.Message;
            return false;
        }
    }
}
