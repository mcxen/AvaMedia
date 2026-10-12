using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>
/// Platform-aware window frame for the modern (light / dark) skins; see docs/WINDOW-CHROME.md.
/// macOS: client area extends under the title bar, native traffic lights sit inside the floating
/// sidebar and the tree starts below them. Windows / Linux: no title bar, our own min / max / close
/// at the top right of the title row; the title row drags and double-click toggles maximize.
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
    private Border[]? _resizeGrips;
    private Button? _maximizeButton;
    private bool _frameHooked, _ownDecorations;

    private void ApplyWindowFrame(bool modern)
    {
        var kind = modern ? FrameKind : WindowFrameKind.System;
        HookTitleDrag();
        if (kind == WindowFrameKind.System)
        {
            ExtendClientAreaToDecorationsHint = false;
            // Only undo our own change: Mac OS 9 / XP skins manage decorations for their custom frames.
            if (_ownDecorations) { SystemDecorations = SystemDecorations.Full; _ownDecorations = false; }
            SetFrameParts(caption: false, lights: false, grips: false);
            return;
        }
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 52;
        if (kind == WindowFrameKind.Mac)
        {
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome | ExtendClientAreaChromeHints.OSXThickTitleBar;
            // Real macOS draws the native lights; elsewhere draw a placeholder to preview spacing.
            SetFrameParts(caption: false, lights: !OperatingSystem.IsMacOS(), grips: false);
            FeatureTree.Margin = new Thickness(8, 34, 8, 10);
            return;
        }
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        // X11 cannot extend into WM decorations: drop them and supply our own resize edges.
        var ownFrame = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        if (ownFrame) { SystemDecorations = SystemDecorations.None; _ownDecorations = true; }
        SetFrameParts(caption: true, lights: false, grips: ownFrame);
    }

    private void SetFrameParts(bool caption, bool lights, bool grips)
    {
        FeatureTree.Margin = new Thickness(8, 10, 8, 10);
        if (caption) EnsureCaptionButtons();
        if (_captionButtons is not null) _captionButtons.IsVisible = caption;
        if (lights) EnsureMacLights();
        if (_macLights is not null) _macLights.IsVisible = lights;
        if (grips) EnsureResizeGrips();
        foreach (var grip in _resizeGrips ?? []) grip.IsVisible = grips;
    }

    private void HookTitleDrag()
    {
        if (_frameHooked) return;
        _frameHooked = true;
        foreach (var region in new Control[] { ModernTitleBar, ModernQueueBar })
            region.PointerPressed += (_, e) =>
            {
                if (!ModernChrome || FrameKind == WindowFrameKind.System || e.Source is Control c && IsInteractive(c)) return;
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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

    private void EnsureResizeGrips()
    {
        if (_resizeGrips is not null || Content is not Grid root) return;
        _resizeGrips = WindowChrome.AddResizeGrips(this, root);
    }
}
