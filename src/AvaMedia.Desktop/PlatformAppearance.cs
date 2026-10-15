using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AvaMedia.Desktop;

internal static class PlatformAppearance
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("IsEnabled", typeof(PlatformAppearance));

    static PlatformAppearance() => IsEnabledProperty.Changed.AddClassHandler<Window>((window, change) =>
    {
        if (change.NewValue is not true || !Controls.WindowChrome.IsApplicationWindow(window)) return;
        var queued = false;
        void Refresh()
        {
            if (queued) return;
            queued = true;
            Dispatcher.UIThread.Post(() =>
            {
                queued = false;
                RenderOptions.SetTextRenderingMode(window, window.ActualThemeVariant == Skin.MacOS9
                    ? TextRenderingMode.Alias : OperatingSystem.IsMacOS()
                        ? TextRenderingMode.Antialias : TextRenderingMode.SubpixelAntialias);
            }, DispatcherPriority.Background);
        }
        window.Opened += (_, _) => Refresh();
        window.ActualThemeVariantChanged += (_, _) => Refresh();
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty || args.Property == ContentControl.ContentProperty) Refresh();
        };
        Refresh();
    });

    internal const string Inter = "avares://AvaMedia.Desktop/Assets/Fonts/Inter#Inter";
    internal const string Noto = "avares://AvaMedia.Desktop/Assets/Fonts/NotoSansSC#Noto Sans SC";
    internal const string MacFont = ".AppleSystemUIFont, .SF NS, SF Pro Text, PingFang SC, " + Noto;
    internal const string WindowsFont = "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, " + Inter + ", " + Noto;

    internal static FontManagerOptions FontOptions() => new()
    {
        // Skia's unspecified macOS face can be Helvetica; prefer native SF explicitly.
        DefaultFamilyName = OperatingSystem.IsMacOS() ? MacFont : OperatingSystem.IsWindows() ? WindowsFont : Inter,
        FontFallbacks = OperatingSystem.IsMacOS()
            ? [Fallback("PingFang SC"), Fallback(Noto)]
            : OperatingSystem.IsWindows()
                ? [Fallback("Microsoft YaHei UI"), Fallback(Inter), Fallback(Noto)]
                : [Fallback(Noto)]
    };

    private static FontFallback Fallback(string family) => new() { FontFamily = new FontFamily(family) };

    internal static void Populate(Styles layer)
    {
        var platform = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux";
        var source = new Uri($"avares://AvaMedia.Desktop/Styles/Platform/Platform.{platform}.axaml");
        layer.Resources = new ResourceInclude(source) { Source = source }.Loaded;
        layer.Add(new Style(selector => selector.OfType<Window>())
        {
            Setters =
            {
                new Setter(Layoutable.UseLayoutRoundingProperty, true),
                new Setter(IsEnabledProperty, true)
            }
        });
    }
}

// Its position in App.axaml makes the precedence explicit, including compiled XAML.
public sealed class PlatformTokenLayer : Styles
{
    public PlatformTokenLayer() => PlatformAppearance.Populate(this);
}
