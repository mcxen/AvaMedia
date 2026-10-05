using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop.Controls;

public sealed class PlatinumWindowFrame : Border
{
    private readonly Window _window;
    private readonly ContentControl _body;
    public PlatinumWindowFrame(Window window, object content)
    {
        _window = window; Name = "PlatinumWindowFrame";
        Classes.Add("platinum-window");
        var grid = new Grid { RowDefinitions = new("25,*") };
        var titleBar = new Grid { ColumnDefinitions = new("25,*,52"), Classes = { "platinum-titlebar" }, Name = "PlatinumTitleBar" };
        var lines = new PlatinumTitleLines(window); Grid.SetColumnSpan(lines, 3); titleBar.Children.Add(lines);
        var close = CaptionButton("□", "关闭窗口", "PlatinumClose", () => window.Close()); titleBar.Children.Add(close);
        var title = new TextBlock { Classes = { "platinum-title" }, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        var appImage = new Image { Source = ApplicationArtwork.Image, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(appImage, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        var caption = new Border { Classes = { "platinum-caption-surface" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { appImage, title } } };
        Grid.SetColumn(caption, 1); titleBar.Children.Add(caption);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 3 };
        var zoom = CaptionButton("▱", "缩放窗口", "PlatinumZoom", () => { if (window.CanResize && window.CanMaximize) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }, window.CanResize && window.CanMaximize);
        zoom.Bind(Button.IsEnabledProperty, new MultiBinding { Converter = Avalonia.Data.Converters.BoolConverters.And, Bindings = { new Binding(nameof(Window.CanResize)) { Source = window }, new Binding(nameof(Window.CanMaximize)) { Source = window } } }); right.Children.Add(zoom);
        var minimize = CaptionButton("─", "最小化窗口", "PlatinumMinimize", () => { if (window.CanMinimize) window.WindowState = WindowState.Minimized; });
        minimize.Bind(Button.IsEnabledProperty, new Binding(nameof(Window.CanMinimize)) { Source = window }); right.Children.Add(minimize);
        Grid.SetColumn(right, 2); titleBar.Children.Add(right);
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed || e.Source is Visual v && v.GetVisualAncestors().OfType<Button>().Any()) return;
            if (e.ClickCount == 2 && window.CanResize && window.CanMaximize) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else window.BeginMoveDrag(e);
            e.Handled = true;
        };
        _body = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_body, 1); grid.Children.Add(titleBar); grid.Children.Add(_body); Child = grid;
        AddHandler(PointerPressedEvent, Resize, RoutingStrategies.Tunnel);
    }
    private static Button CaptionButton(string text, string description, string name, Action click, bool enabled = true)
    {
        var button = new Button { Content = text, Name = name, IsEnabled = enabled, Focusable = false, Classes = { "platinum-caption" } };
        AutomationProperties.SetName(button, description); ToolTip.SetTip(button, description); button.Click += (_, _) => click(); return button;
    }
    public object? ReleaseContent() { var content = _body.Content; _body.Content = null; return content; }
    private void Resize(object? sender, PointerPressedEventArgs e)
    {
        if (!_window.CanResize || _window.WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this); var left = p.X < 4; var right = p.X > Bounds.Width - 4; var top = p.Y < 4; var bottom = p.Y > Bounds.Height - 4;
        WindowEdge? edge = top ? left ? WindowEdge.NorthWest : right ? WindowEdge.NorthEast : WindowEdge.North
            : bottom ? left ? WindowEdge.SouthWest : right ? WindowEdge.SouthEast : WindowEdge.South
            : left ? WindowEdge.West : right ? WindowEdge.East : null;
        if (edge is { } direction) { _window.BeginResizeDrag(direction, e); e.Handled = true; }
    }
}

public sealed class PlatinumTitleLines : Control
{
    public static readonly StyledProperty<IBrush?> DarkBrushProperty = AvaloniaProperty.Register<PlatinumTitleLines,IBrush?>(nameof(DarkBrush));
    public static readonly StyledProperty<IBrush?> LightBrushProperty = AvaloniaProperty.Register<PlatinumTitleLines,IBrush?>(nameof(LightBrush));
    public IBrush? DarkBrush { get => GetValue(DarkBrushProperty); set => SetValue(DarkBrushProperty,value); }
    public IBrush? LightBrush { get => GetValue(LightBrushProperty); set => SetValue(LightBrushProperty,value); }
    static PlatinumTitleLines() => AffectsRender<PlatinumTitleLines>(DarkBrushProperty,LightBrushProperty);
    private readonly Window _window;
    public PlatinumTitleLines(Window window) { _window = window; window.PropertyChanged += Changed; DetachedFromVisualTree += (_, _) => window.PropertyChanged -= Changed; }
    private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Window.IsActiveProperty) InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        if (!_window.IsActive && _window.IsVisible) return;
        for (var y = 5.5; y < Bounds.Height - 3; y += 3)
        {
            context.DrawLine(new Pen(DarkBrush, 1), new(1, y), new(Bounds.Width - 1, y));
            context.DrawLine(new Pen(LightBrush, 1), new(1, y + 1), new(Bounds.Width - 1, y + 1));
        }
    }
}
