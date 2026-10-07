using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public static class SystemPlayerIntegration
{
    private const string ProgId = "AvaMedia.Player.Media";
    private const string Capabilities = @"Software\AvaMedia\Player\Capabilities";
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "AvaMedia.Desktop.exe");
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(VideoFormats.InputExtensions
        .Order(StringComparer.Ordinal).Select(extension => "." + extension).Concat(new[]
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".aiff", ".ac3", ".wma"
    }).ToArray());
    public static string OpenCommand(string executable) => "\"" + Path.GetFullPath(executable) + "\" --play \"%1\"";

    public static void RegisterWindows(string executable, RegistryKey? root = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!File.Exists(executable)) throw new FileNotFoundException("播放器程序不存在。", executable);
        root ??= Registry.CurrentUser;
        var fullPath = Path.GetFullPath(executable);
        var application = @"Software\Classes\Applications\" + Path.GetFileName(fullPath);
        Set(application, "FriendlyAppName", AppIdentity.PlayerChineseName);
        Set(application + @"\DefaultIcon", "", fullPath + ",0");
        Set(application + @"\shell\open\command", "", OpenCommand(fullPath));
        Set(@"Software\Classes\" + ProgId, "", AppIdentity.PlayerChineseName);
        Set(@"Software\Classes\" + ProgId + @"\DefaultIcon", "", fullPath + ",0");
        Set(@"Software\Classes\" + ProgId + @"\shell\open\command", "", OpenCommand(fullPath));
        Set(Capabilities, "ApplicationName", AppIdentity.PlayerChineseName);
        Set(Capabilities, "ApplicationDescription", "Tianchi Player · AvaMedia");
        Set(Capabilities, "ApplicationIcon", fullPath + ",0");
        foreach (var extension in Extensions)
        {
            Set(application + @"\SupportedTypes", extension, "");
            Set(@"Software\Classes\" + extension + @"\OpenWithProgids", ProgId, "");
            Set(Capabilities + @"\FileAssociations", extension, ProgId);
        }
        Set(@"Software\RegisteredApplications", "AvaMedia.Player", Capabilities);
        if (ReferenceEquals(root, Registry.CurrentUser)) NotifyShell();

        void Set(string path, string name, string value)
        { using var key = root.CreateSubKey(path); key.SetValue(name, value); }
    }
    public static void UnregisterWindows(string executable, RegistryKey? root = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        root ??= Registry.CurrentUser;
        foreach (var extension in Extensions)
        {
            using var key = root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true);
            key?.DeleteValue(ProgId, false);
        }
        using (var registered = root.OpenSubKey(@"Software\RegisteredApplications", true)) registered?.DeleteValue("AvaMedia.Player", false);
        root.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        root.DeleteSubKeyTree(@"Software\Classes\Applications\" + Path.GetFileName(executable), false);
        root.DeleteSubKeyTree(@"Software\AvaMedia\Player", false);
        if (ReferenceEquals(root, Registry.CurrentUser)) NotifyShell();
    }
    public static string MacApplicationBundlePath
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            var bundle = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent;
            if (bundle is null || !bundle.Name.EndsWith(".app", StringComparison.Ordinal))
                throw new InvalidOperationException("请安装 macOS 应用后注册播放器。");
            if (!File.Exists(Path.Combine(bundle.FullName, "Contents", "Info.plist")))
                throw new FileNotFoundException("macOS 应用信息不存在。", bundle.FullName);
            return bundle.FullName;
        }
    }
    public static async Task RegisterMacAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var start = new ProcessStartInfo("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister")
        { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("-f"); start.ArgumentList.Add(MacApplicationBundlePath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法注册播放器。");
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        await error;
    }
    public static void RevealMacPlayer()
    {
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add("-R"); start.ArgumentList.Add(MacApplicationBundlePath);
        Process.Start(start)?.Dispose();
    }
    public static void OpenDefaultApps()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }
    private static void NotifyShell() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr first, IntPtr second);
}
