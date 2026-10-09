using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

public sealed record MediaTagTextLabel(string Label, string Category, double Score, string ScoreKind, string Model, string[] Tags);
public sealed record MediaTagPoint(double Seconds, double? Score);

public static class MediaTagTimeline
{
    public static MediaTagPoint[] Points(MediaTagResult result, string[] tags, string? semanticLabel = null)
    {
        if (semanticLabel is not null) return result.Scenes?.Frames.Select(frame => new MediaTagPoint(frame.Seconds,
            frame.Candidates.FirstOrDefault(item => item.Label == semanticLabel)?.Similarity)).ToArray() ?? [];
        var indices = result.Scores.Select((score, index) => (score.Tag, index)).ToDictionary(item => item.Tag, item => item.index, StringComparer.OrdinalIgnoreCase);
        return result.Frames.Select(frame =>
        {
            var values = tags.Select(tag => indices.TryGetValue(tag, out var index) && index < frame.Values.Length
                ? (double?)frame.Values[index] : frame.Scores.FirstOrDefault(score => score.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))?.Score).ToArray();
            return new MediaTagPoint(frame.Seconds, values.Length > 0 && values.All(value => value.HasValue) ? values.Min() : null);
        }).ToArray();
    }

}

/// <summary>Searchable sidecars own a header and source key. Only matching AvaMedia reports are replaced.</summary>
public static class MediaTagText
{
    private const string Header = "AvaMedia AI Tags / 2";
    private static readonly string[] OwnedHeaders = ["AvaMedia AI Tags / 1", "AvaMedia AI Tags / 2"];
    private static string Key(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source)))).ToLowerInvariant()[..12];
    private static string Safe(string value) => new(value.Where(character => !char.IsControl(character) && !"<>:\"/\\|?*".Contains(character)).ToArray());
    private static string Fit(string value, int bytes)
    {
        var text = new StringBuilder();
        foreach (var rune in value.EnumerateRunes()) { if (Encoding.UTF8.GetByteCount(text.ToString()) + rune.Utf8SequenceLength > bytes) break; text.Append(rune.ToString()); }
        return text.ToString().Trim(' ', '.');
    }
    public static string FileName(MediaTagResult result, IReadOnlyList<MediaTagTextLabel> labels)
    {
        var title = new List<string>();
        foreach (var label in labels.Select(label => Safe(label.Label)).Where(label => label.Length > 0).Distinct())
        {
            if (Encoding.UTF8.GetByteCount(string.Join('_', title.Append(label))) > 150) break;
            title.Add(label);
        }
        if (title.Count == 0) title.Add(labels.Count == 0 ? "未命中标签" : Fit(Safe(labels[0].Label), 150));
        var source = Fit(Safe(Path.GetFileNameWithoutExtension(result.Path)), 45);
        return string.Join('_', title) + "__" + source + "__" + Key(result.Path) + ".ai-tags.txt";
    }
    private static bool Owned(string path, string key)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var header = reader.ReadLine();
        return header is not null && OwnedHeaders.Contains(header) && reader.ReadLine() == "SourceKey: " + key;
    }
    public static async Task<string> SaveAsync(MediaTagResult result, IReadOnlyList<MediaTagTextLabel> labels,
        double threshold, double sceneThreshold, double sceneMargin, CancellationToken ct, string? previousSource = null)
    {
        MediaTagService.ValidateSource(result);
        var folder = Path.GetDirectoryName(result.Path)!; var key = Key(result.Path);
        string? old = Directory.EnumerateFiles(folder, "*" + key + ".ai-tags.txt").FirstOrDefault(path => Owned(path, key));
        if (old is null && previousSource is not null)
        {
            var previousKey = Key(previousSource);
            old = Directory.EnumerateFiles(folder, "*" + previousKey + ".ai-tags.txt").FirstOrDefault(path => Owned(path, previousKey));
        }
        var destination = Path.Combine(folder, FileName(result, labels));
        var baseName = Path.GetFileName(destination)[..^".ai-tags.txt".Length];
        for (var index = 2; File.Exists(destination) && !BatchRename.PathComparer.Equals(destination, old); index++)
            destination = Path.Combine(folder, baseName[..baseName.LastIndexOf("__", StringComparison.Ordinal)] + "_" + index + "__" + key + ".ai-tags.txt");
        var body = new StringBuilder().AppendLine(Header).AppendLine("SourceKey: " + key).AppendLine("源文件：" + result.Path)
            .AppendLine("标签：" + string.Join("，", labels.Select(label => label.Label)))
            .AppendLine("生成时间：" + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture))
            .AppendLine("时长：" + MediaTime.Format(result.DurationSeconds))
            .AppendLine(FormattableString.Invariant($"标签阈值：{threshold:0.00}；语义相似度：{sceneThreshold:0.00}；类别分差：{sceneMargin:0.00}"))
            .AppendLine("标签模型：" + result.Backend).AppendLine("语义模型：" + (result.Scenes?.Backend ?? result.SceneError ?? "未启用"))
            .AppendLine("素材标签：" + (result.RealPeopleOnly ? "真人" : "全部"))
            .AppendLine("描述模型：" + (result.CaptionModel ?? result.CaptionError ?? (string.IsNullOrWhiteSpace(result.Caption) ? "未启用" : "未知")))
            .AppendLine().AppendLine("类别\t标签\t分数\t分数类型\t模型\t达标采样时间");
        foreach (var label in labels)
        {
            var points = MediaTagTimeline.Points(result, label.Tags, label.Model == ModelCatalog.EmbeddingId ? label.Label : null);
            var times = label.Model == ModelCatalog.EmbeddingId
                ? result.Scenes?.Frames.Where(frame => frame.Candidates.Any(candidate => candidate.Label == label.Label && candidate.Qualifies(sceneThreshold, sceneMargin))).Select(frame => MediaTime.Format(frame.Seconds)) ?? []
                : points.Where(point => point.Score >= threshold).Select(point => MediaTime.Format(point.Seconds));
            body.AppendLine(FormattableString.Invariant($"{label.Category}\t{label.Label}\t{label.Score:0.000}\t{label.ScoreKind}\t{label.Model}\t{string.Join("，", times)}"));
        }
        if (result.Nsfw is { } nsfw)
        {
            body.AppendLine().AppendLine("真人 NSFW 分类")
                .AppendLine("模型：" + nsfw.Model + " · " + nsfw.Backend)
                .AppendLine(FormattableString.Invariant($"分类阈值：{nsfw.Threshold:0.00}；采样平均：{nsfw.Average:0.000}；采样峰值：{nsfw.Maximum:0.000}"))
                .AppendLine("结果：" + (nsfw.Suspected ? "疑似 NSFW" : "未达分类阈值"))
                .AppendLine("采样时间\tNSFW 分数");
            foreach (var frame in nsfw.Frames) body.AppendLine(FormattableString.Invariant($"{MediaTime.Format(frame.Seconds)}\t{frame.Score:0.000}"));
            if (nsfw.FallbackReason is not null) body.AppendLine("后端回退：" + nsfw.FallbackReason);
        }
        if (!string.IsNullOrWhiteSpace(result.Caption) || result.CaptionError is not null)
        {
            body.AppendLine().AppendLine("画面描述");
            if (!string.IsNullOrWhiteSpace(result.Caption)) body.AppendLine(result.Caption);
            if (result.CaptionError is not null) body.AppendLine("描述失败：" + result.CaptionError);
        }
        var temporary = Path.Combine(folder, ".avamedia-tags-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, body.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); MediaTagService.ValidateSource(result);
            File.Move(temporary, destination, BatchRename.PathComparer.Equals(destination, old));
            if (old is not null && !BatchRename.PathComparer.Equals(old, destination)) File.Delete(old);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Recommended-score labels for queue TXT reports (matches workbench default score mode).</summary>
    public static IReadOnlyList<MediaTagTextLabel> QualifyingLabels(MediaTagResult result, double threshold, double sceneThreshold, double sceneMargin,
        bool onlyLibrary = false, IReadOnlyList<WordCandidate>? library = null)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentException("标签阈值须为 0–1。");
        if (!double.IsFinite(sceneThreshold) || sceneThreshold is < 0 or > 1) throw new ArgumentException("场景相似度须为 0–1。");
        if (!double.IsFinite(sceneMargin) || sceneMargin < 0) throw new ArgumentException("场景分差不能为负。");
        var candidates = library ?? [];
        IEnumerable<MediaTagTextLabel> tags;
        if (onlyLibrary)
        {
            var scores = result.Scores.ToDictionary(score => score.Tag, score => MediaTagService.TagSignal(result, score), StringComparer.OrdinalIgnoreCase);
            tags = candidates.Where(entry => entry.Tags.Length > 0 && entry.Tags.All(tag => scores.TryGetValue(tag, out var value) && value >= threshold))
                .Select(entry => new MediaTagTextLabel(entry.Label, entry.Category, entry.Tags.Min(tag => scores.GetValueOrDefault(tag)),
                    ScoreKind(result, entry.Tags), ModelCatalog.JoyTagId, entry.Tags));
        }
        else tags = result.Scores.Where(score => (!result.RealPeopleOnly || WordLibraryCatalog.RealPeopleTags.Contains(score.Tag)
                || candidates.Any(entry => entry.Tags.Contains(score.Tag, StringComparer.OrdinalIgnoreCase))) && MediaTagService.TagSignal(result, score) >= threshold)
            .Select(score => new MediaTagTextLabel(WordLibraryCatalog.TagLabel(score.Tag), WordLibraryCatalog.TagCategory(score.Tag),
                MediaTagService.TagSignal(result, score), ScoreKind(result, [score.Tag]), ModelCatalog.JoyTagId, [score.Tag]));

        var scenes = result.Scenes?.Frames.SelectMany(frame => frame.Candidates)
            .Where(candidate => !WordLibraryCatalog.IsSemanticBaseline(candidate.Label)).DistinctBy(candidate => candidate.Label) ?? [];
        foreach (var candidate in scenes)
        {
            var peak = result.Scenes!.Frames.SelectMany(frame => frame.Candidates.Where(item => item.Label == candidate.Label))
                .Select(item => item.Similarity).DefaultIfEmpty(double.NegativeInfinity).Max();
            var qualifies = peak >= sceneThreshold && result.Scenes.Frames.Any(frame => frame.Candidates.Any(item =>
                item.Label == candidate.Label && item.Qualifies(sceneThreshold, sceneMargin)));
            if (!qualifies) continue;
            if (onlyLibrary && candidates.All(entry => entry.Label != candidate.Label)) continue;
            tags = tags.Append(new MediaTagTextLabel(candidate.Label, candidate.Category, peak, "cosine_similarity",
                ModelCatalog.EmbeddingId, []));
        }

        return tags
            .OrderBy(tag => tag.Category.StartsWith("NSFW", StringComparison.Ordinal) ? 0
                : tag.Category.StartsWith("场景", StringComparison.Ordinal) || tag.Category is "照明状态" or "画面照明" ? 1 : 2)
            .ThenByDescending(tag => tag.Score)
            .DistinctBy(tag => tag.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ScoreKind(MediaTagResult result, string[] tags) => !VideoFormats.IsVideo(result.Path) ? "score"
        : tags.All(WordLibraryCatalog.UsesSamplePeak) ? "sample_peak" : "sample_average";
}
