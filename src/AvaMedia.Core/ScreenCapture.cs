using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public static class ScreenCapture
{
    public static IReadOnlyList<(int Index,string Name)> MacScreens(string deviceLog)
    {
        var screens=new List<(int,string)>();var video=false;
        foreach(var line in deviceLog.Split('\n'))
        {
            if(line.Contains("AVFoundation video devices:",StringComparison.Ordinal)){video=true;continue;}
            if(line.Contains("AVFoundation audio devices:",StringComparison.Ordinal)){video=false;continue;}
            if(!video)continue;
            var match=Regex.Match(line,@"\[(\d+)\]\s+(Capture screen\s+\d+)\s*$");
            if(match.Success)screens.Add((int.Parse(match.Groups[1].Value),match.Groups[2].Value));
        }
        return screens;
    }
    public static List<string> InputArguments(ConversionOptions o,bool macOS)
    {
        if(!macOS)return ["-f","gdigrab","-framerate",MediaEngine.Number(o.Fps>0?o.Fps:25),"-i",o.RecordSource,"-t",MediaEngine.Number(o.RecordSeconds)];
        if(!Regex.IsMatch(o.RecordSource,@"^\d+:none$"))throw new ArgumentException("macOS 录屏需选定 AVFoundation 屏幕设备索引。");
        return ["-f","avfoundation","-framerate",MediaEngine.Number(o.Fps>0?o.Fps:25),"-pixel_format","bgra","-capture_cursor","1","-i",o.RecordSource,"-t",MediaEngine.Number(o.RecordSeconds)];
    }
    public static string MacPermissionMessage(string error) => "macOS 屏幕采集失败。请检查“系统设置 → 隐私与安全性 → 屏幕与系统音频录制”中的"+AppIdentity.ChineseName+"或所配置 FFmpeg 进程权限，授权后重启应用。\n\n"+error;
}
