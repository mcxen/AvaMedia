using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Controls;

internal static partial class WindowChrome
{
    // Awaiting a tool's close must not make it modal or create a native owner relationship.
    internal static void ShowIndependent(Window window)
    {
        window.ShowInTaskbar = true;
        if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner)
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ApplyPlatformFrame(window);
        window.Show();
    }

    internal static Task ShowIndependentAsync(Window window)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => completion.TrySetResult();
        ShowIndependent(window);
        return completion.Task;
    }

    internal static void RegisterPlatformFrame(Window window)
    {
        var pending = false;
        window.SizeChanged += (_, _) =>
        {
            if (!OperatingSystem.IsWindows() || pending) return;
            pending = true;
            // Win32 reapplies its composition margins during WM_SIZE. Restore the native
            // shadow edge after that message has finished, including maximize/restore.
            Dispatcher.UIThread.Post(() =>
            {
                pending = false;
                ApplyWindowsFrame(window, window.ExtendClientAreaToDecorationsHint);
            });
        };
    }

    private static readonly ConditionalWeakTable<Window, DialogUpdater> DialogUpdaters = new();

    private sealed class DialogUpdater
    {
        public Action? Apply { get; set; }
        public DialogUpdater(Window window)
        {
            window.ActualThemeVariantChanged += (_, _) => Apply?.Invoke();
            window.Opened += (_, _) => Apply?.Invoke();
            window.PropertyChanged += (_, change) =>
            { if (change.Property == Window.WindowStateProperty) Apply?.Invoke(); };
        }
    }

    private static void UpdateDialog(Window window, Action apply)
    {
        DialogUpdaters.GetValue(window, w => new DialogUpdater(w)).Apply = apply;
        apply();
    }
    internal static bool UsesClientTitleBar(Window window) =>
        !Skin.UsesCustomChrome(window.ActualThemeVariant)
        && MainWindow.FrameKind != MainWindow.WindowFrameKind.System
        && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    internal static bool IsMacPreview(Window window) =>
        !Skin.UsesCustomChrome(window.ActualThemeVariant)
        && MainWindow.FrameKind == MainWindow.WindowFrameKind.Mac && !OperatingSystem.IsMacOS();

    // Skin registers every Window, including temporary dialogs which have no specialized title layout.
    internal static void EnsureToolFrame(Window window)
    {
        if (window is MainWindow or EditorWindow || Skin.UsesCustomChrome(window.ActualThemeVariant)
            || window.Content is not Control content || content.Classes.Contains("window-chrome")) return;
        window.Content = null;
        window.Content = WrapDialog(window, content);
    }

    internal static void ApplyPlatformFrame(Window window)
    {
        var classic = Skin.UsesCustomChrome(window.ActualThemeVariant);
        var extended = (UsesClientTitleBar(window) || classic && OperatingSystem.IsWindows())
            && window.WindowState != WindowState.FullScreen;
        // Keep WS_CAPTION/WS_THICKFRAME on Windows, NSWindow on macOS and WM decorations on Linux.
        // A transparent, region-shaped window cannot receive native DWM corners and shadows.
        window.SystemDecorations = SystemDecorations.Full;
        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        if (window.Background is null || window.Background.Opacity < 1
            || window.Background is ISolidColorBrush brush && brush.Color.A < 255)
            window.Bind(TopLevel.BackgroundProperty, new DynamicResourceExtension("UiCanvas"));
        window.ExtendClientAreaToDecorationsHint = extended;
        window.ExtendClientAreaTitleBarHeightHint = extended ? classic ? 0 : window is MainWindow ? 52 : window is EditorWindow ? 44 : 48 : -1;
        window.ExtendClientAreaChromeHints = extended
            ? OperatingSystem.IsMacOS() ? ExtendClientAreaChromeHints.PreferSystemChrome | ExtendClientAreaChromeHints.OSXThickTitleBar
                : ExtendClientAreaChromeHints.NoChrome
            : ExtendClientAreaChromeHints.Default;
        ApplyWindowsFrame(window, extended);
    }

    private static void ApplyWindowsFrame(Window window, bool extended)
    {
        if (!OperatingSystem.IsWindows() || !window.IsVisible
            || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle) return;
        // Avalonia's WinUI composition path can use zero DWM margins. Preserve a native shadow edge.
        var margins = new Margins { Left = extended ? 1 : 0, Right = extended ? 1 : 0,
            Top = extended ? 1 : 0, Bottom = extended ? 1 : 0 };
        _ = DwmExtendFrameIntoClientArea(handle.Handle, ref margins);
        // Windows 10 has no native rounded-corner API. Maximized/fullscreen/snap behavior belongs to DWM.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            const int windowCornerPreference = 33, round = 2, systemDefault = 0;
            var preference = window.WindowState == WindowState.Normal ? round : systemDefault;
            _ = DwmSetWindowAttribute(handle.Handle, windowCornerPreference, ref preference, sizeof(int));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
