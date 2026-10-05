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
        Background = Brush.Parse("#CCCCCC"); BorderBrush = Brushes.Black; BorderThickness = new(1); Padding = new(3);
        var grid = new Grid { RowDefinitions = new("25,*") };
        var titleBar = new Grid { ColumnDefinitions = new("25,*,52"), Background = Background, Name = "PlatinumTitleBar" };
        var lines = new PlatinumTitleLines(window); Grid.SetColumnSpan(lines, 3); titleBar.Children.Add(lines);
        var close = CaptionButton("□", "关闭窗口", "PlatinumClose", () => window.Close()); titleBar.Children.Add(close);
        var title = new TextBlock { FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new(9, 0), Background = Background };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window }); Grid.SetColumn(title, 1); titleBar.Children.Add(title);
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
        var button = new Button { Content = text, Name = name, Width = 20, Height = 19, Padding = new(0), Margin = new(0, 2), FontSize = 12, IsEnabled = enabled, Focusable = false, Classes = { "platinum-caption" } };
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

internal sealed class PlatinumTitleLines : Control
{
    private readonly Window _window;
    public PlatinumTitleLines(Window window) { _window = window; window.PropertyChanged += Changed; DetachedFromVisualTree += (_, _) => window.PropertyChanged -= Changed; }
    private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Window.IsActiveProperty) InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        if (!_window.IsActive && _window.IsVisible) return;
        for (var y = 5.5; y < Bounds.Height - 3; y += 3)
        {
            context.DrawLine(new Pen(Brush.Parse("#999999"), 1), new(1, y), new(Bounds.Width - 1, y));
            context.DrawLine(new Pen(Brushes.White, 1), new(1, y + 1), new(Bounds.Width - 1, y + 1));
        }
    }
}
