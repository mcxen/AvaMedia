using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>
/// Platform-aware window frame for the modern (light / dark) skins; see docs/WINDOW-CHROME.md.
/// macOS: client area extends under the title bar, native traffic lights sit inside the floating
/// sidebar and the tree starts below them. Windows: native frame with client caption buttons.
/// Linux: native WM decorations. All platforms use Controls/WindowChrome for the outer frame.
/// AVAMEDIA_CHROME=mac|win|system overrides detection (mac on other OSes draws placeholder lights).
/// </summary>
public partial class MainWindow
{
    internal enum WindowFrameKind { Mac, WinLinux, System }

    internal static WindowFrameKind FrameKind => Environment.GetEnvironmentVariable("AVAMEDIA_CHROME")?.ToLowerInvariant() switch
    {
        "mac" => WindowFrameKind.Mac,
        "win" or "windows" or "linux" => WindowFrameKind.WinLinux,
        "system" => WindowFrameKind.System,
#if MACOS
        _ => WindowFrameKind.Mac
#else
        _ => OperatingSystem.IsMacOS() ? WindowFrameKind.Mac : WindowFrameKind.WinLinux
#endif
    };

    private StackPanel? _captionButtons;
    private Control? _macLights;
    private Button? _maximizeButton;
    private bool _frameHooked;

    private void ApplyWindowFrame(bool modern)
    {
        var kind = modern ? FrameKind : WindowFrameKind.System;
        HookTitleDrag();
        WindowChrome.ApplyPlatformFrame(this);
        if (kind == WindowFrameKind.System)
        {
            SetFrameParts(caption: false, lights: false);
            return;
        }
        if (kind == WindowFrameKind.Mac)
        {
            SetFrameParts(caption: false, lights: WindowChrome.IsMacPreview(this));
            FeatureTree.Margin = new Thickness(8, 34, 8, 10);
            return;
        }
        SetFrameParts(caption: WindowChrome.UsesClientTitleBar(this), lights: false);
    }

    private void SetFrameParts(bool caption, bool lights)
    {
        FeatureTree.Margin = new Thickness(8, 10, 8, 10);
        if (caption) EnsureCaptionButtons();
        if (_captionButtons is not null) _captionButtons.IsVisible = caption;
        if (lights) EnsureMacLights();
        if (_macLights is not null) _macLights.IsVisible = lights;
    }

    private void HookTitleDrag()
    {
        if (_frameHooked) return;
        _frameHooked = true;
        // Listen on the window: transparent gaps do not hit the title grids, and the
        // sidebar header sits outside them. Use the laid-out title height across both columns.
        PointerPressed += (_, e) =>
        {
            if (!ModernChrome || FrameKind == WindowFrameKind.System || e.Source is Control c && IsInteractive(c)) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var titlePoint = e.GetPosition(ModernTitleBar);
            var queuePoint = e.GetPosition(ModernQueueBar);
            var inTitle = titlePoint.Y >= -ModernTitleBar.Margin.Top && titlePoint.Y < ModernTitleBar.Bounds.Height;
            var inQueue = new Rect(ModernQueueBar.Bounds.Size).Contains(queuePoint);
            if (!inTitle && !inQueue) return;
            e.Handled = true;
            if (e.ClickCount == 2) ToggleMaximize(); else BeginMoveDrag(e);
        };
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateMaximizeGlyph(); };
    }

    private static bool IsInteractive(Control control) => WindowChrome.IsInteractive(control);

    private void ToggleMaximize() => WindowChrome.ToggleMaximize(this);

    private void EnsureCaptionButtons()
    {
        if (_captionButtons is not null) return;
        _captionButtons = WindowChrome.CreateCaptionButtons(this, out var maximize);
        _maximizeButton = maximize;
        _captionButtons.Margin = new Thickness(10, -4, -14, 0);
        ModernTitleBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetColumn(_captionButtons, ModernTitleBar.ColumnDefinitions.Count - 1);
        ModernTitleBar.Children.Add(_captionButtons);
    }

    private void UpdateMaximizeGlyph()
    {
        if (_maximizeButton?.Content is LucideIcon icon) icon.Kind = WindowState == WindowState.Maximized ? "copy" : "square";
    }

    private void EnsureMacLights()
    {
        if (_macLights is not null || FeatureSidebar.Child is not Control tree) return;
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 12, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        foreach (var color in new[] { "#FF5F57", "#FEBC2E", "#28C840" })
            lights.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Brush.Parse(color), Stroke = Brush.Parse("#22000000"), StrokeThickness = 0.5 });
        FeatureSidebar.Child = null;
        FeatureSidebar.Child = new Grid { Children = { tree, lights } };
        _macLights = lights;
    }

}
