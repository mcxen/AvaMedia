using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <param name="Formats">Format targets render as a compact "转换为" chip group instead of tool rows.</param>
internal sealed record FeatureSection(string Title, bool Formats, Feature[] Features);

/// <summary>
/// Sidebar tool tree. The disc category is shown as a group inside 工具集; background tasks stay hidden.
/// Catalog categories are unchanged so routing and task storage keep their identifiers.
/// </summary>
internal static class FeatureNavigation
{
    public const string DiscCategory = "光驱设备\\DVD\\CD\\ISO";
    // Private desktop action; intentionally absent from the public Core/MCP catalog.
    public static readonly Feature ExclusiveRenamer = new("exclusive-vl-renamer", "VL 媒体智能重命名", "视频", "", "edit", Operation.BatchTools);
    public static string[] Categories { get; } = ["视频", "音频", "图片", "文档", "工具集"];

    public static string CategoryTitle(string category) => category == DiscCategory ? "光盘" : category;

    /// <summary>Tree category that hosts a catalog category.</summary>
    public static string TreeCategory(string category) => category == DiscCategory ? "工具集" : category;

    public static string CategoryIcon(string category) => category switch
    {
        "视频" => "video", "音频" => "audio-lines", "图片" => "photo", "文档" => "file", _ => "settings"
    };

    /// <summary>Same-named tools in different categories get a qualified sidebar label.</summary>
    public static string Label(Feature feature) => feature.Id switch
    {
        "voice-enhance" => "视频人声增强",
        "audio-enhance" => "音频人声增强",
        "person-clip" => "保留有人片段",
        _ => feature.Label
    };

    public static string Icon(Feature feature) => feature.Id switch
    {
        "player" => "circle-play", "download" => "download", "info" => "info",
        "mp4" => "arrow-left-right", "video-compress" => "shrink", "video-slim" => "arrow-down-to-line", "repair" => "wrench",
        "clip" or "audio-clip" => "scissors", "person-clip" => "user-round", "delogo" => "eraser",
        "split" => "music", "extract-video" => "film", "join" or "audio-join" => "combine", "mux" or "audio-mix" => "layers",
        "auto-subtitle" => "captions", "video-summary" => "sparkles", "voice-enhance" or "audio-enhance" => "mic",
        "image-viewer" => "photo", "image-compress" => "shrink", "image-tools" or "crop" => "crop", "images-pdf" => "file-image",
        "pdf-merge" => "files", "pdf-split" => "split", "pdf-age" => "file-clock", "pdf-compress" => "file-archive",
        "pdf-text" => "file-text", "pdf-docx" => "file-type", "pdf-xlsx" => "file-spreadsheet", "text-pdf" => "file-output",
        "rotate" => "rotate-right", "batch-rename" or "exclusive-vl-renamer" => "edit", "media-ai" => "tag", "folder-classification" => "folder",
        "contact-sheet" => "layout-grid", "zip" => "file-archive", "unzip" => "archive", "dvd" or "iso" => "disc",
        _ => "file"
    };

    public static IEnumerable<FeatureSection> Sections(string category, bool enableBeta, bool exclusiveUnlocked = false)
    {
        var catalog = exclusiveUnlocked ? Catalog.All.Append(ExclusiveRenamer) : Catalog.All;
        var features = catalog.Where(feature => TreeCategory(feature.Category) == category
                && (enableBeta || !Catalog.IsBeta(feature)))
            .GroupBy(SectionTitle).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var (title, order) in SectionOrder(category))
            if (features.TryGetValue(title, out var items))
                yield return new(title, title == "转换为", order.Length == 0 ? items
                    : items.OrderBy(item => Array.IndexOf(order, item.Id) is var index and >= 0 ? index : order.Length).ToArray());
    }

    public static int Count(string category, bool enableBeta, bool exclusiveUnlocked = false) => Sections(category, enableBeta, exclusiveUnlocked).Sum(section => section.Features.Length);

    private static (string Title, string[] Order)[] SectionOrder(string category) => category switch
    {
        "视频" =>
        [
            ("播放", ["player", "download", "info"]),
            ("格式与体积", ["mp4", "video-compress", "video-slim", "repair"]),
            ("剪辑", ["clip", "person-clip", "delogo"]),
            ("音视频轨道", ["split", "extract-video", "join", "mux"]),
            ("字幕与内容", ["auto-subtitle", "video-summary", "voice-enhance"]),
            ("内部工具", [ExclusiveRenamer.Id])
        ],
        "音频" => [("音频", []), ("转换为", [])],
        "图片" => [("图片", []), ("转换为", [])],
        "文档" => [("文档", []), ("格式转换", [])],
        _ => [("媒体整理", []), ("批量画面", []), ("压缩与解压", []), ("光盘", [])]
    };

    private static string SectionTitle(Feature feature) => feature.Category switch
    {
        "视频" => feature.Id switch
        {
            "exclusive-vl-renamer" => "内部工具",
            "player" or "download" or "info" => "播放",
            "mp4" or "video-compress" or "video-slim" or "repair" => "格式与体积",
            "clip" or "person-clip" or "delogo" => "剪辑",
            "split" or "extract-video" or "join" or "mux" => "音视频轨道",
            _ => "字幕与内容"
        },
        "音频" => feature.Id == "audio-" + feature.Format ? "转换为" : "音频",
        "图片" => feature.Id == "image-" + feature.Format ? "转换为" : "图片",
        "文档" => feature.Operation is Operation.PdfMerge or Operation.PdfSplit or Operation.PdfAge or Operation.PdfCompress
            ? "文档" : "格式转换",
        "工具集" => feature.Id switch
        {
            "zip" or "unzip" => "压缩与解压",
            "crop" or "rotate" or "contact-sheet" => "批量画面",
            _ => "媒体整理"
        },
        DiscCategory => "光盘",
        _ => "后台任务"
    };
}
