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
    private static readonly ConditionalWeakTable<Bitmap, WindowIcon> Icons = new();

    static WindowArtwork() => KindProperty.Changed.AddClassHandler<Window>((window, _) => Attach(window).Refresh());

    public static string GetKind(Window window) => window.GetValue(KindProperty);
    public static void SetKind(Window window, string kind) => window.SetValue(KindProperty, kind);
    public static Bitmap? GetImage(Window window) => window.GetValue(ImageProperty);
    internal static void Enable(Window window) => Attach(window);
    private static Registration Attach(Window window) => Windows.GetValue(window, w => new Registration(w));

    internal static string EffectiveKind(Window window)
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
            RenameWindow => "gear",
            ContactSheetWindow => "frames",
            OptionsWindow or SettingsWindow or HardwareTestWindow or ShutdownCountdownWindow => "gear",
            UpdateWindow => "info",
            _ => window.Owner is Window owner ? EffectiveKind(owner) : ""
        };
    }

    private sealed class Registration
    {
        private readonly Window _window;
        private bool _customIcon;
        private FeatureIconAssets.Lease? _artwork;
        private (string Kind, bool Classic, int Width)? _artworkKey;

        public Registration(Window window)
        {
            _window = window;
            window.ActualThemeVariantChanged += Changed;
            window.Opened += Changed;
            window.ScalingChanged += Changed;
            window.Closed += Closed;
            Refresh();
        }

        public void Refresh()
        {
            var kind = EffectiveKind(_window);
            var classic = _window.ActualThemeVariant == Skin.MacOS9;
            var key = (kind, classic, FeatureIconAssets.PixelWidth(Math.Max(64, 32 * _window.RenderScaling)));
            var previous = _artwork;
            if (_artworkKey != key) { _artwork = FeatureIconAssets.Acquire(kind, classic, key.Item3); _artworkKey = key; }
            var artwork = _artwork?.Bitmap;
            _window.SetValue(ImageProperty, classic ? artwork ?? ApplicationArtwork.Image : null);
            if (artwork is not null)
            {
                _window.Icon = Icons.GetValue(artwork, image => new WindowIcon(image));
                _customIcon = true;
            }
            else if (_customIcon)
            {
                _window.ClearValue(Window.IconProperty);
                _customIcon = false;
            }
            if (!ReferenceEquals(previous, _artwork)) previous?.Dispose();
        }

        private void Changed(object? sender, EventArgs e) => Refresh();
        private void Closed(object? sender, EventArgs e)
        {
            _window.ActualThemeVariantChanged -= Changed;
            _window.Opened -= Changed;
            _window.ScalingChanged -= Changed;
            _window.Closed -= Closed;
            _window.ClearValue(ImageProperty);
            if (_customIcon) _window.ClearValue(Window.IconProperty);
            _artwork?.Dispose(); _artwork = null;
            Windows.Remove(_window);
        }
    }
}
