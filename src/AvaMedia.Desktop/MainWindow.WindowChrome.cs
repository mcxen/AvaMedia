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
    private Grid? _macSidebarLayout;
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
            EnsureMacSidebarLayout();
            SetFrameParts(caption: false, lights: WindowChrome.IsMacPreview(this));
            FeatureTree.Margin = new Thickness(8, 0, 8, 10);
            UpdateMacSidebarLayout();
            return;
        }
        SetFrameParts(caption: WindowChrome.UsesClientTitleBar(this), lights: false);
    }

    private void SetFrameParts(bool caption, bool lights)
    {
        FeatureTree.Margin = new Thickness(8, 10, 8, 10);
        if (_macSidebarLayout is not null) _macSidebarLayout.RowDefinitions[0].Height = new GridLength(0);
        if (caption) EnsureCaptionButtons();
        if (_captionButtons is not null) _captionButtons.IsVisible = caption;
        if (lights) EnsureMacLights();
        if (_macLights is not null) _macLights.IsVisible = lights;
    }

    private void HookTitleDrag()
    {
        if (_frameHooked) return;
        _frameHooked = true;
        WindowChrome.HookMacTrafficLights(this, () => ModernTitleBar, MacLightLeft);
        LayoutUpdated += (_, _) => UpdateMacSidebarLayout();
        WindowChrome.HookDrag(this, WorkspaceLayout,
            () => ModernChrome && FrameKind != WindowFrameKind.System, e =>
        {
            var titlePoint = e.GetPosition(ModernTitleBar);
            // The top row spans the sidebar as well as the task column. The queue
            // header stays within its own row, including its surrounding margins.
            return ModernTitleBar.IsEffectivelyVisible && ModernTitleBar.Bounds.Height > 0
                && titlePoint.Y >= -ModernTitleBar.Margin.Top
                && titlePoint.Y < ModernTitleBar.Bounds.Height + ModernTitleBar.Margin.Bottom
                || WindowChrome.ContainsTitlePoint(ModernQueueBar, e);
        });
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateMaximizeGlyph(); };
    }

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
        EnsureMacSidebarLayout();
        if (_macLights is not null || _macSidebarLayout is null) return;
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        foreach (var color in new[] { "#FF5F57", "#FEBC2E", "#28C840" })
            lights.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Brush.Parse(color), Stroke = Brush.Parse("#22000000"), StrokeThickness = 0.5 });
        _macSidebarLayout.Children.Add(lights);
        _macLights = lights;
        UpdateMacSidebarLayout();
    }

    private double MacLightLeft() => (FeatureSidebar.TranslatePoint(default, this)?.X ?? FeatureSidebar.Margin.Left)
        + (this.TryFindResource("UiMainTitleLightInset", ActualThemeVariant, out var value) && value is double inset ? inset : 20);

    private void EnsureMacSidebarLayout()
    {
        if (_macSidebarLayout is not null || FeatureSidebar.Child is not Control tree) return;
        FeatureSidebar.Child = null;
        _macSidebarLayout = new Grid { RowDefinitions = new("0,*") };
        Grid.SetRow(tree, 1);
        _macSidebarLayout.Children.Add(tree);
        FeatureSidebar.Child = _macSidebarLayout;
    }

    private void UpdateMacSidebarLayout()
    {
        if (_macSidebarLayout is null || !ModernChrome || FrameKind != WindowFrameKind.Mac
            || !ModernTitleBar.IsEffectivelyVisible || ModernTitleBar.Bounds.Height <= 0
            || ModernTitleBar.TranslatePoint(default, this) is not { } title
            || _macSidebarLayout.TranslatePoint(default, this) is not { } sidebar) return;
        // Keep the title band outside the ScrollViewer, aligned with the native buttons.
        var height = new GridLength(Math.Max(0, title.Y + ModernTitleBar.Bounds.Height - sidebar.Y));
        if (_macSidebarLayout.RowDefinitions[0].Height != height) _macSidebarLayout.RowDefinitions[0].Height = height;
        if (_macLights is not null)
        {
            var margin = new Thickness(MacLightLeft() - sidebar.X,
                Math.Max(0, title.Y + ModernTitleBar.Bounds.Height / 2 - sidebar.Y - 6), 0, 0);
            if (_macLights.Margin != margin) _macLights.Margin = margin;
        }
    }

}
