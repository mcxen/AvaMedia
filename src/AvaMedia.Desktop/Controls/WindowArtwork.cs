using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace AvaMedia.Desktop.Controls;

// Keep native window icons and the Platinum caption on the same feature and skin.
public sealed class WindowArtwork : AvaloniaObject
{
    public static readonly AttachedProperty<string> KindProperty = AvaloniaProperty.RegisterAttached<WindowArtwork, Window, string>("Kind", "");
    public static readonly AttachedProperty<Bitmap?> ImageProperty = AvaloniaProperty.RegisterAttached<WindowArtwork, Window, Bitmap?>("Image");
    private static readonly ConditionalWeakTable<Window, Registration> Windows = new();
    private static readonly Dictionary<(string Kind, bool Classic), WindowIcon> Icons = new();

    static WindowArtwork() => KindProperty.Changed.AddClassHandler<Window>((window, _) => Attach(window).Refresh());

    public static string GetKind(Window window) => window.GetValue(KindProperty);
    public static void SetKind(Window window, string kind) => window.SetValue(KindProperty, kind);
    public static Bitmap? GetImage(Window window) => window.GetValue(ImageProperty);
    internal static void Enable(Window window) => Attach(window);
    private static Registration Attach(Window window) => Windows.GetValue(window, w => new Registration(w));

    private static string EffectiveKind(Window window)
    {
        if (GetKind(window) is { Length: > 0 } kind) return kind;
        return window switch
        {
            MainWindow => "",
            BatchCropWindow => "crop",
            BatchRotateWindow => "rotate",
            QuickClipWindow => "clip",
            EditorWindow => "clip",
            ClipExportWindow => "clip",
            ClipSplitWindow => "split",
            PlayerWindow => "player",
            DownloadWindow => "download",
            BatchToolsWindow => "gear",
            OptionsWindow or SettingsWindow or HardwareTestWindow or ShutdownCountdownWindow => "gear",
            UpdateWindow => "info",
            _ => window.Owner is Window owner ? EffectiveKind(owner) : ""
        };
    }

    private sealed class Registration
    {
        private readonly Window _window;
        private bool _customIcon;

        public Registration(Window window)
        {
            _window = window;
            window.ActualThemeVariantChanged += Changed;
            window.Opened += Changed;
            window.Closed += Closed;
            Refresh();
        }

        public void Refresh()
        {
            var kind = EffectiveKind(_window);
            var classic = _window.ActualThemeVariant == Skin.MacOS9;
            var artwork = FeatureIconAssets.Get(kind, classic);
            _window.SetValue(ImageProperty, artwork ?? ApplicationArtwork.Image);
            if (artwork is not null)
            {
                var key = (kind, classic);
                if (!Icons.TryGetValue(key, out var icon)) Icons[key] = icon = new WindowIcon(artwork);
                _window.Icon = icon;
                _customIcon = true;
            }
            else if (_customIcon)
            {
                _window.ClearValue(Window.IconProperty);
                _customIcon = false;
            }
        }

        private void Changed(object? sender, EventArgs e) => Refresh();
        private void Closed(object? sender, EventArgs e)
        {
            _window.ActualThemeVariantChanged -= Changed;
            _window.Opened -= Changed;
            _window.Closed -= Closed;
            Windows.Remove(_window);
        }
    }
}
