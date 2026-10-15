using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AvaMedia.Desktop.Controls;

internal static partial class WindowChrome
{
    /// <summary>Explicit task-list activation after a native minimize/restore round trip.</summary>
    internal static void ActivateRestoredTask(Window window)
    {
        if (!window.IsVisible || window.WindowState == WindowState.Minimized) return;
        window.Activate();
        // X11 window managers can restore and raise a client while retaining keyboard focus on
        // the queue. This is an explicit user action, so finish that activation on the mapped XID.
        if (!OperatingSystem.IsLinux() || window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return;
        try
        {
            XRaiseWindow(display, handle.Handle);
            XSetInputFocus(display, handle.Handle, 2, 0);
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XRaiseWindow(IntPtr display, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(IntPtr display, IntPtr window, int revertTo, nuint time);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
