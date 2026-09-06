using Avalonia.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TigerClawSettingsSpike;

public partial class SelectionKeyWindow : Window
{
    private readonly TigerClawHostCli _host = new();

    public SelectionKeyWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            StatusText.Text = "正在读取当前方案的选词键…";
            ConfigurationInput.Text = await Task.Run(_host.SelectionKeyConfigurationContents);
            StatusText.Text = "保存后，输入法会在下一次按键前自动重载。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            string contents = ConfigurationInput.Text ?? string.Empty;
            await Task.Run(() => _host.SetSelectionKeyConfiguration(contents));
            StatusText.Text = "已保存并校验；输入法会在下一次按键前自动重载。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
    }

    private async void Reset_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            await Task.Run(_host.ResetSelectionKeyConfiguration);
            await LoadAsync();
            StatusText.Text = "已恢复默认数字选词键。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "恢复失败：" + ex.Message;
        }
    }

    private async void OpenInTextEditor_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            string path = await Task.Run(_host.SelectionKeyConfigurationPath);
            var startInfo = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
            StatusText.Text = "已在文本编辑器中打开；保存后输入法会自动重载。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开配置文件：" + ex.Message;
        }
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
