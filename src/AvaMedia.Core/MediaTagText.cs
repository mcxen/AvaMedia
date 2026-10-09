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
    private const string Header = "AvaMedia AI Tags / 1";
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
        return reader.ReadLine() == Header && reader.ReadLine() == "SourceKey: " + key;
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
            .AppendLine().AppendLine("类别\t标签\t分数\t分数类型\t模型\t达标采样时间");
        foreach (var label in labels)
        {
            var points = MediaTagTimeline.Points(result, label.Tags, label.Model == ModelCatalog.EmbeddingId ? label.Label : null);
            var times = label.Model == ModelCatalog.EmbeddingId
                ? result.Scenes?.Frames.Where(frame => frame.Candidates.Any(candidate => candidate.Label == label.Label && candidate.Similarity >= sceneThreshold && candidate.Margin >= sceneMargin)).Select(frame => MediaTime.Format(frame.Seconds)) ?? []
                : points.Where(point => point.Score >= threshold).Select(point => MediaTime.Format(point.Seconds));
            body.AppendLine(FormattableString.Invariant($"{label.Category}\t{label.Label}\t{label.Score:0.000}\t{label.ScoreKind}\t{label.Model}\t{string.Join("，", times)}"));
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
}
