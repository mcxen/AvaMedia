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
        if (!args.Contains("--capture") && !previewExport) FirstRunSetup.ApplyPending();
        if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) && !args.Contains("--capture") && !previewExport)
        {
            try { SystemPlayerIntegration.RegisterWindows(SystemPlayerIntegration.ExecutablePath); }
            catch (Exception error) { AppDiagnostics.Record("Player registration", error); }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
