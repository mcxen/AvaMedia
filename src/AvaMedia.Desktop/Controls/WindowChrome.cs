using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;

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
        void Apply()
        {
            var modern = UsesClientTitleBar(window);
            var mac = MainWindow.FrameKind == MainWindow.WindowFrameKind.Mac;
            var fullscreen = window.WindowState == WindowState.FullScreen;
            var preview = IsMacPreview(window);
            titleBar.IsVisible = !fullscreen && (modern || preview || titleContent is not null);
            titleBar.ColumnDefinitions[0].Width = new GridLength(modern || preview ? 88 : 0);
            lights.IsVisible = preview; captions.IsVisible = modern && !mac;
            title.Margin = new(0, 0, modern && mac || preview ? 88 : 0, 0);
            ApplyPlatformFrame(window);
        }
        HookDrag(window, titleBar, () => titleBar.IsVisible);
        UpdateDialog(window, Apply);
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
