using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class ImageViewerWindow
{
    public static void ShowPreview(Window owner, IImage? image, string caption)
    {
        if (image is not Bitmap bitmap) return;
        // Keep the displayed frame alive independently of its source row or playback session.
        var snapshot = bitmap.CreateScaledBitmap(bitmap.PixelSize, BitmapInterpolationMode.None);
        try { new CoverPreviewWindow(snapshot, caption).Show(owner); }
        catch { snapshot.Dispose(); throw; }
    }

    // Single-image mode shares Tianchi's viewport without constructing its library or reading files.
    private sealed class CoverPreviewWindow : Window
    {
        private readonly ImageViewport _viewport = new();
        private readonly WrapPanel _tools = new() { Orientation = Orientation.Horizontal };
        private WindowState _beforeFullscreen;

        public CoverPreviewWindow(Bitmap snapshot, string caption)
        {
            Title = Localization.Text("天池看图") + " · " + caption;
            Width = 960; Height = 720; MinWidth = 420; MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowArtwork.SetKind(this, "image");
            RenderOptions.SetBitmapInterpolationMode(_viewport, BitmapInterpolationMode.HighQuality);
            var root = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8, Margin = new(8) };
            var surface = new Border { Child = _viewport, ClipToBounds = true };
            surface.Bind(BackgroundProperty, new DynamicResourceExtension("UiMediaSurface"));
            root.Children.Add(surface);
            Add("适应窗口", () => _viewport.Fit("fit"));
            Add("原始尺寸", () => _viewport.Fit("actual"));
            Add("−", () => _viewport.ZoomBy(1 / 1.2));
            Add("+", () => _viewport.ZoomBy(1.2));
            Add("左转", () => _viewport.Rotate(-90));
            Add("右转", () => _viewport.Rotate(90));
            Add("镜像", _viewport.Flip);
            Add("全屏", ToggleFullscreen);
            Add("关闭窗口", Close);
            Grid.SetRow(_tools, 1); root.Children.Add(_tools); Content = root;
            _viewport.SetImages(snapshot);
            Opened += (_, _) => _viewport.Focus();
            Closed += (_, _) => { _viewport.SetImages(null); snapshot.Dispose(); };
            KeyDown += PreviewKeyDown;
        }

        private void Add(string label, Action action)
        {
            var button = Ui.Button(label, action); button.Margin = new(0, 0, 6, 4);
            _tools.Children.Add(button);
        }

        private void ToggleFullscreen()
        {
            if (WindowState == WindowState.FullScreen)
            { WindowState = _beforeFullscreen; _tools.IsVisible = true; }
            else
            { _beforeFullscreen = WindowState; _tools.IsVisible = false; WindowState = WindowState.FullScreen; }
        }

        private void PreviewKeyDown(object? sender, KeyEventArgs args)
        {
            switch (args.Key)
            {
                case Key.Escape:
                    if (WindowState == WindowState.FullScreen) ToggleFullscreen(); else Close();
                    break;
                case Key.F11: ToggleFullscreen(); break;
                case Key.Enter when args.KeyModifiers.HasFlag(KeyModifiers.Alt): ToggleFullscreen(); break;
                case Key.Add: case Key.OemPlus: _viewport.ZoomBy(1.2); break;
                case Key.Subtract: case Key.OemMinus: _viewport.ZoomBy(1 / 1.2); break;
                case Key.D0: _viewport.Fit("actual"); break;
                case Key.D1: case Key.D9: _viewport.Fit("fit"); break;
                case Key.D8: _viewport.Fit("width"); break;
                case Key.R: _viewport.Rotate(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -90 : 90); break;
                default: return;
            }
            args.Handled = true;
        }
    }
}
