using System.Diagnostics;
using System.Runtime.InteropServices;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class StartupError
{
    // This path must work even when Avalonia failed before creating a window.
    internal static void Show(Exception error)
    {
        var title = AppLanguage.IsChinese ? "无法启动 AvaMedia" : "AvaMedia could not start";
        var message = AppLanguage.IsChinese
            ? $"{error.Message}\n\n错误详情已记录到用户目录的 AvaMedia/logs/exceptions.log。"
            : $"{error.Message}\n\nDetails were recorded in AvaMedia/logs/exceptions.log in your application data directory.";
        try
        {
            if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, message, title, 0x10);
            else if (OperatingSystem.IsMacOS())
            {
                var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add("on run argv\ndisplay alert (item 1 of argv) message (item 2 of argv) as critical\nend run");
                start.ArgumentList.Add(title); start.ArgumentList.Add(message);
                using var dialog = Process.Start(start);
                dialog?.WaitForExit();
            }
            else Console.Error.WriteLine($"{title}\n{message}");
        }
        catch (Exception dialogError) { AppDiagnostics.Record("Native startup error notice", dialogError); }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string title, uint type);
}
