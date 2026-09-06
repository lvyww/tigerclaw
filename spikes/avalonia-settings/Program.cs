using Avalonia;
using System;

namespace TigerClawSettingsSpike;

class Program
{
    internal static QuickAddWordRequest? QuickAddWordRequest { get; private set; }
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--quick-add-word-smoke", StringComparer.Ordinal))
        {
            QuickAddWordRequest? request = ParseQuickAddWordRequest(["-psn_0_12345", "--quick-add-word", "abcd", "测试"]);
            if (request is not { Code: "abcd", Text: "测试" })
            {
                Console.Error.WriteLine("QUICK_ADD_WORD_ARGUMENT_SMOKE_FAIL");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine("QUICK_ADD_WORD_ARGUMENT_SMOKE_PASS");
            return;
        }
        QuickAddWordRequest = ParseQuickAddWordRequest(args);
        if (args.Length == 1 && args[0] == "--settings-smoke")
        {
            try
            {
                SettingsSnapshot snapshot = new TigerClawHostCli().ReadSnapshot();
                if (snapshot.Schemas.Count == 0 ||
                    !snapshot.Schemas.Exists(schema => schema.Identifier == snapshot.SelectedSchema))
                {
                    Console.Error.WriteLine($"SETTINGS_SMOKE_FAIL schemas={snapshot.Schemas.Count} selected={snapshot.SelectedSchema}");
                    Environment.ExitCode = 1;
                    return;
                }
                Console.WriteLine($"SETTINGS_SMOKE_PASS schemas={snapshot.Schemas.Count} selected={snapshot.SelectedSchema}");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SETTINGS_SMOKE_FAIL " + ex.Message);
                Environment.ExitCode = 1;
                return;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static QuickAddWordRequest? ParseQuickAddWordRequest(string[] args)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, "--quick-add-word", StringComparison.Ordinal));
        return index >= 0 && args.Length >= index + 3
            ? new QuickAddWordRequest(args[index + 1], args[index + 2])
            : null;
    }
}

public sealed record QuickAddWordRequest(string Code, string Text);
