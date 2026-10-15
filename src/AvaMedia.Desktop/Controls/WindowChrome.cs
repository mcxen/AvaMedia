using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.ApplicationLifetimes;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>
/// Shared native-window policy, independent lifetime and tool placement.
/// Custom window frames are supplied only by the classic skins.
/// </summary>
internal static partial class WindowChrome
{
    /// <summary>Arrange dialog content and an optional business toolbar, without window chrome.</summary>
    public static Grid WrapDialog(Window window, Control content, Control? titleContent = null)
    {
        var root = new Grid { RowDefinitions = new("Auto,*") };
        if (titleContent is not null)
        {
            var toolbar = new Grid { Children = { titleContent } };
            root.Children.Add(toolbar);
            void Apply()
            {
                var classic = Skin.UsesCustomChrome(window.ActualThemeVariant);
                toolbar.IsVisible = window.WindowState != WindowState.FullScreen
                    && (!classic || titleContent is not TextBlock);
                toolbar.Bind(Control.HeightProperty, new DynamicResourceExtension(classic ? "UiClassicToolbarHeight" : "UiToolTitleBarHeight"));
            }
            UpdateDialog(window, Apply);
        }
        Grid.SetRow(content, 1); root.Children.Add(content);
        ApplyPlatformFrame(window);
        return root;
    }

    private static double Metric(Window window, string key, double fallback) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is double number
            && double.IsFinite(number) && number >= 0 ? number : fallback;

    public sealed class ToolPosition
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    /// <summary>Position memory is independent of native ownership and frame policy.</summary>
    internal static void RegisterToolPlacement(Window window)
    {
        if (!IsApplicationWindow(window)) return;
        // Media viewers already manage their source-screen placement and size (10.11).
        if (window is MainWindow or PlayerWindow or ImageViewerWindow) return;
        var key = "window-position-" + window.GetType().Name;
        var tracking = false;
        var moving = false;
        ToolPosition? lastPosition = null;
        window.Opened += (_, _) =>
        {
            // Modal forms and explicit source-screen/manual coordinates keep their policy.
            if (window.Owner is not null || window.WindowStartupLocation == WindowStartupLocation.Manual
                && window.IsSet(Window.WindowStartupLocationProperty)) return;
            var source = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (source is null || ReferenceEquals(source, window)) return;
            ToolPosition? saved = null;
            try { saved = new Storage().LoadToolOptions<ToolPosition>(key); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { AppDiagnostics.Record("Window position read", error); }
            var remembered = saved is null ? (PixelPoint?)null : new(saved.X, saved.Y);
            var screen = remembered is { } point ? window.Screens.ScreenFromPoint(point) : null;
            // A disconnected display invalidates the saved position; choose beside the shell again.
            var target = screen is not null && remembered is { } valid
                ? ClampPosition(window, screen, valid) : BesideSource(window, source);
            if (target is not { } position) return;
            moving = true;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = position;
            moving = false;
            tracking = true;
        };
        window.PositionChanged += (_, _) =>
        {
            if (!tracking || moving || !window.IsVisible || window.WindowState != WindowState.Normal) return;
            lastPosition = new() { X = window.Position.X, Y = window.Position.Y };
        };
        window.Closed += (_, _) =>
        {
            if (lastPosition is null) return;
            try { new Storage().SaveToolOptions(key, lastPosition); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { AppDiagnostics.Record("Window position save", error); }
        };
    }

    private static PixelSize FramePixels(Window window, double scale)
    {
        var size = window.FrameSize ?? window.ClientSize;
        return new(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
    }

    private static PixelPoint ClampPosition(Window window, Avalonia.Platform.Screen screen, PixelPoint point)
    {
        var area = screen.WorkingArea; var size = FramePixels(window, screen.Scaling);
        return new(Math.Clamp(point.X, area.X, Math.Max(area.X, area.Right - size.Width)),
            Math.Clamp(point.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
    }

    private static PixelPoint? BesideSource(Window window, Window source)
    {
        var preferred = source.Screens.ScreenFromWindow(source) ?? source.Screens.ScreenFromPoint(source.Position)
            ?? window.Screens.Primary;
        if (preferred is null) return null;
        // Desktop coordinates differ from rendering pixels on macOS Retina displays.
        var sourceSize = FramePixels(source, source.DesktopScaling);
        var sourceBounds = new PixelRect(source.Position, sourceSize);
        var area = preferred.WorkingArea; var size = FramePixels(window, preferred.Scaling);
        var gap = (int)Math.Ceiling(Metric(window, "UiToolWindowGap", 12) * preferred.Scaling);
        foreach (var x in new[] { sourceBounds.Right + gap, sourceBounds.X - gap - size.Width })
        {
            var candidate = ClampPosition(window, preferred, new(x, source.Position.Y));
            var bounds = new PixelRect(candidate, size);
            if (area.Contains(bounds) && !bounds.Intersects(sourceBounds)) return candidate;
        }
        // Limited screen space permits overlap; keep the tool centered on the source display.
        return ClampPosition(window, preferred, area.CenterRect(new PixelRect(size)).Position);
    }

    public static void ToggleMaximize(Window window)
    {
        if (window.CanResize && window.CanMaximize)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

}
