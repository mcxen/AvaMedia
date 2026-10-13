using Avalonia;
using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Common top-level lifetime, frame and normal-size memory for Tianchi media windows.</summary>
internal static class MediaViewerWindow
{
    public sealed class SizePreference
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public static void Configure(Window window, Control content, Storage storage, string key)
    {
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (storage.LoadToolOptions<SizePreference>(key) is { } size
            && double.IsFinite(size.Width) && double.IsFinite(size.Height)
            && size.Width >= window.MinWidth && size.Height >= window.MinHeight)
        { window.Width = size.Width; window.Height = size.Height; }
        var normalSize = new SizePreference { Width = window.Width, Height = window.Height };
        window.Content = null;
        window.Content = WindowChrome.WrapDialog(window, content);
        window.SizeChanged += (_, _) =>
        {
            if (!window.IsVisible || window.WindowState != WindowState.Normal) return;
            normalSize.Width = window.ClientSize.Width;
            normalSize.Height = window.ClientSize.Height;
        };
        window.Closed += (_, _) => storage.SaveToolOptions(key, normalSize);
    }

    public static void Show(Window window, Window source)
    {
        if (window.IsVisible)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            Skin.RestoreWindow(window);
            window.Activate();
            return;
        }
        if (source.Screens.ScreenFromWindow(source) is { } screen)
        {
            var area = screen.WorkingArea; var scale = screen.Scaling;
            window.Width = Math.Max(window.MinWidth, Math.Min(window.Width, area.Width / scale));
            window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, area.Height / scale));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - (int)Math.Round(window.Width * scale)) / 2),
                area.Y + Math.Max(0, (area.Height - (int)Math.Round(window.Height * scale)) / 2));
        }
        // An owner makes macOS media windows fullscreen auxiliaries and keeps them above the task window.
        window.Show();
        window.Activate();
    }
}
