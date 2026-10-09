using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

public enum NsfwSignalState { Suspected, ContextOnly, NoEvidence }
public sealed record NsfwTagRule(string Category, string Label, string Tag, bool Risk);
public sealed record NsfwEvidence(string Tag, string Label, string Category, bool Risk, double Average, double Maximum, double Signal);
public sealed record NsfwAssessment(NsfwSignalState State, double Threshold, string SignalBasis, IReadOnlyList<NsfwEvidence> Evidence)
{
    public RealNsfwResult? Classifier { get; init; }
}

/// <summary>Automatic risk assessment from a pinned vocabulary. Signals are independent tag scores, not an NSFW probability.</summary>
public static class NsfwModeration
{
    private static readonly byte[] DictionaryBytes = ReadResource("nsfw-review.tsv");
    public static string DictionarySha256 { get; } = Convert.ToHexString(SHA256.HashData(DictionaryBytes)).ToLowerInvariant();
    public static IReadOnlyList<NsfwTagRule> Rules { get; } = ReadRules();
    private static readonly HashSet<string> Tags = Rules.Select(rule => rule.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static bool ContainsTag(string tag) => Tags.Contains(tag);
    public static WordCandidate[] Candidates() => Rules.Select(rule => new WordCandidate(rule.Label, rule.Category,
        "An image tagged as " + rule.Tag.Replace('_', ' ') + ".", [rule.Tag])).ToArray();

    public static NsfwAssessment Evaluate(MediaTagResult result, double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentException("标签阈值须为 0–1。");
        var scores = result.Scores.ToDictionary(score => score.Tag, StringComparer.OrdinalIgnoreCase);
        var video = VideoFormats.IsVideo(result.Path);
        var evidence = new List<NsfwEvidence>();
        foreach (var rule in Rules)
        {
            if (!scores.TryGetValue(rule.Tag, out var score)) throw new InvalidDataException("标签结果不完整，请重新分析。");
            if (!double.IsFinite(score.Score) || !double.IsFinite(score.Maximum) || score.Score is < 0 or > 1 || score.Maximum is < 0 or > 1)
                throw new InvalidDataException("模型输出无效。");
            var signal = video ? Math.Max(score.Score, score.Maximum) : score.Score;
            if (signal >= threshold) evidence.Add(new(rule.Tag, rule.Label, rule.Category, rule.Risk, score.Score, score.Maximum, signal));
        }
        var ordered = evidence.OrderByDescending(item => item.Risk).ThenByDescending(item => item.Signal).ToArray();
        return new(ordered.Any(item => item.Risk) || result.Nsfw?.Suspected == true ? NsfwSignalState.Suspected
            : ordered.Length > 0 ? NsfwSignalState.ContextOnly : NsfwSignalState.NoEvidence,
            threshold, video ? "sample_peak" : "image_score", ordered) { Classifier = result.Nsfw };
    }

    private static NsfwTagRule[] ReadRules()
    {
        var rules = Encoding.UTF8.GetString(DictionaryBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var fields = line.Split('\t');
                if (fields.Length != 4 || fields[3] is not ("risk" or "hint")) throw new InvalidDataException("NSFW 识别词库无效。");
                return new NsfwTagRule(fields[0], fields[1], fields[2], fields[3] == "risk");
            }).ToArray();
        if (rules.Length == 0 || rules.Select(rule => rule.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Length)
            throw new InvalidDataException("NSFW 识别词库标签重复或为空。");
        return rules;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(NsfwModeration).Assembly.GetManifestResourceStream("AvaMedia.Core.AiLexicons." + name)
            ?? throw new InvalidDataException("内置词库缺失：" + name);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
}
