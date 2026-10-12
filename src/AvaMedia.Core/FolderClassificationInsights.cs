namespace AvaMedia.Core;

public enum FolderClassificationStage { Scan, Tags, Scene, Outfit, Group }
public sealed record FolderStageProgress(FolderClassificationStage Stage, string Name, string Model, int Done, int Total, bool Active, bool Enabled)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}
public enum FolderFileState { Queued, Processing, Done, Pending, Failed }
public sealed record FolderTagScore(string Tag, double Score);
public sealed record FolderFrameVisual(double Seconds, string? CategoryId, bool Consistent);
public sealed record FolderColorShare(string Hex, double Share);
public sealed record FolderRuleVerdict(string RuleId, string RuleName, string? CategoryId, string Value);
public sealed record FolderFileVisual(string Path, string Name, bool IsVideo, double DurationSeconds, FolderFileState State,
    FolderTagScore[] Tags, string? Scene, double? SceneScore, FolderFrameVisual[] Frames, double FrameConsistency,
    int InconsistentFrame, FolderColorShare[] OutfitColors, double PersonRatio, string? GroupId, string? GroupName,
    string? ShootName, double? Similarity, string Reason, bool PendingReview, FolderRuleVerdict[] Verdicts, string? Error)
{
    /// <summary>Best candidate categories of the first undecided rule, for one-click resolution.</summary>
    public FolderCategoryOption[] Options { get; init; } = [];
}
public sealed record FolderCategoryOption(string RuleId, string CategoryId, string Name, double Similarity);
public sealed record FolderGroupView(string RuleId, string CategoryId, string Name, string? ShootName, int Count, int Added, string[] CoverPaths);
public sealed record FolderHistogram(double Min, double Max, double Threshold, int[] Bins, int Grouped, int Pending, int Failed);
public sealed record FolderSourceSummary(string Folder, int Files, int Images, int Videos, long Bytes, string[] SamplePaths);
public sealed record FolderBeforeAfter(FolderSourceSummary[] Sources, int Images, int Videos, long Bytes, FolderShootView[] Shoots, int PendingCount, string[] PendingSamples);
public sealed record FolderShootView(string Name, int Count, FolderGroupView[] Groups);
public sealed record FolderClassificationLive(FolderClassificationTaskSnapshot Snapshot, FolderClassificationRule[] Rules,
    double Threshold, string[] SourceFolders, FolderRegroupState? Regroup, string? ActiveStage, double Progress, TimeSpan Elapsed);

/// <summary>Projections of checkpoint data; never starts inference or changes the source media.</summary>
public static class FolderClassificationInsights
{
    // Matches OutfitAppearanceService.Color: white, black, red, blue, green, yellow,
    // pink, purple, brown, grey, orange, multicolored (neutral swatch).
    private static readonly string[] Palette = ["#FFFFFF", "#202020", "#E53935", "#1E88E5", "#43A047", "#FDD835",
        "#EC80AC", "#8E24AA", "#795548", "#9E9E9E", "#FB8C00", "#B0A090"];

    public static FolderStageProgress[] Stages(FolderClassificationTaskSnapshot snapshot,
        IReadOnlyList<FolderClassificationRule> rules, int regroupOrdinal, string? activeStage) =>
        Stages(snapshot, rules, regroupOrdinal, activeStage, OutfitAppearanceService.IsComplete);

    internal static FolderStageProgress[] Stages(FolderClassificationTaskSnapshot snapshot,
        IReadOnlyList<FolderClassificationRule> rules, int regroupOrdinal, string? activeStage, Func<MediaTagResult, bool> complete)
    {
        var files = snapshot.Files; var total = files.Length;
        var scene = rules.Any(rule => rule.Candidates().Length > 0); var outfit = rules.Any(rule => rule.ByOutfit);
        bool Active(string text) => activeStage?.Contains(text, StringComparison.Ordinal) == true;
        return [new(FolderClassificationStage.Scan, "扫描文件夹", "", total, total, Active("扫描"), true),
            new(FolderClassificationStage.Tags, "识别标签", "JoyTag", files.Count(file => file.Result is not null || file.Error is not null), total,
                Active("识别标签") || Active("识别图片标签") || Active("识别视频标签"), true),
            new(FolderClassificationStage.Scene, "场景识别", "语义模型", files.Count(file => file.Result?.Media is { } media
                && (media.Scenes is not null || media.SceneSkipped || media.SceneError is not null)), total, scene && (Active("场景") || Active("语义")), scene),
            new(FolderClassificationStage.Outfit, "服装区域", "MediaPipe · DINOv2", files.Count(file => file.Result is { } result && complete(result.Media)), total,
                outfit && Active("服装") && !Active("分组"), outfit),
            new(FolderClassificationStage.Group, "相似归组", "相似度", regroupOrdinal, 0, outfit && Active("分组"), outfit)];
    }

