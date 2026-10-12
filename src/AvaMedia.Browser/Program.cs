using Avalonia;
using Avalonia.Browser;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Simple;

namespace AvaMedia.Browser;

internal static class Program
{
    // Noto Sans SC (OFL, subset) first; the pixel font stays as a fallback for glyphs outside the subset.
    private const string Noto = "avares://AvaMedia.Browser/Fonts#Noto Sans CJK SC";
    private const string Pixel = "avares://AvaMedia.Browser/Fonts#Fusion Pixel 12px Prop zh-Hans";
    private static Task Main(string[] args)
    {
        try { L.Initialize(BrowserHost.Language()); } catch (Exception) { L.Initialize("zh-CN"); }
        return AppBuilder.Configure<BrowserApp>()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = Noto,
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily(Pixel) }]
            })
            .StartBrowserAppAsync("out");
    }
}

public sealed class BrowserApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        Styles.Add(new RemoteTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime) lifetime.MainView = new ConsoleView();
        base.OnFrameworkInitializationCompleted();
    }
}
