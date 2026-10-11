using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;

namespace AvaMedia.Browser;

internal static class Program
{
    private static Task Main(string[] args) => AppBuilder.Configure<BrowserApp>()
        .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "avares://AvaMedia.Browser/Fonts#Fusion Pixel 12px Prop zh-Hans" })
        .StartBrowserAppAsync("out");
}

public sealed class BrowserApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime) lifetime.MainView = new ConsoleView();
        base.OnFrameworkInitializationCompleted();
    }
}
