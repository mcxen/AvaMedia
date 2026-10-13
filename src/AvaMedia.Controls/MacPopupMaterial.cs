using System.Runtime.InteropServices;
using Avalonia.Controls.Primitives;

namespace AvaMedia.Controls;

/// <summary>Clips the AppKit popup background together with its native blur.</summary>
internal sealed class MacPopupMaterial : IDisposable
{
    private readonly IntPtr _window;
    private readonly IntPtr _layer;
    private readonly IntPtr _background;
    private readonly double _radius;
    private readonly byte _clips;
    private bool _applied;
    private bool _disposed;

    private MacPopupMaterial(IntPtr window, IntPtr layer)
    {
        _window = Native.Send(window, Native.Retain);
        _layer = Native.Send(layer, Native.Retain);
        _background = Native.Send(Native.Send(window, Native.BackgroundColor), Native.Retain);
        _radius = Native.ReadDouble(layer, Native.CornerRadius);
        _clips = Native.ReadBool(layer, Native.MasksToBounds);
    }

    public static MacPopupMaterial? TryCreate(PopupRoot popup)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var handle = popup.TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "NSWindow" } || handle.Handle == IntPtr.Zero) return null;
        var content = Native.Send(handle.Handle, Native.ContentView);
        var layer = Native.Send(content, Native.Layer);
        return layer == IntPtr.Zero ? null : new MacPopupMaterial(handle.Handle, layer);
    }

    public void Apply(double radius)
    {
        // Avalonia 11.3.22 sets NSWindow.windowBackgroundColor for AcrylicBlur.
        // Its NSVisualEffectView fills the content view, outside the XAML border.
        Native.SetPointer(_window, Native.SetBackgroundColor, Native.ClearColor);
        Native.Send(Native.Transaction, Native.Begin);
        Native.SetBool(Native.Transaction, Native.SetDisableActions, 1);
        Native.SetDouble(_layer, Native.SetCornerRadius, radius);
        Native.SetBool(_layer, Native.SetMasksToBounds, 1);
        Native.Send(Native.Transaction, Native.Commit);
        Native.Send(_window, Native.InvalidateShadow);
        _applied = true;
    }

    public void Restore()
    {
        if (!_applied) return;
        Native.SetPointer(_window, Native.SetBackgroundColor, _background);
        Native.Send(Native.Transaction, Native.Begin);
        Native.SetBool(Native.Transaction, Native.SetDisableActions, 1);
        Native.SetDouble(_layer, Native.SetCornerRadius, _radius);
        Native.SetBool(_layer, Native.SetMasksToBounds, _clips);
        Native.Send(Native.Transaction, Native.Commit);
        Native.Send(_window, Native.InvalidateShadow);
        _applied = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        Native.Send(_background, Native.Release);
        Native.Send(_layer, Native.Release);
        Native.Send(_window, Native.Release);
        _disposed = true;
    }

    // Keep Objective-C initialization behind the macOS platform check.
    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal static readonly IntPtr Retain = Selector("retain");
        internal static readonly IntPtr Release = Selector("release");
        internal static readonly IntPtr ContentView = Selector("contentView");
        internal static readonly IntPtr Layer = Selector("layer");
        internal static readonly IntPtr BackgroundColor = Selector("backgroundColor");
        internal static readonly IntPtr SetBackgroundColor = Selector("setBackgroundColor:");
        internal static readonly IntPtr CornerRadius = Selector("cornerRadius");
        internal static readonly IntPtr SetCornerRadius = Selector("setCornerRadius:");
        internal static readonly IntPtr MasksToBounds = Selector("masksToBounds");
        internal static readonly IntPtr SetMasksToBounds = Selector("setMasksToBounds:");
        internal static readonly IntPtr InvalidateShadow = Selector("invalidateShadow");
        internal static readonly IntPtr Begin = Selector("begin");
        internal static readonly IntPtr Commit = Selector("commit");
        internal static readonly IntPtr SetDisableActions = Selector("setDisableActions:");
        internal static readonly IntPtr Transaction = Class("CATransaction");
        internal static readonly IntPtr ClearColor = Send(Class("NSColor"), Selector("clearColor"));

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        private static extern IntPtr Class(string name);
        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        private static extern IntPtr Selector(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern double ReadDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern byte ReadBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetPointer(IntPtr receiver, IntPtr selector, IntPtr value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetDouble(IntPtr receiver, IntPtr selector, double value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetBool(IntPtr receiver, IntPtr selector, byte value);
    }
}
