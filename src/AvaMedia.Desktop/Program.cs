using Avalonia;

namespace AvaMedia.Desktop;
internal static class Program
{
    internal static readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
    [STAThread]
    public static int Main(string[] args)
    {
        _ = StartedUtc;
        if (args.Contains("--register-player")) { SystemPlayerIntegration.RegisterWindows(SystemPlayerIntegration.ExecutablePath); return 0; }
        if (args.Contains("--unregister-player")) { SystemPlayerIntegration.UnregisterWindows(SystemPlayerIntegration.ExecutablePath); return 0; }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
