using Avalonia.Controls.ApplicationLifetimes;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class App
{
    private void StartMediaAi(IClassicDesktopStyleApplicationLifetime desktop, string[] args)
    {
        var storage = new Storage(); var settings = storage.LoadSettings();
        if (args.Contains("--winxp")) settings.Theme = "WindowsXP";
        else if (args.Contains("--macos9")) settings.Theme = "MacOS9";
        else if (args.Contains("--dark")) settings.Theme = "Dark";
        else if (args.Contains("--light")) settings.Theme = "Light";
        var host = new MainWindow(storage); Skin.Apply(settings.Theme);
        desktop.MainWindow = host; FirstRunSetup.AttachFailureNotice(host);
        host.Opened += async (_, _) => await host.ConfigureMediaAiAsync(args.Where(File.Exists).ToArray());
        InitializeModelInstallation(desktop, host, args);
    }
}
