using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace AvaMedia.Controls;

/// <summary>Clips the AppKit popup background together with its native blur.</summary>
internal sealed class MacPopupMaterial : IDisposable
{
    private readonly IntPtr _window;
    private readonly IntPtr _content;
    private readonly IntPtr _background;
    private IntPtr _effect, _layer;
    private double _radius;
    private byte _clips;
    private NativeRect _frame;
    private Rect _bounds;
    private double _appliedRadius;
    private bool _applied;
    private bool _disposed;

    private MacPopupMaterial(IntPtr window, IntPtr content)
    {
        _window = Native.Send(window, Native.Retain);
        _content = Native.Send(content, Native.Retain);
        _background = Native.Send(Native.Send(window, Native.BackgroundColor), Native.Retain);
    }

    public static MacPopupMaterial? TryCreate(PopupRoot popup)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var handle = popup.TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "NSWindow" } || handle.Handle == IntPtr.Zero) return null;
        var content = Native.Send(handle.Handle, Native.ContentView);
        return content == IntPtr.Zero ? null : new MacPopupMaterial(handle.Handle, content);
    }

    public bool Apply(Rect bounds, double radius)
    {
        // Avalonia 11.3.22 sets NSWindow.windowBackgroundColor for AcrylicBlur.
        // Its NSVisualEffectView fills the content view, outside the XAML border.
        if (_effect == IntPtr.Zero)
        {
            var views = Native.Send(_content, Native.Subviews);
            var count = Native.ReadCount(views, Native.Count);
            for (nuint i = 0; i < count; i++)
            {
                var view = Native.ReadItem(views, Native.ObjectAtIndex, i);
                if (Native.IsKindOfClass(view, Native.IsKind, Native.EffectView) == 0
                    || Native.ReadBool(view, Native.IsHidden) != 0) continue;
                var layer = Native.Send(view, Native.Layer);
                if (layer == IntPtr.Zero) continue;
                _effect = Native.Send(view, Native.Retain); _layer = Native.Send(layer, Native.Retain);
                _frame = Native.Rect(view, Native.Frame);
                _radius = Native.ReadDouble(layer, Native.CornerRadius);
                _clips = Native.ReadBool(layer, Native.MasksToBounds);
                break;
            }
            if (_effect == IntPtr.Zero) return false;
        }
        Native.SetPointer(_window, Native.SetBackgroundColor, Native.ClearColor);
        var host = Native.Rect(_content, Native.Bounds);
        var frame = new NativeRect { X = bounds.X, Y = Native.ReadBool(_content, Native.IsFlipped) != 0
            ? bounds.Y : host.Height - bounds.Bottom, Width = bounds.Width, Height = bounds.Height };
        if (_applied && _bounds == bounds && _appliedRadius == radius && Native.Rect(_effect, Native.Frame).Equals(frame)) return true;
        Native.Send(Native.Transaction, Native.Begin);
        Native.SetBool(Native.Transaction, Native.SetDisableActions, 1);
        Native.SetFrame(_effect, Native.SetFrameSelector, frame);
        Native.SetDouble(_layer, Native.SetCornerRadius, radius);
        Native.SetBool(_layer, Native.SetMasksToBounds, 1);
        Native.Send(Native.Transaction, Native.Commit);
        Native.Send(_window, Native.InvalidateShadow);
        _applied = true; _bounds = bounds; _appliedRadius = radius;
        return true;
    }

    public void Restore()
    {
        if (!_applied) return;
        Native.SetPointer(_window, Native.SetBackgroundColor, _background);
        Native.Send(Native.Transaction, Native.Begin);
        Native.SetBool(Native.Transaction, Native.SetDisableActions, 1);
        Native.SetFrame(_effect, Native.SetFrameSelector, _frame);
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
        Native.Send(_effect, Native.Release);
        Native.Send(_content, Native.Release);
        Native.Send(_window, Native.Release);
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public double X, Y, Width, Height; }

    // Keep Objective-C initialization behind the macOS platform check.
    private static class Native
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        internal static readonly IntPtr Retain = Selector("retain");
        internal static readonly IntPtr Release = Selector("release");
        internal static readonly IntPtr ContentView = Selector("contentView");
        internal static readonly IntPtr Layer = Selector("layer");
        internal static readonly IntPtr Subviews = Selector("subviews");
        internal static readonly IntPtr Count = Selector("count");
        internal static readonly IntPtr ObjectAtIndex = Selector("objectAtIndex:");
        internal static readonly IntPtr IsKind = Selector("isKindOfClass:");
        internal static readonly IntPtr IsHidden = Selector("isHidden");
        internal static readonly IntPtr IsFlipped = Selector("isFlipped");
        internal static readonly IntPtr Frame = Selector("frame");
        internal static readonly IntPtr Bounds = Selector("bounds");
        internal static readonly IntPtr SetFrameSelector = Selector("setFrame:");
        internal static readonly IntPtr EffectView = Class("NSVisualEffectView");
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

        internal static NativeRect Rect(IntPtr view, IntPtr selector)
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            { ReadRectX64(out var rect, view, selector); return rect; }
            return ReadRect(view, selector);
        }

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
        internal static extern nuint ReadCount(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern IntPtr ReadItem(IntPtr receiver, IntPtr selector, nuint index);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern byte IsKindOfClass(IntPtr receiver, IntPtr selector, IntPtr type);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern NativeRect ReadRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
        private static extern void ReadRectX64(out NativeRect rect, IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetFrame(IntPtr receiver, IntPtr selector, NativeRect value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetPointer(IntPtr receiver, IntPtr selector, IntPtr value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetDouble(IntPtr receiver, IntPtr selector, double value);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SetBool(IntPtr receiver, IntPtr selector, byte value);
    }
}
