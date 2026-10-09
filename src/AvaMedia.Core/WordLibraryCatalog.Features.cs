namespace AvaMedia.Core;

public static partial class WordLibraryCatalog
{
    private static WordCandidate[] ReadFeatureWords() => ReadText("person-features.tsv")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var fields = line.Split('\t');
                if (fields.Length != 3) throw new InvalidDataException("人物特征词库字段无效。");
                var tags = fields[2].Split('+');
                return new WordCandidate(fields[1], fields[0], "A photo showing "
                    + string.Join(" and ", tags.Select(tag => tag.Replace('_', ' '))) + ".", tags);
            }).ToArray();

    private static WordCandidate[] ReadRealPeopleWords() => ReadText("real-people.tsv")
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.Split('\t');
            if (fields.Length != 3) throw new InvalidDataException("真人词库字段无效。");
            return new WordCandidate(fields[1], fields[0], "A photograph showing "
                + string.Join(" and ", fields[2].Split('+').Select(tag => tag.Replace('_', ' '))) + ".", fields[2].Split('+'));
        }).ToArray();

    private static IEnumerable<WordCandidate> DisplayWords() => NsfwEntries.Concat(SceneEntries).Concat(RealPeopleEntries).Concat(ChineseEntries).Concat(Common());

    private static Dictionary<string, string> CreateTagCategories() => DisplayWords()
            .SelectMany(entry => entry.Tags.Select(tag => (Tag: tag, entry.Category)))
            .GroupBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Category, StringComparer.OrdinalIgnoreCase);

    public static string TagCategory(string tag) => TagCategories.GetValueOrDefault(tag) ?? "其他标签";

    private static Dictionary<string, string> CreateTagLabels()
    {
        var labels = ReadChineseLabels();
        foreach (var entry in ChineseEntries.Concat(DisplayWords())
            .Where(entry => entry.Tags.Length == 1)
            .GroupBy(entry => entry.Tags[0], StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())) labels[entry.Tags[0]] = entry.Label;
        return labels;
    }

    public static string TagLabel(string tag) => TagLabels.GetValueOrDefault(tag) ?? tag;

    public static bool UsesSamplePeak(string tag) => TagCategory(tag).StartsWith("NSFW", StringComparison.Ordinal)
        || TagCategory(tag).StartsWith("场景", StringComparison.Ordinal)
        || RealPeopleEntries.Any(entry => entry.Category == "动作姿态" && entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        || SceneEntries.Any(entry => entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
}
