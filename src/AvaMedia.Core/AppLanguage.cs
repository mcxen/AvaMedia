using System.Globalization;

namespace AvaMedia.Core;

public static class AppLanguage
{
    private static readonly string SystemLanguage = CultureInfo.CurrentUICulture.Name;
    public static string Current { get; private set; } = Resolve("system");
    public static bool IsChinese => Current == "zh-CN";

    public static string Resolve(string preference) => preference switch
    {
        "zh-CN" => "zh-CN",
        "en-US" => "en-US",
        _ => SystemLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US"
    };

    public static void Apply(string preference) => Current = Resolve(preference);
}
