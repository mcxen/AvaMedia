namespace AvaMedia.Core;

public sealed record FolderClassificationCategory(string Id, string Name, string Description)
{
    // Alternatives are OR; tags within an alternative are AND.
    public string[][] Tags { get; init; } = [];
    public string[] SupersededBy { get; init; } = [];
}

/// <summary>Categories in a group compete for one destination; uncertainty has its own basket.</summary>
public sealed record FolderClassificationRule(string Id, string Name, FolderClassificationCategory[] Categories)
{
    public const double DefaultThreshold = .5;
    public const double DefaultMargin = .04;
    public const double DefaultMinimumAgreement = .8;
    public FolderNippleDetection? NippleDetection { get; init; }
    public bool IsNsfw { get; init; }
    public bool ByDuration { get; init; }
    public string? FallbackCategoryId { get; init; }
    public bool UsePeakEvidence { get; init; }
    public bool UsesTagScores => Categories.Any(category => category.Tags.Length > 0);
    public bool UseAutomaticSettings { get; init; } = true;
    public double Threshold { get; init; } = DefaultThreshold;
    public double Margin { get; init; } = DefaultMargin;
    public double MinimumAgreement { get; init; } = DefaultMinimumAgreement;
    public string Label(string categoryId) => "class_" + Id + "_" + categoryId;

