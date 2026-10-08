using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;

namespace AvaMedia.Desktop;

public static class WebViewDocumentScript
{
    public static Task InstallAsync(IPlatformHandle? handle, string script)
    {
        if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
        {
            InstallApple(apple.WKWebView, script);
            return Task.CompletedTask;
        }
        if (OperatingSystem.IsWindows() && handle is IWindowsWebView2PlatformHandle windows)
            return InstallWindowsAsync(windows.CoreWebView2, script);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("macos")]
    private static void InstallApple(IntPtr webView, string script)
    {
        var configuration = Send(webView, Selector("configuration"));
        var controller = Send(configuration, Selector("userContentController"));
        var text = SendString(Send(objc_getClass("NSString"), Selector("alloc")), Selector("initWithUTF8String:"), script);
        var userScript = IntPtr.Zero;
        try
        {
            userScript = InitScript(Send(objc_getClass("WKUserScript"), Selector("alloc")),
                Selector("initWithSource:injectionTime:forMainFrameOnly:"), text, 0, 0);
            if (userScript == IntPtr.Zero) throw new InvalidOperationException("无法启动浏览器嗅探。");
            SendObject(controller, Selector("addUserScript:"), userScript);
        }
        finally
        {
            if (userScript != IntPtr.Zero) Send(userScript, Selector("release"));
            Send(text, Selector("release"));
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task InstallWindowsAsync(IntPtr webView, string script)
    {
        var callback = new ScriptCompleted();
        var pointer = Marshal.GetComInterfaceForObject(callback, typeof(IScriptCompleted));
        try
        {
            // ICoreWebView2's stable COM ABI: AddScriptToExecuteOnDocumentCreated is slot 27.
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(webView), 27 * IntPtr.Size);
            var add = Marshal.GetDelegateForFunctionPointer<AddScript>(method);
            Marshal.ThrowExceptionForHR(add(webView, script, pointer));
            await callback.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Marshal.Release(pointer); GC.KeepAlive(callback); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AddScript(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string script, IntPtr callback);

    [ComVisible(true), Guid("B99369F3-9B11-47B5-BC6F-8E7895FCEA17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IScriptCompleted
    {
        [PreserveSig] int Invoke(int errorCode, [MarshalAs(UnmanagedType.LPWStr)] string id);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ScriptCompleted : IScriptCompleted
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Invoke(int errorCode, string id)
        {
            if (errorCode < 0) Completion.TrySetException(new COMException("无法启动浏览器嗅探。", errorCode));
            else Completion.TrySetResult();
            return 0;
        }
    }

    private static IntPtr Selector(string name) => sel_registerName(name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void SendObject(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr InitScript(IntPtr receiver, IntPtr selector, IntPtr source, nint injectionTime, byte mainFrameOnly);
}
