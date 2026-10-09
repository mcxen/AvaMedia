namespace AvaMedia.Core;

public enum BinaryMediaAnswer { Review, Yes, No }

public sealed record FolderClassificationRule(string Id, string Name, string PositiveDescription, string NegativeDescription)
{
    public double Threshold { get; init; } = .5;
    public double Margin { get; init; } = .04;
    public string PositiveLabel => "yes_" + Id;
    public string NegativeLabel => "no_" + Id;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 40 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("分类规则标识无效。");
        BatchRename.ValidateRenameKeyword(Name);
        if (!double.IsFinite(Threshold) || Threshold is < 0 or > 1
            || !double.IsFinite(Margin) || Margin is < 0 or > 1) throw new ArgumentException("分类阈值无效。");
        WordLibraryCatalog.Validate(Candidates());
    }

    public WordCandidate[] Candidates() =>
        [new(PositiveLabel, "分类-" + Id, PositiveDescription, []), new(NegativeLabel, "分类-" + Id, NegativeDescription, [])];

    public static IReadOnlyList<FolderClassificationRule> Presets { get; } = [
        new("forest", "森林",
            "A forest or woodland scene, with many trees, leafy canopies, undergrowth or a wooded trail dominating the view.",
            "A scene outside a forest: an indoor room, city street, beach, open grassland or bare mountain without woodland."),
        new("empty-shot", "空镜",
            "An establishing shot of scenery or an empty space with no visible people: landscape, architecture, street, room or natural detail.",
            "A shot containing visible people, with a person or a group present in the scene or shown in close-up."),
        new("coast", "海边",
            "A coastal scene showing the sea, a sandy or rocky beach, ocean waves or a seaside shoreline.",
            "An inland scene: a forest, mountain, city interior, river or lake without an ocean coast."),
        new("mountains", "山景",
            "A mountain landscape with visible mountain peaks, ridges, rocky slopes or a valley surrounded by mountains.",
            "A scene without a mountain landscape: an indoor room, flat city street, flat field or beach."),
        new("city", "城市",
            "An outdoor urban scene with city streets, buildings, a skyline, sidewalks or other dense built surroundings.",
            "A non-urban scene: a natural forest, mountain, open countryside, beach or an indoor room."),
        new("indoors", "室内",
            "An indoor scene inside a room or building, with walls, ceiling, furniture or interior fittings visible.",
            "An outdoor scene in open air: a street, forest, mountain, beach or countryside.")];
    public static FolderClassificationRule[] DefaultRules() => Presets.Take(2).ToArray();
}

public sealed record FolderClassificationDecision(string RuleId, string Name, BinaryMediaAnswer Answer,
    double? PositiveScore, double? NegativeScore, double? Seconds, string Evidence, bool Manual = false);
public sealed record FolderClassifiedFile(MediaTagResult Media, IReadOnlyList<FolderClassificationDecision> Decisions,
    IReadOnlyList<string> Tags);
public sealed record FolderScanResult(string[] Files, string[] Errors);

/// <summary>Binary decisions keep inconclusive observations separate from a negative answer.</summary>
public static class FolderClassification
{
    public static void ValidateRules(IReadOnlyList<FolderClassificationRule> rules)
    {
        if (rules.Count > 8) throw new ArgumentException("最多使用 8 条分类规则。");
        foreach (var rule in rules) rule.Validate();
        if (rules.Select(rule => rule.Id).Distinct().Count() != rules.Count
            || rules.Select(rule => rule.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Count)
            throw new ArgumentException("分类规则名称或标识重复。");
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
                { if (MediaTagService.Supports(path)) files.Add(path); continue; }
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

    public static FolderClassifiedFile Classify(MediaTagResult media, IReadOnlyList<FolderClassificationRule> rules, double tagThreshold)
    {
        ValidateRules(rules);
        if (!double.IsFinite(tagThreshold) || tagThreshold is < 0 or > 1) throw new ArgumentException("标签阈值须为 0–1。");
        var tags = media.Scores.Where(score => MediaTagService.TagSignal(media, score) >= tagThreshold)
            .OrderByDescending(score => MediaTagService.TagSignal(media, score)).Take(80)
            .Select(score => WordLibraryCatalog.TagLabel(score.Tag)).Distinct().ToArray();
        return new(media, rules.Select(rule => Decide(media, rule)).ToArray(), tags);
    }

    private static FolderClassificationDecision Decide(MediaTagResult media, FolderClassificationRule rule)
    {
        FolderClassificationDecision Result(BinaryMediaAnswer answer, double? yes, double? no, double? seconds, string evidence)
            => new(rule.Id, rule.Name, answer, yes, no, seconds, evidence);
        if (media.Scenes is null || media.SceneError is not null)
            return Result(BinaryMediaAnswer.Review, null, null, null, media.SceneError ?? "语义识别结果缺失");
        var observations = media.Scenes.Frames.Select(frame => (frame.Seconds,
            Yes: frame.Candidates.FirstOrDefault(match => match.Label == rule.PositiveLabel)?.Similarity,
            No: frame.Candidates.FirstOrDefault(match => match.Label == rule.NegativeLabel)?.Similarity)).ToArray();
        if (observations.Length == 0 || observations.Any(item => item.Yes is null || item.No is null
            || !double.IsFinite(item.Yes.Value) || !double.IsFinite(item.No.Value)))
            return Result(BinaryMediaAnswer.Review, null, null, null, "二分语义分数缺失");
        var positive = observations.Where(item => item.Yes >= rule.Threshold && item.Yes - item.No >= rule.Margin)
            .OrderByDescending(item => item.Yes - item.No).ToArray();
        var negative = observations.Where(item => item.No >= rule.Threshold && item.No - item.Yes >= rule.Margin).ToArray();
        // Mixed or ambiguous videos need review. Do not force the best frame's label on an entire video.
        var answerSemantic = positive.Length == observations.Length ? BinaryMediaAnswer.Yes
            : negative.Length == observations.Length ? BinaryMediaAnswer.No : BinaryMediaAnswer.Review;
        var best = observations.MaxBy(item => Math.Abs(item.Yes!.Value - item.No!.Value));
        return Result(answerSemantic, best.Yes, best.No, best.Seconds,
            answerSemantic == BinaryMediaAnswer.Review ? "采样画面有分歧或分差不足" : "二分语义匹配");
    }

    public static string AnswerLabel(BinaryMediaAnswer answer) => answer switch
    { BinaryMediaAnswer.Yes => "是", BinaryMediaAnswer.No => "否", _ => "待确认" };
}
