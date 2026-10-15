using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class App
{
    private readonly List<string> _startupFiles = [];
    private bool _desktopReady;

    private static string[] PlaybackPaths(IEnumerable<string> arguments) => arguments
        .Where(path => !path.StartsWith("--", StringComparison.Ordinal) && File.Exists(path)
            && SystemPlayerIntegration.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        .Select(Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray();

    private void StartStandalonePlayer(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var settings = StartupSettings ?? new Storage().LoadSettings();
        Skin.Apply(args.Contains("--winxp") ? "WindowsXP" : args.Contains("--macos9") ? "MacOS9"
            : args.Contains("--dark") ? "Dark" : args.Contains("--light") ? "Light" : settings.Theme, settings.AccentColor);
        Motion.SetReducedMotion(settings.ReduceMotion);
        var player = PlayerWindow.GetOrCreate(new MediaEngine(settings), PlaybackPaths(args), settings);
        desktop.MainWindow = player;
        FirstRunSetup.AttachFailureNotice(player);
        ScheduleInstalledPlayerRegistration();
    }

    private void OpenPlayback(IClassicDesktopStyleApplicationLifetime desktop, string[] paths)
    {
        var settings = StartupSettings ?? new Storage().LoadSettings();
        var engine = desktop.MainWindow is MainWindow main ? main.Engine : new MediaEngine(settings);
        var player = PlayerWindow.GetOrCreate(engine, paths, settings);
        if (desktop.MainWindow is { } source) player.ShowForPlayback(source);
        else { desktop.MainWindow = player; player.Show(); }
    }

    private async void HandleDesktopRequest(string[] args)
    {
        try
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
            if (!args.Contains("--convert") && !args.Contains("--ai-tags")
                && (args.Contains("--play") || PlaybackPaths(args).Length > 0))
            { OpenPlayback(desktop, PlaybackPaths(args)); return; }
            // A normal app launch restores the converter, creating it only when requested.
            var main = desktop.Windows.OfType<MainWindow>().FirstOrDefault();
            if (main is null)
            {
                main = new MainWindow();
                desktop.MainWindow = main;
                FirstRunSetup.AttachFailureNotice(main);
                InitializeModelInstallation(desktop, main, args);
            }
            main.RestoreBackgroundWindow();
            if (args.Contains("--convert")) await main.ImportForConversionAsync(args);
            else if (args.Contains("--ai-tags")) await main.ConfigureMediaAiAsync(args.Where(File.Exists).ToArray());
        }
        catch (Exception error) { AppDiagnostics.Record("Open desktop request", error); StartupError.Show(error); }
    }
}
