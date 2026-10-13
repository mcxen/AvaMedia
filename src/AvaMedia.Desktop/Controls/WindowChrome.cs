using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Avalonia.Controls.ApplicationLifetimes;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>
/// Shared pieces of the modern platform frame (see MainWindow.WindowChrome / docs/WINDOW-CHROME.md):
/// native platform policy, shared min / max / close caption buttons and title-row dragging.
/// Skin connects every tool window; MainWindow and EditorWindow retain their specialized layouts.
/// </summary>
internal static partial class WindowChrome
{
    /// <summary>Wrap a tool dialog with the same platform detection, drag and resize behavior as the shell.</summary>
    public static Grid WrapDialog(Window window, Control content, Control? titleContent = null)
    {
        var root = new Grid { RowDefinitions = new("Auto,*"), Classes = { "window-chrome" } };
        var titleBar = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        titleBar.Bind(Control.HeightProperty, new DynamicResourceExtension("UiToolTitleBarHeight"));
        if (titleContent is not null) titleBar.Bind(Panel.BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Window.Title)) { Source = window });
        title.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(Window.Title)) { Source = window });
        title.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("AcSectionFont"));
        var headerContent = titleContent ?? title;
        Grid.SetColumn(headerContent, 1); titleBar.Children.Add(headerContent);
        var lights = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        lights.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcRowGap"));
        lights.Bind(Control.MarginProperty, new DynamicResourceExtension("UiToolTitleLightInset"));
        foreach (var key in new[] { "UiDanger", "UiWarning", "UiSuccessFill" })
        {
            var light = new Ellipse { Width = 12, Height = 12 };
            light.Bind(Shape.FillProperty, new DynamicResourceExtension(key)); lights.Children.Add(light);
        }
        titleBar.Children.Add(lights);
        var captions = CreateCaptionButtons(window, out _); Grid.SetColumn(captions, 2); titleBar.Children.Add(captions);
        root.Children.Add(titleBar); Grid.SetRow(content, 1); root.Children.Add(content);
        void Apply()
        {
            var modern = UsesClientTitleBar(window);
            var mac = MainWindow.FrameKind == MainWindow.WindowFrameKind.Mac;
            var fullscreen = window.WindowState == WindowState.FullScreen;
            var preview = IsMacPreview(window);
            titleBar.IsVisible = !fullscreen && (modern || preview || titleContent is not null);
            var gutter = Metric(window, "UiToolTitleGutter", 88);
            titleBar.ColumnDefinitions[0].Width = new GridLength(modern || preview ? gutter : 0);
            lights.IsVisible = preview; captions.IsVisible = modern && !mac;
            title.Margin = new(0, 0, modern && mac || preview ? gutter : 0, 0);
            ApplyPlatformFrame(window);
            if (modern && !fullscreen)
                window.ExtendClientAreaTitleBarHeightHint = Metric(window, "UiToolTitleBarHeight", 48);
        }
        HookDrag(window, titleBar, () => titleBar.IsVisible);
        UpdateDialog(window, Apply);
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
        // Media viewers already manage their source-screen placement and size (10.11).
        if (window is MainWindow or PlayerWindow or ImageViewerWindow) return;
        var key = "window-position-" + window.GetType().Name;
        var tracking = false;
        var moving = false;
        ToolPosition? lastPosition = null;
        window.Opened += (_, _) =>
        {
            // Modal forms and explicit source-screen/manual coordinates keep their policy.
            if (window.Owner is not null || window.WindowStartupLocation == WindowStartupLocation.Manual) return;
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
        var preferred = source.Screens.ScreenFromWindow(source);
        var sourceSize = FramePixels(source, source.RenderScaling);
        var sourceBounds = new PixelRect(source.Position, sourceSize);
        foreach (var screen in window.Screens.All.OrderBy(s => s != preferred))
        {
            var area = screen.WorkingArea; var size = FramePixels(window, screen.Scaling);
            var gap = (int)Math.Ceiling(Metric(window, "UiToolWindowGap", 12) * screen.Scaling);
            foreach (var x in new[] { sourceBounds.Right + gap, sourceBounds.X - gap - size.Width, area.X, area.Right - size.Width })
            {
                var candidate = ClampPosition(window, screen, new(x, source.Position.Y));
                var bounds = new PixelRect(candidate, size);
                if (area.Contains(bounds) && !bounds.Intersects(sourceBounds)) return candidate;
            }
        }
        // Limited screen space permits overlap. Prefer the left edge so the shell's
        // task area on the right retains as much exposed space as possible.
        if (preferred is null) return null;
        var fallbackArea = preferred.WorkingArea;
        return ClampPosition(window, preferred, new(fallbackArea.X, source.Position.Y));
    }

    public static StackPanel CreateCaptionButtons(Window window, out Button maximize)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Classes = { "caption-buttons" } };
        Button Caption(string glyph, string name, Action click, bool close = false)
        {
            var button = new Button { Content = new LucideIcon { Kind = glyph, Width = 14, Height = 14 }, Classes = { "caption" }, Focusable = false };
            if (close) button.Classes.Add("close");
            ToolTip.SetTip(button, Localization.Text(name));
            Avalonia.Automation.AutomationProperties.SetName(button, Localization.Text(name));
            button.Click += (_, _) => click();
            return button;
        }
        var minimize = Caption("minus", "最小化窗口", () => window.WindowState = WindowState.Minimized);
        minimize.Bind(Button.IsEnabledProperty, new Avalonia.Data.Binding(nameof(Window.CanMinimize)) { Source = window });
        panel.Children.Add(minimize);
        panel.Children.Add(maximize = Caption("square", "最大化", () => ToggleMaximize(window)));
        maximize.Bind(Button.IsEnabledProperty, new Avalonia.Data.MultiBinding
        {
            Converter = Avalonia.Data.Converters.BoolConverters.And,
            Bindings = { new Avalonia.Data.Binding(nameof(Window.CanResize)) { Source = window },
                new Avalonia.Data.Binding(nameof(Window.CanMaximize)) { Source = window } }
        });
        panel.Children.Add(Caption("x", "关闭", window.Close, close: true));
        var max = maximize;
        void Glyph() { if (max.Content is LucideIcon icon) icon.Kind = window.WindowState == WindowState.Maximized ? "copy" : "square"; }
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Glyph(); };
        Glyph();
        return panel;
    }

    public static void ToggleMaximize(Window window)
    {
        if (window.CanResize && window.CanMaximize)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    public static bool IsInteractive(Control control)
    {
        for (Visual? c = control; c is not null; c = c.GetVisualParent())
            if (c is Button or TextBox or MenuItem or Slider or ComboBox or GridSplitter) return true;
        return false;
    }

    /// <summary>Drag the window from a title region; double-click toggles maximize.</summary>
    public static void HookDrag(Window window, Control region, Func<bool> enabled) =>
        region.PointerPressed += (_, e) =>
        {
            if (!enabled() || e.Source is Control c && IsInteractive(c)) return;
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) ToggleMaximize(window); else window.BeginMoveDrag(e);
        };

}
