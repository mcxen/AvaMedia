namespace AvaMedia.Core;

public static partial class WordLibraryCatalog
{
    public static bool IsSemanticBaseline(string label) => label is "其他室内" or "照明不明" or "面部不明";

    private static WordCandidate[] ReadSceneWords() => ReadText("scene-context.tsv")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length is < 3 or > 4) throw new InvalidDataException("场景词库字段无效。");
            var tags = fields.Length == 4 ? fields[3].Split('+', StringSplitOptions.RemoveEmptyEntries) : [];
            if (tags.Any(tag => !JoyTags.Contains(tag))) throw new InvalidDataException("场景词库包含模型不支持的标签。");
            return new WordCandidate(fields[1], fields[0], fields[2], tags);
        }).ToArray();
}
