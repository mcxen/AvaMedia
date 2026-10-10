using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal sealed record FeatureSection(string Title, bool Compact, Feature[] Features);

internal static class FeatureNavigation
{
    public static string CategoryTitle(string category) => category == "光驱设备\\DVD\\CD\\ISO" ? "光盘" : category;

    public static string CategoryIcon(string category) => category switch
    {
        "视频" => "video", "音频" => "audio", "图片" => "image", "文档" => "document",
        "工具集" => "gear", _ => "disc"
    };

    public static IEnumerable<FeatureSection> Sections(string category, bool enableBeta)
    {
        var features = Catalog.All.Where(feature => feature.Category == category && (enableBeta || !Catalog.IsBeta(feature)))
            .GroupBy(SectionTitle).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var title in SectionOrder(category))
            if (features.TryGetValue(title, out var items))
                yield return new(title, title == "格式转换" && category is ("音频" or "图片"), items);
    }

    private static string[] SectionOrder(string category) => category switch
    {
        "视频" => ["播放与下载", "转换与体积", "剪辑与画面", "提取与截图", "字幕与分析"],
        "音频" => ["音频编辑", "格式转换"],
        "图片" => ["图片处理", "格式转换"],
        "文档" => ["PDF 处理", "格式转换"],
        "工具集" => ["媒体整理", "批量画面", "压缩与解压"],
        _ => ["光盘"]
    };

    private static string SectionTitle(Feature feature) => feature.Category switch
    {
        "视频" => feature.Id switch
        {
            "player" or "download" or "info" => "播放与下载",
            "mp4" or "video-compress" or "video-slim" or "repair" => "转换与体积",
            "clip" or "person-clip" or "join" or "mux" or "delogo" => "剪辑与画面",
            "split" or "extract-video" or "frames" => "提取与截图",
            _ => "字幕与分析"
        },
        "音频" => feature.Id == "audio-" + feature.Format ? "格式转换" : "音频编辑",
        "图片" => feature.Id == "image-" + feature.Format ? "格式转换" : "图片处理",
        "文档" => feature.Operation is Operation.PdfMerge or Operation.PdfSplit or Operation.PdfAge or Operation.PdfCompress
            ? "PDF 处理" : "格式转换",
        "工具集" => feature.Id switch
        {
            "zip" or "unzip" => "压缩与解压",
            "crop" or "rotate" or "contact-sheet" => "批量画面",
            _ => "媒体整理"
        },
        _ => "光盘"
    };
}
