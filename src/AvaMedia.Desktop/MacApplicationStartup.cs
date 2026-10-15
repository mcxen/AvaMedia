using System.Runtime.InteropServices;

namespace AvaMedia.Desktop;

internal static class MacApplicationStartup
{
    // AppKit delivers the initial open-document Apple event during finishLaunching.
    // Resolve it before deciding whether a converter window is needed.
    internal static void FinishLaunching()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var running = Send(GetClass("NSRunningApplication"), Selector("currentApplication"));
        if (SendBool(running, Selector("isFinishedLaunching"))) return;
        SendVoid(Send(GetClass("NSApplication"), Selector("sharedApplication")), Selector("finishLaunching"));
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "objc_getClass")] private static extern IntPtr GetClass(string name);
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(IntPtr target, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr target, IntPtr selector);
}
