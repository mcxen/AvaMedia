namespace AvaMedia.Core;

public sealed record FolderNippleDetection(string ExposedCategoryId, string CoveredCategoryId);

/// <summary>Visible nipples use independent detector tags, including the absence side of a binary rule.</summary>
public static class FolderNippleClassification
{
    // JoyTag's recommended tag cutoff; the lower cutoff leaves weak evidence for review.
    public const double DefaultThreshold = .4;
    public const double DefaultReviewRange = .2;
    private static readonly string[] Tags = ["nipples", "puffy_nipples", "nipple_slip"];
    private static readonly HashSet<string> ExposedNames = ["露点", "漏点", "有露点", "有漏点", "乳头可见", "奶头可见"];
    private static readonly HashSet<string> CoveredNames = ["非露点", "非漏点", "不露点", "不漏点", "无露点", "无漏点", "未露点", "未漏点", "乳头不可见", "奶头不可见"];

    public static FolderNippleDetection? Resolve(FolderClassificationRule rule)
        => rule.NippleDetection ?? Resolve(rule.Categories);

    public static FolderNippleDetection? Resolve(IReadOnlyList<FolderClassificationCategory> categories)
    {
        if (categories.Count != 2) return null;
        var exposed = categories.FirstOrDefault(category => ExposedNames.Contains(category.Name.Trim()));
        var covered = categories.FirstOrDefault(category => CoveredNames.Contains(category.Name.Trim()));
        return exposed is null || covered is null ? null : new(exposed.Id, covered.Id);
    }

    internal static FolderClassificationDecision Decide(MediaTagResult media, FolderClassificationRule rule, FolderNippleDetection detection)
    {
        var exposed = rule.Categories.Single(category => category.Id == detection.ExposedCategoryId);
        var covered = rule.Categories.Single(category => category.Id == detection.CoveredCategoryId);
        var threshold = rule.UseAutomaticSettings ? DefaultThreshold : rule.Threshold;
        var reviewRange = rule.UseAutomaticSettings ? DefaultReviewRange : rule.Margin;
        var coveredThreshold = Math.Max(0, threshold - reviewRange);
        var video = VideoFormats.IsVideo(media.Path);
        var indices = Tags.Select(tag => media.Scores.Select((score, index) => (score.Tag, Index: index))
            .FirstOrDefault(item => item.Tag == tag, (Tag: "", Index: -1)).Index).ToArray();
        var frames = new List<FolderFrameClassification>();
        if (video)
        {
            foreach (var frame in media.Frames)
            {
                // Values include low scores. Missing entries in the cropped display list do not mean zero.
                var values = indices.Select(index => index >= 0 && frame.Values.Length == media.Scores.Count
                    ? (double?)frame.Values[index] : null).ToArray();
                AddFrame(frame.Seconds, values);
            }
        }
        else AddFrame(0, indices.Select(index => index >= 0 ? (double?)media.Scores[index].Score : null).ToArray());

        void AddFrame(double seconds, double?[] values)
        {
            var score = values.All(value => value is { } number && double.IsFinite(number) && number is >= 0 and <= 1)
                ? values.Max() : null;
            var category = score >= threshold ? exposed.Id : score < coveredThreshold ? covered.Id : null;
            frames.Add(new(seconds, category, score, null));
        }

        var hits = frames.Count(frame => frame.CategoryId == exposed.Id);
        var coveredFrames = frames.Count(frame => frame.CategoryId == covered.Id);
        var complete = frames.Count > 0 && frames.All(frame => frame.Similarity is not null)
            && (!video || frames.Count == media.SampledFrames);
        // One exposed sample is enough. An unobserved or ambiguous sample cannot establish absence.
        var categoryId = hits > 0 ? exposed.Id : complete && coveredFrames == frames.Count ? covered.Id : null;
        var peak = frames.Where(frame => frame.Similarity is not null).MaxBy(frame => frame.Similarity);
        var scores = peak?.Similarity is { } maximum
            ? new FolderCategoryScore[] { new(exposed.Id, exposed.Name, maximum, hits) } : [];
        return new(rule.Id, rule.Name, categoryId, categoryId == exposed.Id ? exposed.Name : categoryId == covered.Id ? covered.Name : "待确认",
            scores, frames, peak?.Seconds, frames.Count == 0 ? 0 : (double)(categoryId == exposed.Id ? hits : coveredFrames) / frames.Count,
            categoryId == exposed.Id ? "检出露点标签" : categoryId == covered.Id ? "采样画面未检出露点标签"
                : !complete ? "露点标签结果不完整，请重新分析。" : "露点标签分数接近判断边界");
    }
}
