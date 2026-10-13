using Avalonia;

namespace AvaMedia.Desktop;
internal static class Program
{
    internal static readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
    [STAThread]
    public static int Main(string[] args)
    {
        _ = StartedUtc;
        AppDiagnostics.Initialize();
        if (args.Contains("--register-player")) { SystemPlayerIntegration.RegisterWindows(SystemPlayerIntegration.ExecutablePath); return 0; }
        if (args.Contains("--unregister-player")) { SystemPlayerIntegration.UnregisterWindows(SystemPlayerIntegration.ExecutablePath); return 0; }
        var previewExport = args.Contains(SetupPreviewExporter.Argument);
        var mcpBackground = args.Contains("--mcp-background") || args.Contains("--server");
        if (!args.Contains("--capture") && !previewExport && !mcpBackground) FirstRunSetup.TryApplyPending();
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Application startup", error);
            if (mcpBackground) Console.Error.WriteLine(error.Message); else StartupError.Show(error);
            return 1;
        }
        finally
        {
            ApplicationUpdater.Shared.Shutdown();
            AvaMedia.Core.LocalSummaryModel.Shutdown();
        }
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        LinuxInterfaceScaling.Configure();
        var builder = AppBuilder.Configure<App>().UsePlatformDetect()
            .With(PlatformAppearance.FontOptions()).LogToTrace();
        if (OperatingSystem.IsMacOS())
        {
            var background = Environment.GetCommandLineArgs().Any(argument => argument is "--mcp-background" or "--server");
            builder.With(new MacOSPlatformOptions
            {
                ShowInDock = !background, DisableNativeMenus = background,
                DisableDefaultApplicationMenuItems = true
            });
        }
        return builder;
    }
}
