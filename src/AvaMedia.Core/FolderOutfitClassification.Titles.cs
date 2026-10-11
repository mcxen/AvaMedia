namespace AvaMedia.Core;

public static partial class FolderOutfitClassification
{
    private sealed record ShootTitleLabel(string Label, int Kind, int Priority, string Family);
    private static readonly Lazy<Dictionary<string, ShootTitleLabel>> ShootTitleLabels = new(() =>
    {
        var labels = new List<ShootTitleLabel>();
        foreach (var tag in WordLibraryCatalog.JoyTags.Order(StringComparer.Ordinal))
        {
            var category = WordLibraryCatalog.TagCategory(tag);
            if (Describe(tag) is { Item: not null } garment)
                labels.Add(new(WordLibraryCatalog.TagLabel(tag), 1, garment.Color is null ? 1 : 0, garment.Item));
            else if (category.StartsWith("场景", StringComparison.Ordinal))
                labels.Add(new(WordLibraryCatalog.TagLabel(tag), 0, ScenePriority(category, WordLibraryCatalog.TagLabel(tag)), tag));
            else if (category is "发色特征" or "头发特征" || tag is "1girl" or "1boy" or "solo" or "selfie"
                || tag.EndsWith("girls", StringComparison.Ordinal) || tag.EndsWith("boys", StringComparison.Ordinal))
                labels.Add(new(WordLibraryCatalog.TagLabel(tag), 2, category is "发色特征" or "头发特征" ? 0 : 1,
                    category == "发色特征" ? "hair-color" : category == "头发特征" ? "hair-style" : tag));
        }
        labels.AddRange(WordLibraryCatalog.SceneEntries.Where(entry => entry.Category.StartsWith("场景", StringComparison.Ordinal)
                && !WordLibraryCatalog.IsSemanticBaseline(entry.Label))
            .Select(entry => new ShootTitleLabel(entry.Label, 0, ScenePriority(entry.Category, entry.Label), entry.Label)));
        return labels.GroupBy(label => label.Label, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.MinBy(label => label.Priority)!, StringComparer.OrdinalIgnoreCase);
    });

    private static int ScenePriority(string category, string label) => label is "室内" or "户外" ? 2
        : category == "场景空间" ? 0 : category is "场景物体" or "场景自然" or "场景位置" ? 1 : 3;

    /// <summary>Describe existing shoots with shared recognized labels, without changing membership.</summary>
    public static FolderClassifiedFile[] WithShootTitles(IEnumerable<FolderClassifiedFile> files)
    {
        var output = files.ToArray();
        var titles = output.SelectMany(file => file.Decisions.Where(decision => decision.OutfitShootName is not null)
                .Select(decision => (File: file, Decision: decision)))
            .GroupBy(item => (item.Decision.RuleId, Shoot: item.Decision.OutfitShootName!))
            // Keep titles stable when organizing only a subset of a shoot.
            .ToDictionary(group => group.Key, group => group.Key.Shoot.Contains(" · ", StringComparison.Ordinal)
                ? group.Key.Shoot : group.Key.Shoot + " · " + ShootTitle(group.Select(item => item.File)));
        return output.Select(file => file with { Decisions = file.Decisions.Select(decision =>
        {
            if (decision.OutfitShootName is not { } shoot) return decision;
            var title = titles[(decision.RuleId, shoot)];
            return decision with { OutfitShootName = title, Scores = decision.Scores.Select(score => score with
            {
                Name = score.Name.StartsWith(shoot + " · ", StringComparison.Ordinal)
                    ? title + score.Name[shoot.Length..] : score.Name
            }).ToArray() };
        }).ToArray() }).ToArray();
    }

    private static string ShootTitle(IEnumerable<FolderClassifiedFile> files)
    {
        var members = files.DistinctBy(file => file.Media.Path, BatchRename.PathComparer).ToArray();
        var common = members.SelectMany(file => file.Tags.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(ShootTitleLabels.Value.ContainsKey).Select(label => ShootTitleLabels.Value[label]))
            .GroupBy(label => label.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= Math.Ceiling(members.Length * .6))
            .Select(group => (Label: group.First(), Count: group.Count()))
            .OrderBy(item => item.Label.Priority).ThenByDescending(item => item.Count)
            .ThenBy(item => item.Label.Label, StringComparer.Ordinal).ToArray();
        var specificClothing = common.Any(item => item.Label.Kind == 1 && item.Label.Family is not ("underwear" or "lingerie"));
        var personCount = common.Any(item => item.Label.Kind == 2 && item.Label.Family is "1girl" or "1boy"
            || item.Label.Family.EndsWith("girls", StringComparison.Ordinal) || item.Label.Family.EndsWith("boys", StringComparison.Ordinal));
        var descriptive = common.Where(item => !(specificClothing && item.Label.Family is "underwear" or "lingerie")
            && !(personCount && item.Label.Family == "solo")).ToArray();
        var parts = Enumerable.Range(0, 3).SelectMany(kind => descriptive.Where(item => item.Label.Kind == kind)
                .DistinctBy(item => item.Label.Family).Take(2).Select(item => item.Label.Label))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        return parts.Length == 0 ? "拍摄素材" : string.Join(" · ", parts);
    }
}
