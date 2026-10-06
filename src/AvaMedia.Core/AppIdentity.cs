using System.Reflection;

namespace AvaMedia.Core;

public static class AppIdentity
{
    private static string Metadata(string key) => typeof(AppIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value!;

    public static string ChineseName { get; } = Metadata("ProductChineseName");
    public static string EnglishName { get; } = Metadata("ProductEnglishName");
    public static string Name => AppLanguage.IsChinese ? ChineseName : EnglishName;
    public static string DisplayName => AppLanguage.IsChinese ? ChineseName + " · " + EnglishName : EnglishName;
    public static string Version => typeof(AppIdentity).Assembly.GetName().Version!.ToString(3);
    public static string WindowTitle => DisplayName + " " + Version;
    public static string PlayerTitle => AppLanguage.IsChinese ? ChineseName + " · 播放器" : EnglishName + " Player";
    public static string PlayerWelcome => Name + (AppLanguage.IsChinese ? "\n拖入媒体文件，或按 F3 打开" : "\nDrop media files here, or press F3 to open");
    public static string ConvertMenuLabel => AppLanguage.IsChinese ? "使用" + ChineseName : "Convert with " + EnglishName;
}