    public void Validate()
    {
        ValidateId(Id);
        BatchRename.ValidateRenameKeyword(Name);
        if (Categories is null || Categories.Length is < 2 or > 12)
            throw new ArgumentException("每组须包含 2–12 个类别。");
        foreach (var category in Categories)
        {
            if (category is null) throw new ArgumentException("分类字段缺失。");
            ValidateId(category.Id);
            BatchRename.ValidateRenameKeyword(category.Name);
            if (string.IsNullOrWhiteSpace(category.Description) || category.Description.Length > 512
                || category.Description.Any(character => character is '\t' or '\r' or '\n'))
                throw new ArgumentException("类别画面描述须为 1–512 个字符且不能换行。");
            if (category.Name is "待确认" or "待分析") throw new ArgumentException("类别名称不能使用待确认或待分析。");
        }
        if (Categories.Select(category => category.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Categories.Length
            || Categories.Select(category => category.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Categories.Length)
            throw new ArgumentException("同组类别名称或标识重复。");
        if (FallbackCategoryId is not null && !Categories.Any(category => category.Id == FallbackCategoryId && category.Tags.Length == 0))
            throw new ArgumentException("其他类别标识无效。");
        foreach (var category in Categories)
        {
            if (category.Tags.Any(alternative => alternative.Length == 0 || alternative.Any(tag => !WordLibraryCatalog.JoyTags.Contains(tag))))
                throw new ArgumentException("分类包含模型不支持的标签。");
            if (category.SupersededBy.Any(id => id == category.Id || !Categories.Any(other => other.Id == id)))
                throw new ArgumentException("分类优先级标识无效。");
        }
        if (ByDuration && !Categories.Select(category => category.Id).SequenceEqual(FolderClassificationPresets.DurationCategoryIds))
            throw new ArgumentException("视频时长类别无效。");
        if (!double.IsFinite(Threshold) || Threshold is < 0 or > 1
            || !double.IsFinite(Margin) || Margin is < 0 or > 1
            || !double.IsFinite(MinimumAgreement) || MinimumAgreement is <= .5 or > 1)
            throw new ArgumentException("分类阈值无效。");
        if (FolderNippleClassification.Resolve(this) is { } detection
            && (Categories.Length != 2 || detection.ExposedCategoryId == detection.CoveredCategoryId
                || !Categories.Any(category => category.Id == detection.ExposedCategoryId)
                || !Categories.Any(category => category.Id == detection.CoveredCategoryId)))
            throw new ArgumentException("露点分类须包含露点和非露点两个类别。");
        var candidates = Candidates();
        if (candidates.Length > 0) WordLibraryCatalog.Validate(candidates);
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("分类标识无效。");
    }

    public WordCandidate[] Candidates() => ByDuration || UsesTagScores || FolderNippleClassification.Resolve(this) is not null ? [] : Categories.Select(category =>
        new WordCandidate(Label(category.Id), "分类-" + Id, category.Description, [])).ToArray();

    private static IReadOnlyList<FolderClassificationRule> ScenePresets { get; } = [
        new("nipple-visibility", "露点与否", [
            new("exposed", "露点", "画面中能看到裸露乳头。"),
            new("covered", "非露点", "乳头未露出或被衣物遮住。")])
            { NippleDetection = new("exposed", "covered"), IsNsfw = true },
        new("scenery", "场景", [
            new("forest", "森林", "A forest or woodland scene with dense trees, leafy canopies, undergrowth or a wooded trail."),
            new("coast", "海边", "A coastal scene showing the ocean, a sandy or rocky beach, sea waves or a seaside shoreline."),
            new("mountains", "山景", "A mountain landscape with peaks, ridges, rocky slopes or valleys surrounded by mountains."),
            new("city", "城市", "An outdoor urban scene with streets, buildings, sidewalks or a city skyline."),
            new("indoors", "室内", "An indoor room or building interior with walls, ceilings, furniture or interior fittings."),
            new("other", "其他场景", "A scene in open countryside, grassland, a river, lake, sky or a close-up detail.")]),
        new("indoor-location", "室内场景", [
            new("room", "房间", "A living room, office or general room interior, with seating, tables or shelves, not focused on a bed or bathroom fittings."),
            new("bed", "床上", "A scene focused on a bed surface, mattress, pillows or bed covers. The bed fills the main part of the view."),
            new("bathroom", "浴室", "A bathroom interior with a bathtub, shower, washbasin, toilet, tiled walls or bathroom fittings.")]),
        new("empty-shot", "空镜", [
            new("empty", "空镜", "An establishing shot of scenery or an empty space without visible people: landscape, architecture, street, room or natural detail."),
            new("people", "有人物", "A shot with visible people, with a person or a group present in the scene or shown in close-up.")]),
        new("age-appearance", "外观年龄段", [
            new("young", "儿童或青少年", "A visible person with the appearance of a child or adolescent, showing a youthful face and body proportions."),
            new("adult", "成年人", "A visible person with the appearance of an adult, showing a mature face without prominent elderly facial features."),
            new("older", "老年人", "A visible person with the appearance of an older adult, showing pronounced age-related wrinkles, grey hair or elderly facial features.")])];
    public static IReadOnlyList<FolderClassificationRule> Presets { get; } = ScenePresets.Concat(FolderClassificationPresets.Additional).ToArray();
    public static FolderClassificationRule[] DefaultRules() => [Presets.Single(rule => rule.Id == "scenery")];
}

public sealed record FolderCategoryScore(string CategoryId, string Name, double Similarity, int MatchedFrames);
public sealed record FolderFrameClassification(double Seconds, string? CategoryId, double? Similarity, double? Margin);
public sealed record FolderClassificationDecision(string RuleId, string Name, string? CategoryId, string CategoryName,
    IReadOnlyList<FolderCategoryScore> Scores, IReadOnlyList<FolderFrameClassification> Frames, double? Seconds,
    double Agreement, string Evidence, bool Manual = false)
{
    public bool NeedsReview => CategoryId is null;
}
public sealed record FolderClassifiedFile(MediaTagResult Media, IReadOnlyList<FolderClassificationDecision> Decisions,
    IReadOnlyList<string> Tags);
public sealed record FolderScanResult(string[] Files, string[] Errors);

public static class FolderClassification
{
    public static void ValidateRules(IReadOnlyList<FolderClassificationRule> rules)
    {
        if (rules.Count > 8) throw new ArgumentException("最多使用 8 组分类。");
        foreach (var rule in rules) rule.Validate();
        if (rules.Select(rule => rule.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Count
            || rules.Select(rule => rule.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Count)
            throw new ArgumentException("分类组名称或标识重复。");
    }

    public static FolderScanResult Scan(IEnumerable<string> inputs, bool recursive, string? excludedFolder, CancellationToken ct)
    {
        var files = new HashSet<string>(BatchRename.PathComparer);
        var visited = new HashSet<string>(BatchRename.PathComparer);
        var errors = new List<string>();
        var pending = new Stack<string>(inputs.Select(Path.GetFullPath));
        var excluded = string.IsNullOrWhiteSpace(excludedFolder) ? null : Path.GetFullPath(excludedFolder);
        while (pending.TryPop(out var path))
        {
            ct.ThrowIfCancellationRequested();
            if (excluded is not null && IsWithin(path, excluded)) continue;
            try
            {
                var attributes = File.GetAttributes(path);
                // Never follow links outside the selected tree or traverse link cycles.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    if (!Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal) && MediaTagService.Supports(path)) files.Add(path);
                    continue;
                }
                if (!visited.Add(path)) continue;
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (recursive || (File.GetAttributes(child) & FileAttributes.Directory) == 0) pending.Push(child);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(child + " · " + error.Message); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(path + " · " + error.Message); }
        }
        return new(files.Order(BatchRename.PathComparer).ToArray(), errors.ToArray());
    }

    public static bool IsWithin(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static FolderClassifiedFile Classify(MediaTagResult media, IReadOnlyList<FolderClassificationRule> rules, double tagThreshold, bool includeNsfw = false)
    {
        ValidateRules(rules);
        if (!double.IsFinite(tagThreshold) || tagThreshold is < 0 or > 1) throw new ArgumentException("标签阈值须为 0–1。");
        var tags = media.Scores.Where(score => (includeNsfw || !MediaPrivacy.IsSensitiveTag(score.Tag)) && MediaTagService.TagSignal(media, score) >= tagThreshold)
            .OrderByDescending(score => MediaTagService.TagSignal(media, score)).Take(80)
            .Select(score => WordLibraryCatalog.TagLabel(score.Tag)).Distinct().ToArray();
        return new(media, rules.Where(rule => includeNsfw || !MediaPrivacy.IsSensitiveRule(rule)).Select(rule => Decide(media, rule)).ToArray(), tags);
    }

    private static FolderClassificationDecision Decide(MediaTagResult media, FolderClassificationRule rule)
    {
        if (rule.ByDuration) return FolderClassificationPresets.DecideDuration(media, rule);
        if (FolderNippleClassification.Resolve(rule) is { } detection)
            return FolderNippleClassification.Decide(media, rule, detection);
        if (rule.UsesTagScores) return FolderTagClassification.Decide(media, rule);
        var threshold = rule.UseAutomaticSettings ? FolderClassificationRule.DefaultThreshold : rule.Threshold;
        var margin = rule.UseAutomaticSettings ? FolderClassificationRule.DefaultMargin : rule.Margin;
        var minimumAgreement = rule.UseAutomaticSettings ? FolderClassificationRule.DefaultMinimumAgreement : rule.MinimumAgreement;
        FolderClassificationDecision Review(string evidence) => new(rule.Id, rule.Name, null, "待确认", [], [], null, 0, evidence);
        if (media.Scenes is null || media.SceneError is not null)
            return Review(media.SceneError ?? "语义识别结果缺失");
        var frames = new List<FolderFrameClassification>();
        var sums = new Dictionary<string, List<double>>();
        foreach (var category in rule.Categories) sums[category.Id] = [];
        foreach (var frame in media.Scenes.Frames)
        {
            var scores = rule.Categories.Select(category => (Category: category,
                Score: frame.Candidates.FirstOrDefault(match => match.Label == rule.Label(category.Id))?.Similarity)).ToArray();
            foreach (var item in scores.Where(item => item.Score is { } score && double.IsFinite(score)))
                sums[item.Category.Id].Add(item.Score!.Value);
            if (scores.Any(item => item.Score is null || !double.IsFinite(item.Score.Value)))
            { frames.Add(new(frame.Seconds, null, null, null)); continue; }
            var ranked = scores.OrderByDescending(item => item.Score).ToArray();
            var top = ranked[0]; var gap = top.Score!.Value - ranked[1].Score!.Value;
            frames.Add(new(frame.Seconds, top.Score >= threshold && gap >= margin && gap > 0 ? top.Category.Id : null, top.Score, gap));
        }
        if (frames.Count == 0) return Review("分类语义分数缺失");
        var aggregate = rule.Categories.Where(category => sums[category.Id].Count > 0).Select(category =>
            new FolderCategoryScore(category.Id, category.Name, sums[category.Id].Average(), frames.Count(frame => frame.CategoryId == category.Id)))
            .OrderByDescending(score => score.MatchedFrames).ThenByDescending(score => score.Similarity).ToArray();
        var winner = aggregate.FirstOrDefault();
        var agreement = winner is null ? 0 : (double)winner.MatchedFrames / frames.Count;
        var accepted = winner is { MatchedFrames: > 0 } && agreement >= minimumAgreement
            && frames.All(frame => frame.Similarity is not null);
        var best = frames.Where(frame => frame.CategoryId == winner?.CategoryId).MaxBy(frame => frame.Margin);
        best ??= frames.MaxBy(frame => frame.Similarity);
        return new(rule.Id, rule.Name, accepted ? winner!.CategoryId : null, accepted ? winner!.Name : "待确认",
            aggregate, frames, best?.Seconds, agreement,
            accepted ? "多类别语义匹配" : frames.Any(frame => frame.Similarity is null) ? "分类语义分数缺失"
                : winner is { MatchedFrames: > 0 } ? "采样画面有分歧" : "匹配分数或分差不足");
    }

    public static FolderClassifiedFile KeepManual(FolderClassifiedFile classified, FolderClassifiedFile? previous)
    {
        if (previous is null || !BatchRename.PathComparer.Equals(previous.Media.Path, classified.Media.Path)
            || previous.Media.Length != classified.Media.Length
            || previous.Media.LastWriteUtc != classified.Media.LastWriteUtc) return classified;
        return classified with { Decisions = classified.Decisions.Select(decision =>
        {
            var manual = previous.Decisions.FirstOrDefault(old => old.RuleId == decision.RuleId && old.Manual);
            return manual is null ? decision : decision with
            { CategoryId = manual.CategoryId, CategoryName = manual.CategoryName, Manual = true, Evidence = "人工确认" };
        }).ToArray() };
    }
}
