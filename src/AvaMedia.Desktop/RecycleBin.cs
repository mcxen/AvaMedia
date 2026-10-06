using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AvaMedia.Desktop;

public interface IRecycleBin
{
    Task MoveAsync(string path, CancellationToken token);
}

public sealed class RecycleBin : IRecycleBin
{
    private static readonly Lazy<IntPtr> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"));
    public Task MoveAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return MoveWindowsAsync(path, token);
        if (OperatingSystem.IsMacOS()) return MoveMacAsync(path, token);
        return Task.FromException(new PlatformNotSupportedException("当前平台尚未接入系统回收站。"));
    }

    [SupportedOSPlatform("windows")]
    private static Task MoveWindowsAsync(string path, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
                IntPtr item = IntPtr.Zero;
                try
                {
                    // Silent, no extra confirmation/error UI, abort on error, recycle rather than permanent deletion.
                    operation.SetOperationFlags(0x0004 | 0x0010 | 0x0400 | 0x00100000 | 0x00080000 | 0x20000000);
                    var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                    Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                    token.ThrowIfCancellationRequested();
                    operation.DeleteItem(item, IntPtr.Zero); operation.PerformOperations();
                    operation.GetAnyOperationsAborted(out var aborted);
                    if (aborted) throw new IOException("文件未移入回收站。");
                }
                finally { if (item != IntPtr.Zero) Marshal.Release(item); Marshal.ReleaseComObject(operation); }
                completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "AvaMedia Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("macos")]
    private static Task MoveMacAsync(string path, CancellationToken token)
        => Task.Run(() => { token.ThrowIfCancellationRequested(); MoveMac(path); }, token);

    [SupportedOSPlatform("macos")]
    private static void MoveMac(string path)
    {
        _ = Foundation.Value;
        var pool = Send(Send(objc_getClass("NSAutoreleasePool"), sel_registerName("alloc")), sel_registerName("init"));
        try
        {
            var text = SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), path);
            var url = SendObject(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), text);
            var manager = Send(objc_getClass("NSFileManager"), sel_registerName("defaultManager"));
            if (!Trash(manager, sel_registerName("trashItemAtURL:resultingItemURL:error:"), url, IntPtr.Zero, out var error))
            {
                var description = error == IntPtr.Zero ? IntPtr.Zero : Send(Send(error, sel_registerName("localizedDescription")), sel_registerName("UTF8String"));
                throw new IOException(Marshal.PtrToStringUTF8(description) ?? "文件未移入废纸篓。");
            }
        }
        finally { Send(pool, sel_registerName("drain")); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, out IntPtr item);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr SendObject(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool Trash(IntPtr receiver, IntPtr selector, IntPtr url, IntPtr result, out IntPtr error);

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner); void ApplyPropertiesToItem(IntPtr item); void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IntPtr item, IntPtr sink); void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