    internal static FolderClassificationDecision? Primary(FolderClassifiedFile? result, IReadOnlyList<FolderClassificationRule>? rules = null)
    {
        if (result is null) return null;
        var outfit = rules?.FirstOrDefault(rule => rule.ByOutfit)?.Id;
        return result.Decisions.FirstOrDefault(decision => outfit is not null ? decision.RuleId == outfit : IsOutfit(decision))
            ?? result.Decisions.FirstOrDefault();
    }
    private static bool IsOutfit(FolderClassificationDecision decision) => decision.ByOutfit
        || decision.OutfitShootName is not null || decision.CategoryId?.StartsWith("outfit-", StringComparison.Ordinal) == true;
    internal static double? Similarity(FolderClassificationDecision? decision) => decision?.Scores
        .Where(score => double.IsFinite(score.Similarity)).Select(score => (double?)score.Similarity).Max();
    public static FolderFileVisual Visual(FolderClassificationTaskFile file, IReadOnlyList<FolderClassificationRule> rules, bool processing)
    {
        var media = file.Result?.Media; var decision = Primary(file.Result, rules); var video = VideoFormats.IsVideo(file.Path);
        var review = file.Result?.Decisions.Any(value => value.NeedsReview) == true;
        var state = file.Error is not null ? FolderFileState.Failed : processing ? FolderFileState.Processing
            : media is null || file.Pending ? FolderFileState.Queued : review ? FolderFileState.Pending : FolderFileState.Done;
        var observations = decision?.Frames.ToArray() ?? [];
        var majority = observations.Where(frame => frame.CategoryId is not null).GroupBy(frame => frame.CategoryId)
            .OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key == decision?.CategoryId).FirstOrDefault()?.Key ?? decision?.CategoryId;
        var frames = observations.Select(frame => new FolderFrameVisual(frame.Seconds, frame.CategoryId,
            majority is not null && frame.CategoryId == majority)).ToArray();
        if (frames.Length == 0 && media is not null)
            frames = media.Frames.Select(frame => new FolderFrameVisual(frame.Seconds, null, false)).ToArray();
        var appearance = media?.OutfitFrames.Where(frame => frame.Colors.Length == 12).ToArray() ?? [];
        var colors = Enumerable.Range(0, 12).Select(index => appearance.Length == 0 ? 0 : appearance.Average(frame =>
            double.IsFinite(frame.Colors[index]) ? Math.Max(0, frame.Colors[index]) : 0)).ToArray();
        var sum = colors.Sum();
        var sceneDecision = file.Result?.Decisions.FirstOrDefault(value => rules.Any(rule => rule.Id == value.RuleId && rule.Candidates().Length > 0));
        var sceneScore = media?.Scenes?.Scores.Where(score => double.IsFinite(score.Similarity)).MaxBy(score => score.Similarity);
        var reason = decision is null ? file.Error ?? "等待分析" : string.Join(" · ", new[] {
            decision.Manual ? "人工确认" : null, OneLine(decision.Evidence),
            Similarity(decision) is { } score ? $"相似度 {score:P0}" : null }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new FolderFileVisual(file.Path, Path.GetFileName(file.Path), video, media?.DurationSeconds ?? 0, state,
            media?.Scores.Where(score => double.IsFinite(score.Score)).OrderByDescending(score => score.Score).Take(8)
                .Select(score => new FolderTagScore(score.Tag, score.Score)).ToArray() ?? [],
            sceneDecision is { NeedsReview: false } ? sceneDecision.CategoryName : sceneScore?.Label,
            sceneDecision is { NeedsReview: false } ? Similarity(sceneDecision) : sceneScore?.Similarity,
            frames, !video ? 1 : frames.Length == 0 ? 0 : (double)frames.Count(frame => frame.Consistent) / frames.Length,
            !video ? 0 : Array.FindIndex(frames, frame => !frame.Consistent) + 1,
            sum > 0 ? colors.Select((share, index) => new FolderColorShare(Palette[index], share / sum)).Where(color => color.Share > 0).ToArray() : [],
            media?.OutfitFrames.Where(frame => frame.Pixels > 0).Select(frame => (double)frame.PersonPixels / frame.Pixels).DefaultIfEmpty().Average() ?? 0,
            decision?.CategoryId, decision?.CategoryName, decision?.OutfitShootName, Similarity(decision), reason, review,
            file.Result?.Decisions.Select(value => new FolderRuleVerdict(value.RuleId, value.Name, value.CategoryId, value.DisplayName)).ToArray() ?? [], file.Error) with
        {
            Options = file.Result?.Decisions.FirstOrDefault(value => value.NeedsReview) is { } open
                ? open.Scores.OrderByDescending(score => score.Similarity).Take(3)
                    .Select(score => new FolderCategoryOption(open.RuleId, score.CategoryId, score.Name, score.Similarity)).ToArray() : []
        };
    }
    private static string OneLine(string value) => string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static FolderGroupView[] Groups(IEnumerable<FolderClassificationTaskFile> files, IReadOnlyDictionary<string, int>? previousCounts)
    {
        var selected = files.Where(file => file.Result is not null && file.Error is null && !file.Pending).ToArray();
        var ruleId = selected.SelectMany(file => file.Result!.Decisions).FirstOrDefault(IsOutfit)?.RuleId
            ?? selected.SelectMany(file => file.Result!.Decisions).FirstOrDefault()?.RuleId;
        return selected.Select(file => (File: file, Decision: file.Result!.Decisions.FirstOrDefault(decision => decision.RuleId == ruleId)))
            .Where(item => item.Decision is { NeedsReview: false }).GroupBy(item => (item.Decision!.RuleId, CategoryId: item.Decision.CategoryId!))
            .Select(group => new FolderGroupView(group.Key.RuleId, group.Key.CategoryId, group.First().Decision!.CategoryName,
                group.First().Decision!.OutfitShootName, group.Count(), Math.Max(0, group.Count() -
                    (previousCounts?.GetValueOrDefault(group.Key.RuleId + "/" + group.Key.CategoryId) ?? 0)),
                group.OrderByDescending(item => Similarity(item.Decision)).ThenBy(item => item.File.Path, BatchRename.PathComparer)
                    .Take(3).Select(item => item.File.Path).ToArray()))
            .OrderBy(group => group.ShootName, StringComparer.Ordinal).ThenBy(group => group.Name, StringComparer.Ordinal).ToArray();
    }

