using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public sealed record VlMediaFeatures(string Prefix, string Scene, string View, string Attire, string Action, string Quality, string Identifier)
{
    public bool QualityIsExplicit { get; init; }
    public string FileName(string extension) => VlMediaNaming.FileName(this, extension);
}

/// <summary>The six semantic fields plus the original identifier; model output never chooses paths or identifiers.</summary>
public static class VlMediaNaming
{
    private static readonly string[] Noise = ["卧槽绝了", "确实好看", "极品美女", "质量不错", "极品", "绝了", "卧槽", "加微信", "关注公众号", "扫码购买"];
    private static readonly (string Name, string[] Aliases)[] Scenes =
    [
        ("卫生间", ["卫生间", "洗手间", "厕所", "厕拍", "toilet", "restroom"]), ("试衣间", ["试衣间", "更衣室", "fitting room"]),
        ("地铁", ["地铁", "subway", "metro"]), ("扶梯", ["扶梯", "escalator"]), ("车展", ["车展", "auto show"]),
        ("酒店", ["酒店", "hotel"]), ("酒吧", ["酒吧", "bar"]), ("街头", ["街头", "街拍", "street"]), ("室内", ["室内", "indoor"])
    ];
    private static readonly (string Name, string[] Aliases)[] Views =
    [
        ("超低抄底", ["超低抄底", "超低机位"]), ("高抄俯视", ["高抄俯视", "高抄", "俯视", "俯拍", "high angle"]),
        ("隐蔽机位", ["隐蔽机位", "隐藏机位"]), ("仰拍", ["仰拍", "仰视", "抄底", "low angle"]),
        ("特写", ["特写", "close-up"]), ("侧拍", ["侧拍", "侧面", "side view"])
    ];
    private static readonly (string Name, string[] Aliases)[] Attires =
    [
        ("JK短裙", ["JK短裙", "JK", "水手服"]), ("OL制服", ["OL制服", "OL", "职业装"]),
        ("包臀裙", ["包臀裙", "包臀"]), ("百褶裙", ["百褶裙", "pleated skirt"]),
        ("黑丝", ["黑丝", "黑色丝袜", "black stockings"]), ("短裙长腿", ["短裙长腿"]), ("短裙", ["短裙", "miniskirt"])
    ];
    private static readonly (string Name, string[] Aliases)[] Actions =
    [
        ("站立行走", ["站立行走", "行走", "走路", "walking"]), ("如厕", ["如厕"]), ("换衣", ["换衣", "更衣", "changing clothes"]),
        ("后入体位", ["后入体位", "后入"]), ("坐姿", ["坐姿", "坐着", "sitting"]), ("站立", ["站立", "standing"])
    ];
    private static readonly (string Name, string[] Aliases)[] Prefixes =
    [
        ("GCP", ["GCP"]), ("GC", ["GC", "高抄", "俯视", "俯拍"]), ("CPS", ["CPS"]), ("CP", ["CP", "厕拍"]),
        ("TPF", ["TPF"]), ("TP", ["TP", "偷拍"]), ("CD", ["CD", "抄底", "仰视", "仰拍"]),
        ("JD", ["JD", "酒店"]), ("FC", ["FC", "Fancy", "合集"])
    ];

