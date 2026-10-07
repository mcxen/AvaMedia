using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace AvaMedia.Desktop;

public sealed partial class App
{
    private void InitializeModelInstallation(IClassicDesktopStyleApplicationLifetime desktop, Window window, string[] args)
    {
        if (args.Contains("--capture") || args.Contains("--player-benchmark")) return;
        var lifetime = new CancellationTokenSource();
        ModelInstallation.Lifetime = lifetime.Token;
        desktop.Exit += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        window.Opened += async (_, _) => await ModelInstallation.StartAsync();
    }
}
