using System.Diagnostics;

namespace AvaMedia.Desktop;

internal static class PlatformServices
{
    public static void RevealFile(string path)
    {
        if(OperatingSystem.IsMacOS())Process.Start(new ProcessStartInfo("/usr/bin/open"){ArgumentList={"-R",Path.GetFullPath(path)},UseShellExecute=false});
        else if(OperatingSystem.IsWindows())Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={"/select,",Path.GetFullPath(path)},UseShellExecute=true});
        else OpenFolder(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }
    public static void OpenFolder(string path)
    {
        if(OperatingSystem.IsMacOS())Process.Start(new ProcessStartInfo("/usr/bin/open"){ArgumentList={Path.GetFullPath(path)},UseShellExecute=false});
        else Process.Start(new ProcessStartInfo(Path.GetFullPath(path)){UseShellExecute=true});
    }
}
