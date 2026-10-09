namespace AvaMedia.Core;

public static partial class WordLibraryCatalog
{
    private static WordCandidate[] ReadChineseWords() => ReadText("joytag-zh-curated.tsv")
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.Split('\t');
            if (fields.Length != 3 || !JoyTags.Contains(fields[2])) throw new InvalidDataException("中文标签映射无效。");
            return new WordCandidate(fields[1], fields[0], "A photo showing " + fields[2].Replace('_', ' ') + ".", [fields[2]]);
        }).ToArray();

    private static Dictionary<string, string> ReadChineseLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ReadText("joytag-zh.tsv").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length != 2 || !JoyTags.Contains(fields[0]) || !labels.TryAdd(fields[0], fields[1]))
                throw new InvalidDataException("中文标签映射包含无效或重复标签。");
            BatchRename.ValidateRenameKeyword(fields[1]);
        }
        return labels;
    }

    // A model tag used as a library key must not override its translated display name.
    // Descriptive names supplied by the user remain their chosen names.
    private static bool UsesModelLabel(WordCandidate entry) => entry.Tags.Length == 1
        && (entry.Label.Equals(entry.Tags[0], StringComparison.OrdinalIgnoreCase)
            || entry.Label.Equals(entry.Tags[0].Replace('_', ' '), StringComparison.OrdinalIgnoreCase)
            || entry.Label.Equals(RenameLabel(entry.Tags[0]), StringComparison.OrdinalIgnoreCase));

    public static string CandidateLabel(WordCandidate entry) => UsesModelLabel(entry) ? TagLabel(entry.Tags[0]) : entry.Label;
    public static string CandidateCategory(WordCandidate entry) => UsesModelLabel(entry) ? TagCategory(entry.Tags[0]) : entry.Category;
}
