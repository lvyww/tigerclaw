using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace TigerClawSettingsSpike;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Program.QuickAddWordRequest is { } request
                ? new QuickAddWordWindow(request)
                : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
