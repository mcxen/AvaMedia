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
    private readonly Grid _layout;
    private readonly PlatinumGlyph _growBox;
    private double _expandedHeight;
    private double _expandedMinHeight;
    private SizeToContent _expandedSizeToContent;
    private WindowState _expandedState;
    public bool IsShaded { get; private set; }
    public PlatinumWindowFrame(Window window, object content)
    {
        _window = window; Name = "PlatinumWindowFrame";
        WindowArtwork.Enable(window);
        Classes.Add("platinum-window");
        _layout = new Grid { RowDefinitions = new("21,*") };
        var titleBar = new Grid { ColumnDefinitions = new("20,*,36"), Classes = { "platinum-titlebar" }, Name = "PlatinumTitleBar" };
        var lines = new PlatinumTitleLines(window); Grid.SetColumnSpan(lines, 3); titleBar.Children.Add(lines);
        var close = CaptionButton("close", "关闭窗口", "PlatinumClose", () => window.Close()); titleBar.Children.Add(close);
        var title = new TextBlock { Classes = { "platinum-title" }, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        var appImage = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        appImage.Bind(Image.SourceProperty, window.GetObservable(WindowArtwork.ImageProperty));
        RenderOptions.SetBitmapInterpolationMode(appImage, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        var caption = new Border { Classes = { "platinum-caption-surface" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { appImage, title } } };
        Grid.SetColumn(caption, 1); titleBar.Children.Add(caption);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 4 };
        var zoom = CaptionButton("zoom", "缩放窗口", "PlatinumZoom", Zoom, window.CanResize && window.CanMaximize);
        zoom.Bind(Button.IsEnabledProperty, new MultiBinding { Converter = Avalonia.Data.Converters.BoolConverters.And, Bindings = { new Binding(nameof(Window.CanResize)) { Source = window }, new Binding(nameof(Window.CanMaximize)) { Source = window } } }); right.Children.Add(zoom);
        zoom.Bind(Button.IsVisibleProperty, new Binding(nameof(Window.CanResize)) { Source = window });
        var shade = CaptionButton("collapse", "收起 / 展开标题栏", "PlatinumShade", ToggleShade);
        shade.Bind(Button.IsVisibleProperty, new Binding(nameof(Window.CanMinimize)) { Source = window }); right.Children.Add(shade);
        Grid.SetColumn(right, 2); titleBar.Children.Add(right);
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed || e.Source is Button || e.Source is Visual v && v.GetVisualAncestors().OfType<Button>().Any()) return;
            if (e.ClickCount == 2) ToggleShade();
            else window.BeginMoveDrag(e);
            e.Handled = true;
        };
        _body = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_body, 1); _layout.Children.Add(titleBar); _layout.Children.Add(_body);
        _growBox = new PlatinumGlyph { Kind = "resize", Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
        Grid.SetRow(_growBox, 1); _layout.Children.Add(_growBox); Child = _layout;
        window.PropertyChanged += WindowChanged;
        UpdateActivity();
        AddHandler(PointerPressedEvent, Resize, RoutingStrategies.Tunnel);
    }
    private Button CaptionButton(string kind, string description, string name, Action click, bool enabled = true)
    {
        var glyph = new PlatinumGlyph { Kind = kind, Width = 14, Height = 14 };
        var button = new Button { Content = glyph, Name = name, IsEnabled = enabled, Focusable = false, Classes = { "platinum-caption" } };
        glyph.Bind(PlatinumGlyph.IsActiveProperty, new Binding(nameof(Window.IsActive)) { Source = _window });
        glyph.Bind(PlatinumGlyph.IsPressedProperty, button.GetObservable(Button.IsPressedProperty));
        AutomationProperties.SetName(button, description); ToolTip.SetTip(button, description); button.Click += (_, _) => click(); return button;
    }
    public void Zoom()
    {
        if (!_window.CanResize || !_window.CanMaximize) return;
        RestoreShade();
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    public void ToggleShade()
    {
        if (!_window.CanMinimize) return;
        if (IsShaded) { RestoreShade(); return; }
        _expandedHeight = double.IsNaN(_window.Height) ? _window.ClientSize.Height : _window.Height;
        _expandedMinHeight = _window.MinHeight;
        _expandedSizeToContent = _window.SizeToContent;
        _expandedState = _window.WindowState;
        _window.WindowState = WindowState.Normal;
        IsShaded = true; _body.IsVisible = false; _growBox.IsVisible = false;
        _window.SizeToContent = SizeToContent.Manual; _window.MinHeight = 0;
        _window.Height = 21 + Padding.Top + Padding.Bottom + BorderThickness.Top + BorderThickness.Bottom;
    }
    private void RestoreShade()
    {
        if (!IsShaded) return;
        IsShaded = false; _body.IsVisible = true;
        _window.MinHeight = _expandedMinHeight; _window.Height = _expandedHeight;
        _window.SizeToContent = _expandedSizeToContent;
        if (_window.WindowState == WindowState.Normal) _window.WindowState = _expandedState;
        UpdateActivity();
    }
    public object? ReleaseContent()
    {
        RestoreShade(); _window.PropertyChanged -= WindowChanged;
        var content = _body.Content; _body.Content = null;
        return content;
    }
    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsActiveProperty || e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty) UpdateActivity();
    }
    private void UpdateActivity()
    {
        Classes.Set("inactive", !_window.IsActive);
        _growBox.IsVisible = !IsShaded && _window.CanResize && _window.WindowState == WindowState.Normal;
    }
    private void Resize(object? sender, PointerPressedEventArgs e)
    {
        if (IsShaded || !_window.CanResize || _window.WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ReferenceEquals(e.Source, _growBox)) { _window.BeginResizeDrag(WindowEdge.SouthEast, e); e.Handled = true; return; }
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
    public PlatinumTitleLines(Window window)
    {
        _window = window; RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        AttachedToVisualTree += (_, _) => { window.PropertyChanged += Changed; InvalidateVisual(); };
        DetachedFromVisualTree += (_, _) => window.PropertyChanged -= Changed;
    }
    private void Changed(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Window.IsActiveProperty) InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        if (!_window.IsActive && _window.IsVisible) return;
        for (var y = 2.5; y < Bounds.Height - 2; y += 3)
        {
            context.DrawLine(new Pen(DarkBrush, 1), new(1, y), new(Bounds.Width - 1, y));
            context.DrawLine(new Pen(LightBrush, 1), new(1, y + 1), new(Bounds.Width - 1, y + 1));
        }
    }
}
