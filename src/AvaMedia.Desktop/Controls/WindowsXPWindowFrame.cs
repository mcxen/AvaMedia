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

/// <summary>Shared XP non-client chrome for every window, including modal tools.</summary>
public sealed class WindowsXPWindowFrame : Border
{
    private readonly Window _window;
    private readonly ContentControl _body;
    private readonly WindowsXPFace _maximizeFace;
    private readonly WindowsXPFace _grip;
    private readonly CustomWindowResize _resize;
    private readonly ContextMenu _systemMenu;
    private readonly MenuItem _restore;
    private readonly MenuItem _minimize;
    private readonly MenuItem _maximize;
    private readonly MenuItem _move;
    private readonly MenuItem _size;
    private int _systemAction;
    private PixelPoint _originalPosition;
    private Size _originalSize;

    public WindowsXPWindowFrame(Window window, object content)
    {
        _window = window;
        Name = "WindowsXPWindowFrame"; Classes.Add("xp-window");
        var layout = new Grid { RowDefinitions = new("30,*") };
        var titleBar = new Border { Classes = { "xp-titlebar" }, Name = "XPTitleBar" };
        var titleLayout = new Grid { ColumnDefinitions = new("23,*,Auto"), Margin = new(3, 0, 2, 0) };
        var icon = new FeatureIcon { Kind = WindowArtwork.EffectiveKind(window) is { Length: > 0 } kind ? kind : "player", Label = "", Width = 16, Height = 16 };
        var iconButton = new Button { Content = icon, Classes = { "xp-system-icon" }, Focusable = false };
        titleLayout.Children.Add(iconButton);
        var text = new TextBlock
        {
            Classes = { "xp-title" }, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(1, 0, 4, 0)
        };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        var shadow = new TextBlock { Classes = { "xp-title", "xp-title-shadow" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(2, 1, 3, 0) };
        shadow.Bind(TextBlock.TextProperty, new Binding(nameof(Window.Title)) { Source = window });
        var caption = new Panel { Children = { shadow, text } };
        Grid.SetColumn(caption, 1); titleLayout.Children.Add(caption);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var minimize = CaptionButton("minimize", "最小化", () => window.WindowState = WindowState.Minimized);
        minimize.Bind(IsVisibleProperty, new Binding(nameof(Window.CanMinimize)) { Source = window });
        buttons.Children.Add(minimize);
        var maximize = CaptionButton("maximize", "最大化 / 还原", Zoom);
        _maximizeFace = (WindowsXPFace)maximize.Content!;
        maximize.Bind(IsVisibleProperty, new Binding(nameof(Window.CanMaximize)) { Source = window });
        maximize.Bind(IsEnabledProperty, new Binding(nameof(Window.CanResize)) { Source = window });
        buttons.Children.Add(maximize);
        buttons.Children.Add(CaptionButton("close", "关闭窗口", window.Close));
        Grid.SetColumn(buttons, 2); titleLayout.Children.Add(buttons);
        titleBar.Child = titleLayout;
        _body = new ContentControl { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(_body, 1); layout.Children.Add(titleBar); layout.Children.Add(_body);
        _grip = new WindowsXPFace { Kind = "grip", Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
        Grid.SetRow(_grip, 1); layout.Children.Add(_grip); Child = layout;

        _restore = SystemItem("还原", () => window.WindowState = WindowState.Normal);
        _move = SystemItem("移动", () => BeginSystemAction(1));
        _size = SystemItem("大小", () => BeginSystemAction(2));
        _minimize = SystemItem("最小化", () => window.WindowState = WindowState.Minimized);
        _maximize = SystemItem("最大化", Zoom);
        _systemMenu = new ContextMenu { Items = { _restore, _move, _size, _minimize, _maximize, new Separator(), SystemItem("关闭窗口", window.Close) } };
        _systemMenu.Opening += (_, _) => UpdateSystemMenu();
        titleBar.ContextMenu = _systemMenu;
        iconButton.Click += (_, _) => _systemMenu.Open(iconButton);
        iconButton.DoubleTapped += (_, e) => { _systemMenu.Close(); window.Close(); e.Handled = true; };
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed || e.Source is Visual v && v.GetVisualAncestors().Prepend(v).OfType<Button>().Any()) return;
            if (e.ClickCount == 2) Zoom(); else window.BeginMoveDrag(e);
            e.Handled = true;
        };
        window.PropertyChanged += WindowChanged;
        window.AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        _resize = new CustomWindowResize(window, this, _grip);
        AddHandler(PointerPressedEvent, (_, _) => _systemAction = 0, RoutingStrategies.Tunnel, handledEventsToo: true);
        UpdateActivity();
    }

    private Button CaptionButton(string kind, string label, Action click)
    {
        var face = new WindowsXPFace { Kind = kind, Width = 21, Height = 21 };
        face.Bind(WindowsXPFace.IsActiveProperty, new Binding(nameof(Window.IsActive)) { Source = _window });
        var button = new Button { Content = face, Classes = { "xp-caption" }, Name = "XP" + kind, Focusable = false };
        face.Bind(WindowsXPFace.IsPressedProperty, button.GetObservable(Button.IsPressedProperty));
        face.Bind(WindowsXPFace.IsHotProperty, button.GetObservable(IsPointerOverProperty));
        face.Bind(IsEnabledProperty, button.GetObservable(IsEnabledProperty));
        AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    private static MenuItem SystemItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    public void Zoom()
    {
        if (!_window.CanResize || !_window.CanMaximize) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateSystemMenu()
    {
        _restore.IsEnabled = _window.WindowState != WindowState.Normal;
        _move.IsEnabled = _window.WindowState == WindowState.Normal;
        _size.IsEnabled = _window.CanResize && _window.WindowState == WindowState.Normal;
        _minimize.IsEnabled = _window.CanMinimize && _window.WindowState != WindowState.Minimized;
        _maximize.IsEnabled = _window.CanResize && _window.CanMaximize && _window.WindowState != WindowState.Maximized;
    }

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_systemAction != 0)
        {
            if (e.Key is Key.Escape or Key.Enter)
            {
                if (e.Key == Key.Escape)
                { _window.Position = _originalPosition; if (_systemAction == 2) { _window.Width = _originalSize.Width; _window.Height = _originalSize.Height; } }
                _systemAction = 0; e.Handled = true; return;
            }
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1 : 10;
            var dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            var dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            if (dx != 0 || dy != 0)
            {
                if (_systemAction == 1) _window.Position = new PixelPoint(_window.Position.X + dx, _window.Position.Y + dy);
                else
                {
                    _window.Width = Math.Clamp(_window.ClientSize.Width + dx, _window.MinWidth, _window.MaxWidth);
                    _window.Height = Math.Clamp(_window.ClientSize.Height + dy, _window.MinHeight, _window.MaxHeight);
                }
                e.Handled = true; return;
            }
        }
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Alt)
        { _systemMenu.Open(this); e.Handled = true; }
        else if (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.Alt)
        { _window.Close(); e.Handled = true; }
    }

    private void BeginSystemAction(int action)
    {
        _originalPosition = _window.Position; _originalSize = _window.ClientSize;
        _systemAction = action;
    }

    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsActiveProperty || e.Property == Window.WindowStateProperty || e.Property == Window.CanResizeProperty)
            UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (!_window.IsActive || _window.WindowState != WindowState.Normal) _systemAction = 0;
        Classes.Set("inactive", !_window.IsActive);
        Classes.Set("maximized", _window.WindowState == WindowState.Maximized);
        _maximizeFace.Kind = _window.WindowState == WindowState.Maximized ? "restore" : "maximize";
        _grip.IsVisible = _window.CanResize && _window.WindowState == WindowState.Normal;
    }

    public object? ReleaseContent()
    {
        _systemMenu.Close();
        _resize.Dispose();
        _window.PropertyChanged -= WindowChanged;
        _window.RemoveHandler(KeyDownEvent, WindowKeyDown);
        var content = _body.Content; _body.Content = null;
        return content;
    }
}
