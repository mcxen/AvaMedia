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
        var mcpBackground = args.Contains("--mcp-background");
        if (!args.Contains("--capture") && !previewExport && !mcpBackground) FirstRunSetup.TryApplyPending();
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) && !args.Contains("--capture") && !previewExport && !mcpBackground)
        {
            try { SystemPlayerIntegration.RegisterWindows(SystemPlayerIntegration.ExecutablePath); }
            catch (Exception error) { AppDiagnostics.Record("Player registration", error); }
        }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Application startup", error);
            if (mcpBackground) Console.Error.WriteLine(error.Message); else StartupError.Show(error);
            return 1;
        }
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
        if (Environment.GetCommandLineArgs().Contains("--mcp-background"))
            builder.With(new MacOSPlatformOptions { ShowInDock = false, DisableNativeMenus = true });
        return builder;
    }
}
