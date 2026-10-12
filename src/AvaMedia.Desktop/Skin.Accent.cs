using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace AvaMedia.Desktop;

public sealed partial class Skin
{
    private static string _accentName = "Blue";
    private static Application? _accentApplication;
    private static readonly HashSet<string> AccentResourceKeys = [];
    private static void ApplyAccent(string? name)
    {
        if (name is not null) _accentName = name;
        EnsureSkinStyles("AccentColors");
        var app = Application.Current!;
        if (!ReferenceEquals(app, _accentApplication))
        {
            _accentApplication = app;
            app.ActualThemeVariantChanged += (_, _) => RefreshAccent();
        }
        RefreshAccent();
    }
    private static void RefreshAccent()
    {
        var app = Application.Current!;
        foreach (var key in AccentResourceKeys) app.Resources.Remove(key);
        AccentResourceKeys.Clear();
        if (UsesCustomChrome(app.ActualThemeVariant)) return;
        var dark = app.ActualThemeVariant == ThemeVariant.Dark;
        var name = _accentName is "Purple" or "Pink" or "Red" or "Orange" or "Yellow" or "Green" or "Gray" ? _accentName : "Blue";
        var color = app.TryFindResource("UiAccentPreset" + name, app.ActualThemeVariant, out var resource) && resource is Color preset ? preset : default;
        void Set(string key, object value) { app.Resources[key] = value; AccentResourceKeys.Add(key); }
        var brush = new SolidColorBrush(color);
        foreach (var key in new[] { "UiAccent", "UiAccentBorder", "ShellBlue", "UiTreeSelection", "UiTreeIcon", "QcAccent", "ThemeAccentBrush", "ThemeAccentBrush1", "ThemeAccentBrush2", "ThemeAccentBrush3", "ThemeAccentBrush4" }) Set(key, brush);
        Set("ThemeAccentColor", color);
        var inkKey = name is "Yellow" or "Orange" or "Green" ? "UiAccentDarkInk" : "UiAccentLightInk";
        if (app.TryFindResource(inkKey, app.ActualThemeVariant, out var ink))
            foreach (var key in new[] { "UiAccentText", "QcAccentText" }) Set(key, ink!);
        var soft = new SolidColorBrush(color, dark ? .22 : .12);
        foreach (var key in new[] { "UiSelection", "UiAccentSoft", "ShellSelected", "AcSelection" }) Set(key, soft);
        foreach (var key in new[] { "UiPrimaryHover", "UiPrimaryPressed" }) Set(key, brush);
        Set("UiFocusShadow", new BoxShadows(new BoxShadow { Spread = 3, Color = Color.FromArgb(70, color.R, color.G, color.B) }));
        Set("AcFocusShadow", new BoxShadows(new BoxShadow { Spread = 3, Color = Color.FromArgb(70, color.R, color.G, color.B) }));
    }
}
