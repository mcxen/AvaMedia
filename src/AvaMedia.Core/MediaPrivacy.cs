namespace AvaMedia.Core;

/// <summary>One policy for private groups, labels, evidence and new exports.</summary>
public static class MediaPrivacy
{
    private static readonly string[] PrivateWords = ["nsfw", "nudenet", "露点", "漏点", "乳头", "奶头", "阴毛", "裸体", "裸露", "全裸", "生殖器", "成人", "体位", "亲密行为", "私密", "口交", "肛交", "性交", "自慰", "骑乘", "后入", "情趣", "精液", "射精", "nipples", "penis", "pussy", "porn", "nude", "sex", "masturbat", "genital", "乳房", "乳沟", "阴茎", "阴道", "阴唇", "肛门", "睾丸", "龟头", "breast", "boob", "erection", "lingerie", "panties", "underwear", "私房", "大尺度", "福利", "无码", "有码", "打飞机", "撸管", "手淫", "口爱", "乳交", "足交", "无套", "内射", "颜射", "潮吹", "羞羞", "性感"];
    public static bool IsSensitiveText(string text) => PrivateWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    public static bool IsSensitiveTag(string tag) => NsfwModeration.ContainsTag(tag) || WordLibraryCatalog.TagCategory(tag).StartsWith("NSFW", StringComparison.Ordinal)
        || WordLibraryCatalog.TagCategory(tag) == "其他标签" || IsSensitiveText(tag) || tag is "genitals" or "rating:explicit" or "rating:questionable";
    private static readonly Lazy<HashSet<string>> SensitiveLabels = new(() => NsfwModeration.Rules.Select(rule => rule.Label)
        .Concat(WordLibraryCatalog.BuiltIns.SelectMany(library => library.Entries).Where(entry => entry.Category.StartsWith("NSFW", StringComparison.Ordinal)).Select(WordLibraryCatalog.CandidateLabel))
        .ToHashSet(StringComparer.OrdinalIgnoreCase));
    public static bool IsSensitiveLabel(string label, string category, IEnumerable<string>? tags = null)
        => SensitiveLabels.Value.Contains(label.Trim()) || IsSensitiveText(label) || IsSensitiveText(category) || tags?.Any(IsSensitiveTag) == true;
    public static bool IsSensitive(WordCandidate candidate) => IsSensitiveLabel(candidate.Label, candidate.Category, candidate.Tags)
        || IsSensitiveText(candidate.Description);
    public static bool IsSensitiveRule(FolderClassificationRule rule) => rule.IsNsfw || FolderNippleClassification.Resolve(rule) is not null
        || IsSensitiveLabel(rule.Name, "") || rule.Categories.Any(category => IsSensitiveLabel(category.Name, category.Description, category.Tags.SelectMany(tags => tags)));

    public static MediaTagResult Filter(MediaTagResult media, bool enabled, IEnumerable<string>? privateLabels = null)
    {
        if (enabled) return media;
        var indices = media.Scores.Select((score, index) => (score, index)).Where(item => !IsSensitiveTag(item.score.Tag)).Select(item => item.index).ToArray();
        var hidden = privateLabels?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        bool Safe(MediaSceneMatch match) => !hidden.Contains(match.Label) && !IsSensitiveLabel(match.Label, match.Category);
        return media with
        {
            Scores = indices.Select(index => media.Scores[index]).ToArray(),
            Frames = media.Frames.Select(frame => frame with
            {
                Scores = frame.Scores.Where(score => !IsSensitiveTag(score.Tag)).ToArray(),
                Values = frame.Values.Length == media.Scores.Count ? indices.Select(index => frame.Values[index]).ToArray() : []
            }).ToArray(),
            Scenes = media.Scenes is not { } scenes ? null : scenes with
            {
                Scores = scenes.Scores.Where(score => !hidden.Contains(score.Label) && !IsSensitiveLabel(score.Label, score.Category)).ToArray(),
                Frames = scenes.Frames.Select(frame => frame with { Matches = frame.Matches.Where(Safe).ToArray(), Candidates = frame.Candidates.Where(Safe).ToArray() }).ToArray()
            },
            Nsfw = null,
            Caption = media.Caption is { } caption && IsSensitiveText(caption) ? null : media.Caption
        };
    }

    public static AiActivity Filter(AiActivity activity, bool enabled) => enabled ? activity : activity with
    {
        // Activity lines contain combined labels; omit them as a whole rather than expose an unmapped synonym.
        RecentResults = [],
        Stage = IsSensitiveText(activity.Stage) ? "识别媒体标签" : activity.Stage,
        RecentStages = activity.RecentStages.Where(stage => !IsSensitiveText(stage)).ToArray(),
        PreviewCaption = IsSensitiveText(activity.PreviewCaption) ? "" : activity.PreviewCaption,
        Nodes = activity.Nodes.Select(node => node with
        { Title = IsSensitiveText(node.Title) ? "识别媒体标签" : node.Title, Snapshot = node.Snapshot is { } snapshot ? Filter(snapshot, false) : null }).ToArray(),
        Detail = IsSensitiveText(activity.Detail) ? "" : activity.Detail,
        Model = IsSensitiveText(activity.Model) ? "AI 标签" : activity.Model
    };
}
