using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.Shapes;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop.Controls;

/// <summary>
/// Shared pieces of the modern platform frame (see MainWindow.WindowChrome / docs/WINDOW-CHROME.md):
/// own min / max / close caption buttons, X11 resize grips and title-row dragging. Used by MainWindow
/// and the quick-clip EditorWindow so both windows get the same frame on every platform.
/// </summary>
internal static class WindowChrome
{
    /// <summary>Wrap a tool dialog with the same platform detection, drag and resize behavior as the shell.</summary>
    public static Grid WrapDialog(Window window, Control content, Control? titleContent = null)
    {
        var root = new Grid { RowDefinitions = new("Auto,*") };
        var titleBar = new Grid { Height = 48, ColumnDefinitions = new("88,*,Auto") };
        if (titleContent is not null) titleBar.Bind(Panel.BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Window.Title)) { Source = window });
        title.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(Window.Title)) { Source = window });
        var headerContent = titleContent ?? title;
        Grid.SetColumn(headerContent, 1); titleBar.Children.Add(headerContent);
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        foreach (var key in new[] { "UiDanger", "UiWarning", "UiSuccessFill" })
        {
            var light = new Ellipse { Width = 12, Height = 12 };
            light.Bind(Shape.FillProperty, new DynamicResourceExtension(key)); lights.Children.Add(light);
        }
        titleBar.Children.Add(lights);
        var captions = CreateCaptionButtons(window, out _); Grid.SetColumn(captions, 2); titleBar.Children.Add(captions);
        root.Children.Add(titleBar); Grid.SetRow(content, 1); root.Children.Add(content);
        var grips = AddResizeGrips(window, root);
        var ownDecorations = false;
        void Apply()
        {
            var modern = !Skin.UsesCustomChrome(window.ActualThemeVariant) && MainWindow.FrameKind != MainWindow.WindowFrameKind.System;
            var mac = MainWindow.FrameKind == MainWindow.WindowFrameKind.Mac;
            var fullscreen = window.WindowState == WindowState.FullScreen;
            titleBar.IsVisible = !fullscreen && (modern || titleContent is not null);
            titleBar.ColumnDefinitions[0].Width = new GridLength(modern ? 88 : 0);
            lights.IsVisible = modern && mac && !OperatingSystem.IsMacOS(); captions.IsVisible = modern && !mac;
            title.Margin = new(0, 0, mac ? 88 : 0, 0);
            window.ExtendClientAreaToDecorationsHint = modern;
            window.ExtendClientAreaTitleBarHeightHint = fullscreen ? 0 : modern ? 48 : -1;
            if (modern)
            {
                window.ExtendClientAreaChromeHints = mac && OperatingSystem.IsMacOS() ? ExtendClientAreaChromeHints.PreferSystemChrome : ExtendClientAreaChromeHints.NoChrome;
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                { window.SystemDecorations = SystemDecorations.None; ownDecorations = true; }
            }
            else if (ownDecorations && !Skin.UsesCustomChrome(window.ActualThemeVariant))
            { window.SystemDecorations = SystemDecorations.Full; ownDecorations = false; }
            foreach (var grip in grips) grip.IsVisible = modern && ownDecorations && window.WindowState == WindowState.Normal;
        }
        HookDrag(window, titleBar, () => titleBar.IsVisible);
        window.ActualThemeVariantChanged += (_, _) => Apply(); window.Opened += (_, _) => Apply();
        window.PropertyChanged += (_, change) => { if (change.Property == Window.WindowStateProperty) Apply(); };
        return root;
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
        panel.Children.Add(Caption("minus", "最小化窗口", () => window.WindowState = WindowState.Minimized));
        panel.Children.Add(maximize = Caption("square", "最大化", () => ToggleMaximize(window)));
        panel.Children.Add(Caption("x", "关闭", window.Close, close: true));
        var max = maximize;
        void Glyph() { if (max.Content is LucideIcon icon) icon.Kind = window.WindowState == WindowState.Maximized ? "copy" : "square"; }
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Glyph(); };
        Glyph();
        return panel;
    }

    public static void ToggleMaximize(Window window) => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

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

    public static Border[] AddResizeGrips(Window window, Grid root)
    {
        Border Grip(WindowEdge edge, HorizontalAlignment h, VerticalAlignment v, StandardCursorType cursor)
        {
            var horizontal = h == HorizontalAlignment.Stretch; var vertical = v == VerticalAlignment.Stretch;
            var grip = new Border { Background = Brushes.Transparent, HorizontalAlignment = h, VerticalAlignment = v,
                Width = horizontal ? double.NaN : 6, Height = vertical ? double.NaN : 6, Cursor = new Cursor(cursor), ZIndex = 1000 };
            Grid.SetRowSpan(grip, Math.Max(1, root.RowDefinitions.Count)); Grid.SetColumnSpan(grip, Math.Max(1, root.ColumnDefinitions.Count));
            grip.PointerPressed += (_, e) => { if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed && window.WindowState == WindowState.Normal) window.BeginResizeDrag(edge, e); };
            root.Children.Add(grip);
            return grip;
        }
        return
        [
            Grip(WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.LeftSide),
            Grip(WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.RightSide),
            Grip(WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, StandardCursorType.TopSide),
            Grip(WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.BottomSide),
            Grip(WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, StandardCursorType.TopLeftCorner),
            Grip(WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, StandardCursorType.TopRightCorner),
            Grip(WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, StandardCursorType.BottomLeftCorner),
            Grip(WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, StandardCursorType.BottomRightCorner),
        ];
    }
}
