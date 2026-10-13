using System.Reflection;

namespace AvaMedia.Core;

public static class AppIdentity
{
    public const string PlayerChineseName = "天池播放器";
    public const string PlayerEnglishName = "Tianchi Player";

    private static string Metadata(string key) => typeof(AppIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value!;

    public static string ChineseName { get; } = Metadata("ProductChineseName");
    public static string EnglishName { get; } = Metadata("ProductEnglishName");
    public static string Name => AppLanguage.IsChinese ? ChineseName : EnglishName;
    public static string DisplayName => AppLanguage.IsChinese ? ChineseName + " · " + EnglishName : EnglishName;
    public static string Version => typeof(AppIdentity).Assembly.GetName().Version!.ToString(3);
    public static string WindowTitle => DisplayName + " " + Version;
    public static string PlayerTitle => AppLanguage.IsChinese ? PlayerChineseName : PlayerEnglishName;
    public static string PlayerWelcome => AppLanguage.IsChinese ? "打开视频或音频文件" : "Open video or audio files";
    public static string ConvertMenuLabel => AppLanguage.IsChinese ? "使用" + ChineseName : "Convert with " + EnglishName;
}