    public static FolderHistogram Histogram(IEnumerable<FolderClassificationTaskFile> files, double threshold, int bins = 20, double min = .5, double max = .95)
    {
        if (bins is < 1 or > 10000 || !double.IsFinite(min) || !double.IsFinite(max) || max <= min || !double.IsFinite(threshold))
            throw new ArgumentException("直方图参数无效。");
        var values = new int[bins]; var grouped = 0; var pending = 0; var failed = 0;
        foreach (var file in files)
        {
            if (file.Error is not null) { failed++; continue; }
            if (file.Pending || file.Result is null) { pending++; continue; }
            var score = file.Result.Decisions.Select(Similarity).Max();
            if (score is { } similarity)
                values[(int)Math.Clamp(Math.Floor((similarity - min) / (max - min) * bins), 0, bins - 1)]++;
            if (file.Result.Decisions.Any(decision => decision.NeedsReview) || score is { } below && below < threshold) pending++;
            else grouped++;
        }
        return new(min, max, threshold, values, grouped, pending, failed);
    }

    public static FolderBeforeAfter BeforeAfter(IEnumerable<FolderClassificationTaskFile> files, IReadOnlyList<string> sourceFolders)
    {
        var all = files.ToArray();
        var sources = all.GroupBy(file => sourceFolders.Where(folder => FolderClassification.IsWithin(file.Path, folder))
            .OrderByDescending(folder => Path.GetFullPath(folder).Length).FirstOrDefault() ?? Path.GetDirectoryName(file.Path) ?? "", BatchRename.PathComparer)
            .Select(group => new FolderSourceSummary(group.Key, group.Count(), group.Count(file => !VideoFormats.IsVideo(file.Path)),
                group.Count(file => VideoFormats.IsVideo(file.Path)), group.Sum(file => Size(file.Path)), group.Take(25).Select(file => file.Path).ToArray())).ToArray();
        var pending = all.Where(file => file.Error is null && (file.Result is null || file.Pending || file.Result.Decisions.Any(decision => decision.NeedsReview))).ToArray();
        return new(sources, sources.Sum(source => source.Images), sources.Sum(source => source.Videos), sources.Sum(source => source.Bytes),
            Groups(all, null).GroupBy(group => group.ShootName ?? "未指定拍摄组").Select(group => new FolderShootView(group.Key,
                group.Sum(value => value.Count), group.ToArray())).ToArray(), pending.Length, pending.Take(25).Select(file => file.Path).ToArray());
    }
    private static long Size(string path)
    {
        try { return new FileInfo(path) is { Exists: true } info ? info.Length : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return 0; }
    }
    public static FolderClassificationTaskFile ResolvePending(FolderClassificationTaskFile file, string ruleId, string categoryId, string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(categoryName)) throw new ArgumentException("请选择分类类别。");
        if (file.Result is not { } result || !result.Decisions.Any(decision => decision.RuleId == ruleId)) throw new ArgumentException("分类判定不存在。", nameof(ruleId));
        return file with { Result = result with { Decisions = result.Decisions.Select(decision => decision.RuleId == ruleId
            ? decision with { CategoryId = categoryId, CategoryName = categoryName, Manual = true } : decision).ToArray() } };
    }
}
