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

    private static Dictionary<string, string> CreateTagCategories() => Common()
            .SelectMany(entry => entry.Tags.Select(tag => (Tag: tag, entry.Category)))
            .GroupBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Category, StringComparer.OrdinalIgnoreCase);

    public static string TagCategory(string tag) => TagCategories.GetValueOrDefault(tag) ?? "其他标签";
}
