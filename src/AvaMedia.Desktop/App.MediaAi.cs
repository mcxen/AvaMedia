using Avalonia.Controls.ApplicationLifetimes;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class App
{
    private void StartMediaAi(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var storage = new Storage(); var settings = storage.LoadSettings();
        Localization.Apply(settings.Language);
        Skin.Apply(args.Contains("--winxp") ? "WindowsXP" : args.Contains("--macos9") ? "MacOS9" : args.Contains("--dark") ? "Dark" : args.Contains("--light") ? "Light" : settings.Theme);
        Motion.SetReducedMotion(settings.ReduceMotion);
        var options = new AppOptionsServices(); var lifetime = new CancellationTokenSource();
        var workspace = new MediaAiWindow(new MediaEngine(settings), settings, args.Where(File.Exists), async owner =>
        {
            var dialog = new SettingsWindow(settings, options); dialog.OpenModelManagement();
            dialog.Applied += (_, _) =>
            {
                storage.SaveSettings(settings); Localization.Apply(settings.Language); Skin.Apply(settings.Theme); Motion.SetReducedMotion(settings.ReduceMotion);
            };
            await dialog.ShowDialog<bool>(owner);
        }, storage: storage);
        desktop.MainWindow = workspace; FirstRunSetup.AttachFailureNotice(workspace);
        workspace.Opened += async (_, _) => await ApplicationUpdater.Shared.StartupAsync(workspace, settings, options.CheckUpdatesAsync, lifetime.Token);
        workspace.Closed += (_, _) => { lifetime.Cancel(); options.Dispose(); };
        InitializeModelInstallation(desktop, workspace, args);
    }
}
