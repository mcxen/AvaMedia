using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Controls;

internal static partial class WindowChrome
{
    private static readonly ConditionalWeakTable<Window, MacTitleLayout> MacTitleLayouts = new();

    internal static void HookMacTrafficLights(Window window, Func<Control> titleRegion, Func<double> leftInset)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var layout = MacTitleLayouts.GetValue(window, w => new MacTitleLayout(w));
        layout.TitleRegion = titleRegion;
        layout.LeftInset = leftInset;
        layout.Request();
    }

    private static void RequestMacTrafficLights(Window window)
    {
        if (OperatingSystem.IsMacOS() && MacTitleLayouts.TryGetValue(window, out var layout)) layout.Request();
    }

    private sealed class MacTitleLayout
    {
        private readonly Window _window;
        private bool _pending;
        private bool _closed;
        public Func<Control>? TitleRegion { get; set; }
        public Func<double>? LeftInset { get; set; }

        public MacTitleLayout(Window window)
        {
            _window = window;
            window.LayoutUpdated += Changed;
            window.Opened += Changed;
            window.Activated += Changed;
            window.ScalingChanged += Changed;
            window.ActualThemeVariantChanged += Changed;
            window.PropertyChanged += PropertyChanged;
            window.Closed += Closed;
        }

        private void Changed(object? sender, EventArgs args) => Request();
        private void PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Window.WindowStateProperty || args.Property == Window.TitleProperty)
                Request();
        }

        public void Request()
        {
            if (_pending || _closed) return;
            _pending = true;
            // AppKit lays out its title bar during resize/state changes. Apply after
            // that and Avalonia's layout, using the actual client title-row center.
            Dispatcher.UIThread.Post(() =>
            {
                _pending = false;
                if (_closed || !_window.IsVisible || !UsesClientTitleBar(_window)
                    || !_window.ExtendClientAreaToDecorationsHint
                    || _window.WindowState is WindowState.FullScreen or WindowState.Minimized) return;
                var title = TitleRegion?.Invoke();
                if (title is not { IsEffectivelyVisible: true } || title.Bounds.Height <= 0
                    || title.TranslatePoint(default, _window) is not { } origin) return;
                var left = LeftInset?.Invoke() ?? 16;
                var center = origin.Y + title.Bounds.Height / 2;
                if (double.IsFinite(left) && left >= 0 && double.IsFinite(center) && center > 0)
                    MacButtons.Align(_window, left, center);
            }, DispatcherPriority.Loaded);
        }

        private void Closed(object? sender, EventArgs args)
        {
            _closed = true;
            _window.LayoutUpdated -= Changed;
            _window.Opened -= Changed;
            _window.Activated -= Changed;
            _window.ScalingChanged -= Changed;
            _window.ActualThemeVariantChanged -= Changed;
            _window.PropertyChanged -= PropertyChanged;
            _window.Closed -= Closed;
            MacTitleLayouts.Remove(_window);
        }
    }

    // Keep AppKit's actual NSButtons in their system view hierarchy. Their sizes,
    // targets/actions, tracking and accessibility stay native.
    private static class MacButtons
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private static readonly IntPtr ContentView = Selector("contentView");
        private static readonly IntPtr WindowSelector = Selector("window");
        private static readonly IntPtr StandardButton = Selector("standardWindowButton:");
        private static readonly IntPtr Superview = Selector("superview");
        private static readonly IntPtr Frame = Selector("frame");
        private static readonly IntPtr Bounds = Selector("bounds");
        private static readonly IntPtr IsFlipped = Selector("isFlipped");
        private static readonly IntPtr ConvertPoint = Selector("convertPoint:fromView:");
        private static readonly IntPtr SetOrigin = Selector("setFrameOrigin:");
        private static readonly IntPtr SetFrame = Selector("setFrame:");
        private static readonly IntPtr Layout = Selector("layoutSubtreeIfNeeded");

        internal static void Align(Window window, double left, double center)
        {
            Dispatcher.UIThread.VerifyAccess();
            var handle = window.TryGetPlatformHandle();
            if (handle is null || handle.Handle == IntPtr.Zero) return;
            var nativeWindow = handle.HandleDescriptor switch
            {
                "NSWindow" => handle.Handle,
                "NSView" => Send(handle.Handle, WindowSelector),
                _ => IntPtr.Zero
            };
            if (nativeWindow == IntPtr.Zero) return;
            var content = Send(nativeWindow, ContentView);
            if (content == IntPtr.Zero) return;
            var root = Send(content, Superview);
            if (root == IntPtr.Zero) return;
            SendVoid(root, Layout);
            var close = SendButton(nativeWindow, StandardButton, 0);
            if (close == IntPtr.Zero) return;
            var first = ReadRect(close, Frame);
            var bounds = ReadRect(content, Bounds);
            if (first.Width <= 0 || first.Height <= 0 || bounds.Height <= 0) return;
            var y = bounds.Y + (SendBool(content, IsFlipped) != 0 ? center : bounds.Height - center);
            // NSWindow coordinates are points, just like Avalonia's logical layout.
            // Preserve native spacing by measuring offsets from the close button.
            Span<IntPtr> buttons = stackalloc IntPtr[3];
            Span<NativeRect> frames = stackalloc NativeRect[3];
            for (var i = 0; i < buttons.Length; i++)
            {
                buttons[i] = SendButton(nativeWindow, StandardButton, i);
                if (buttons[i] != IntPtr.Zero) frames[i] = ReadRect(buttons[i], Frame);
            }
            // A visible button outside a parent's bounds cannot receive mouse
            // events. Grow the native title hosts before moving the buttons.
            EnsureTitleHitArea(Send(close, Superview), root, content,
                new NativePoint(bounds.X + left + first.Width / 2, y), first.Height);
            for (var i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == IntPtr.Zero) continue;
                var parent = Send(buttons[i], Superview);
                if (parent == IntPtr.Zero) continue;
                var frame = frames[i];
                var point = SendPoint(parent, ConvertPoint,
                    new NativePoint(bounds.X + left + frame.X - first.X + frame.Width / 2, y), content);
                var origin = new NativePoint(point.X - frame.Width / 2, point.Y - frame.Height / 2);
                if (Math.Abs(origin.X - frame.X) > 0.01 || Math.Abs(origin.Y - frame.Y) > 0.01)
                    SendOrigin(buttons[i], SetOrigin, origin);
            }
        }

        private static void EnsureTitleHitArea(IntPtr parent, IntPtr root, IntPtr content,
            NativePoint center, double buttonHeight)
        {
            Span<IntPtr> hosts = stackalloc IntPtr[8];
            var count = 0;
            while (parent != IntPtr.Zero && parent != root && count < hosts.Length)
            {
                hosts[count++] = parent;
                parent = Send(parent, Superview);
            }
            if (parent != root) return;
            // Expand outside in, converting from the unchanged client coordinate
            // space each time. Keep the system hosts' width and top edge intact.
            for (var i = count - 1; i >= 0; i--)
            {
                var view = hosts[i];
                var owner = Send(view, Superview);
                var lower = SendPoint(owner, ConvertPoint, new NativePoint(center.X, center.Y - buttonHeight / 2), content);
                var upper = SendPoint(owner, ConvertPoint, new NativePoint(center.X, center.Y + buttonHeight / 2), content);
                var frame = ReadRect(view, Frame);
                var bottom = Math.Min(frame.Y, Math.Min(lower.Y, upper.Y));
                var top = Math.Max(frame.Y + frame.Height, Math.Max(lower.Y, upper.Y));
                if (bottom < frame.Y - 0.01 || top > frame.Y + frame.Height + 0.01)
                {
                    frame.Y = bottom;
                    frame.Height = top - bottom;
                    SendFrame(view, SetFrame, frame);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct NativePoint(double X, double Y);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public double X, Y, Width, Height; }

        private static NativeRect ReadRect(IntPtr view, IntPtr selector)
        {
            // x64 returns a 32-byte NSRect via stret; Apple Silicon uses objc_msgSend.
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            { SendRectX64(out var rect, view, selector); return rect; }
            return SendRect(view, selector);
        }

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern byte SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendButton(IntPtr receiver, IntPtr selector, nint kind);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern NativeRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
        private static extern void SendRectX64(out NativeRect rect, IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern NativePoint SendPoint(IntPtr receiver, IntPtr selector, NativePoint point, IntPtr view);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendOrigin(IntPtr receiver, IntPtr selector, NativePoint point);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendFrame(IntPtr receiver, IntPtr selector, NativeRect frame);
    }
}