    public static VlMediaFeatures FromPath(string path, string? folderContext = null)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var parts = stem.Split('_');
        if (parts.Length == 7 && parts.All(part => part.Length > 0) && IsQuality(parts[5].ToUpperInvariant()) && IsPrefix(parts[0].ToUpperInvariant()))
            return new(parts[0].ToUpperInvariant(), Field(parts[1], "未知场景"), Field(parts[2], "未知视角"), Field(parts[3], "未知装扮"),
                Field(parts[4], "未知动作"), parts[5].ToUpperInvariant(), Field(parts[6], Hash(path), int.MaxValue)) { QualityIsExplicit = true };
        // File-specific labels take precedence over the enclosing folder's labels.
        var contexts = new[] { Clean(stem), Clean(folderContext ?? Path.GetFileName(Path.GetDirectoryName(path)) ?? "") };
        // A source ID such as BC-AA-539 must not mask a recognized code in the folder.
        var prefix = contexts.Select(Prefix).FirstOrDefault(value => value.Length > 0)
            ?? contexts.Select(OriginalPrefix).FirstOrDefault(value => value.Length > 0) ?? "MEDIA";
        var view = prefix switch { "CD" => "仰拍", "GC" or "GCP" => "高抄俯视", _ => "未知视角" };
        var scene = prefix switch { "CP" or "CPS" => "卫生间", "JD" => "酒店", _ => "未知场景" };
        var quality = Quality(contexts);
        return new(prefix, Match(contexts, Scenes, scene), Match(contexts, Views, view),
            Match(contexts, Attires, "未知装扮"), Match(contexts, Actions, "未知动作"), quality ?? "SSR", Identifier(stem, path))
            { QualityIsExplicit = quality is not null };
    }

    public static VlMediaFeatures WithVisual(VlMediaFeatures source, string scene, string view, string attire, string action, string quality)
        => source with
        {
            Scene = Visual(scene, source.Scene, Scenes), View = Visual(view, source.View, Views),
            Attire = Visual(attire, source.Attire, Attires), Action = Visual(action, source.Action, Actions),
            // Collection grades are source labels, not something a model can infer from pixels.
            Quality = !source.QualityIsExplicit && Clean(quality).Trim() == "低画质" ? "低画质" : source.Quality
        };

    public static string FileName(VlMediaFeatures features, string extension)
    {
        var parts = new[] { features.Prefix, features.Scene, features.View, features.Attire, features.Action, features.Quality, features.Identifier };
        string Name() => string.Join('_', parts) + extension;
        // Keep the original identifier intact while fitting both Windows and UTF-8 filesystem limits.
        while (Name().Length > 220 || Encoding.UTF8.GetByteCount(Name()) > 255)
        {
            var longest = Enumerable.Range(1, 4).Where(index => parts[index].EnumerateRunes().Count() > 1)
                .OrderByDescending(index => Encoding.UTF8.GetByteCount(parts[index])).FirstOrDefault(-1);
            if (longest < 0) throw new ArgumentException("原标识过长，无法生成有效的七段文件名。");
            var runes = parts[longest].EnumerateRunes().ToArray();
            parts[longest] = string.Concat(runes[..^1].Select(rune => rune.ToString()));
        }
        return Name();
    }

    public static string WithConflictNumber(string name, string identifier, int number)
    {
        var parts = Path.GetFileNameWithoutExtension(name).Split('_');
        if (parts.Length != 7 || number < 2) throw new ArgumentException("冲突编号或七段文件名无效。");
        return FileName(new(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], identifier + "-" + number), Path.GetExtension(name));
    }

    public static void ValidateFileName(string source, string name, string identifier)
    {
        BatchRename.ValidateRenameFileName(name);
        if (Path.GetFileName(name) != name || Path.GetExtension(name) != Path.GetExtension(source))
            throw new ArgumentException("只可修改文件名，须保留原扩展名。");
        var parts = Path.GetFileNameWithoutExtension(name).Split('_');
        if (parts.Length != 7 || parts.Any(string.IsNullOrWhiteSpace) || !IsPrefix(parts[0]) || !IsQuality(parts[5]))
            throw new ArgumentException("名称须包含七段：前缀_场景_视角_装扮_动作_评级_原标识。");
        if (parts[6] != identifier && !Regex.IsMatch(parts[6], "^" + Regex.Escape(identifier) + @"-\d+$"))
            throw new ArgumentException("末段须保留原标识，可追加数字以避免重名。");
        if (parts.Any(part => Clean(part) != part)) throw new ArgumentException("名称包含须过滤的杂词。");
    }

    private static string Prefix(string text)
    {
        foreach (var (name, aliases) in Prefixes)
            foreach (var alias in aliases.Where(alias => alias.All(char.IsAsciiLetter)))
                if (Regex.IsMatch(text, @"(?<![A-Za-z])" + alias + @"(?![A-Za-z])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return name;
        foreach (var (name, aliases) in Prefixes)
            if (aliases.Where(alias => !alias.All(char.IsAsciiLetter)).Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase))) return name;
        return "";
    }
    private static string OriginalPrefix(string text)
    {
        var original = Regex.Match(text, @"^([A-Za-z]{2,12})(?=$|[_\-\s\d\p{IsCJKUnifiedIdeographs}])");
        return original.Success ? original.Groups[1].Value.ToUpperInvariant() : "";
    }
    private static string Match(IEnumerable<string> contexts, (string Name, string[] Aliases)[] rules, string fallback)
    {
        foreach (var text in contexts)
            foreach (var (name, aliases) in rules)
                if (aliases.Any(alias => alias.All(char.IsAsciiLetter)
                    ? Regex.IsMatch(text, @"(?<![A-Za-z])" + Regex.Escape(alias) + @"(?![A-Za-z])", RegexOptions.IgnoreCase)
                    : text.Contains(alias, StringComparison.OrdinalIgnoreCase))) return name;
        return fallback;
    }
    private static string Visual(string value, string fallback, (string Name, string[] Aliases)[] rules)
    {
        value = Clean(value).Trim();
        if (value.Length == 0 || value.Contains("未知", StringComparison.Ordinal) || value.Contains("不确定", StringComparison.Ordinal)
            || value.Equals("unknown", StringComparison.OrdinalIgnoreCase) || value is "无" or "未见" or "null") return fallback;
        return Field(Match([value], rules, value), fallback);
    }
    private static string? Quality(IEnumerable<string> contexts)
    {
        foreach (var text in contexts)
        {
            var match = Regex.Match(text, @"(?<![A-Za-z])(SSSR|SSR|SR)(?![A-Za-z])", RegexOptions.IgnoreCase);
            if (match.Success) return match.Value.ToUpperInvariant();
            if (text.Contains("低画质", StringComparison.Ordinal)) return "低画质";
        }
        return null;
    }
    private static string Identifier(string stem, string path)
    {
        var code = Regex.Match(stem, @"(?<![A-Za-z0-9])(?:[A-Za-z]{1,8}-){1,3}\d{1,12}(?![A-Za-z0-9])");
        if (code.Success) return code.Value;
        var tail = Regex.Match(stem, @"(?:^|[_\-\s(（])([A-Fa-f0-9]{8,64}|\d{1,16})[)）]?$", RegexOptions.CultureInvariant);
        if (tail.Success) return tail.Groups[1].Value;
        // Preserve a named identifier when it follows the structural labels, otherwise use a stable path hash.
        var residual = Clean(stem);
        foreach (var rules in new[] { Scenes, Views, Attires, Actions, Prefixes })
            foreach (var alias in rules.SelectMany(rule => rule.Aliases).OrderByDescending(value => value.Length))
                residual = Regex.Replace(residual, alias.All(char.IsAsciiLetter) ? @"(?<![A-Za-z])" + Regex.Escape(alias) + @"(?![A-Za-z])" : Regex.Escape(alias), "", RegexOptions.IgnoreCase);
        residual = Regex.Replace(residual, @"(?<![A-Za-z])(SSSR|SSR|SR)(?![A-Za-z])|低画质", "", RegexOptions.IgnoreCase);
        var label = Field(residual, "", 24).Trim('-');
        return label.Length > 0 ? label + "-" + Hash(path) : Hash(path);
    }
    private static bool IsPrefix(string value) => Regex.IsMatch(value, @"^[A-Z][A-Z0-9-]{1,11}$");
    private static bool IsQuality(string value) => value is "SSSR" or "SSR" or "SR" or "低画质";
    private static string Clean(string value)
    {
        foreach (var noise in Noise) value = value.Replace(noise, "", StringComparison.OrdinalIgnoreCase);
        return value;
    }
    private static string Field(string value, string fallback, int limit = 20)
    {
        value = Regex.Replace(Clean(value), @"[^\p{L}\p{N}\-]", "").Trim('-');
        if (value.EnumerateRunes().Count() > limit) value = string.Concat(value.EnumerateRunes().Take(limit).Select(rune => rune.ToString()));
        return value.Length == 0 ? fallback : value;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..10].ToLowerInvariant();
}
