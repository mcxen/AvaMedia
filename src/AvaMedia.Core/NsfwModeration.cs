using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public enum NsfwSignalState { Suspected, ContextOnly, NoEvidence }
public enum NsfwReviewDecision { Unreviewed, Nsfw, NonNsfw, Uncertain }
public sealed record NsfwTagRule(string Category, string Label, string Tag, bool Risk);
public sealed record NsfwEvidence(string Tag, string Label, string Category, bool Risk, double Average, double Maximum, double Signal);
public sealed record NsfwAssessment(NsfwSignalState State, double Threshold, string SignalBasis, IReadOnlyList<NsfwEvidence> Evidence);

/// <summary>Review triage from a pinned vocabulary. Signals are independent tag scores, not an NSFW probability.</summary>
public static class NsfwModeration
{
    private static readonly byte[] DictionaryBytes = ReadResource("nsfw-review.tsv");
    public static string DictionarySha256 { get; } = Convert.ToHexString(SHA256.HashData(DictionaryBytes)).ToLowerInvariant();
    public static IReadOnlyList<NsfwTagRule> Rules { get; } = ReadRules();
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
        return new(ordered.Any(item => item.Risk) ? NsfwSignalState.Suspected : ordered.Length > 0 ? NsfwSignalState.ContextOnly : NsfwSignalState.NoEvidence,
            threshold, video ? "sample_peak" : "image_score", ordered);
    }

    public static async Task WriteFeedbackAsync(Stream output, MediaTagResult result, NsfwReviewDecision review, DateTime reviewedAtUtc,
        double threshold, CancellationToken ct)
    {
        if (review == NsfwReviewDecision.Unreviewed || !Enum.IsDefined(review)) throw new ArgumentException("请先完成人工审核。");
        MediaTagService.ValidateSource(result);
        string hash;
        await using (var input = File.OpenRead(result.Path))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)).ToLowerInvariant();
        MediaTagService.ValidateSource(result);
        var artifacts = ModelCatalog.Find(ModelCatalog.JoyTagId).Files.Select(file => new { file.Path, file.Size, file.Sha256, file.Sources });
        var record = new
        {
            Schema = "avamedia.nsfw-feedback.v1", ContentSha256 = hash, result.Path, result.Length, result.LastWriteUtc,
            Model = ModelCatalog.JoyTagId, ModelArtifacts = artifacts, DictionarySha256,
            HumanReview = review, ReviewedAtUtc = reviewedAtUtc, ModelAssessment = Evaluate(result, threshold),
            result.Backend, result.FallbackReason, result.SampledFrames, result.InferredFrames,
            TagScores = result.Scores
        };
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        // One complete record per line; no source image/video bytes are exported.
        await output.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, json) + "\n"), ct).ConfigureAwait(false);
    }

    private static NsfwTagRule[] ReadRules()
    {
        var rules = Encoding.UTF8.GetString(DictionaryBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var fields = line.Split('\t');
                if (fields.Length != 4 || fields[3] is not ("risk" or "hint")) throw new InvalidDataException("NSFW 审核词库无效。");
                return new NsfwTagRule(fields[0], fields[1], fields[2], fields[3] == "risk");
            }).ToArray();
        if (rules.Length == 0 || rules.Select(rule => rule.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Length)
            throw new InvalidDataException("NSFW 审核词库标签重复或为空。");
        return rules;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = typeof(NsfwModeration).Assembly.GetManifestResourceStream("AvaMedia.Core.AiLexicons." + name)
            ?? throw new InvalidDataException("内置词库缺失：" + name);
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
}
