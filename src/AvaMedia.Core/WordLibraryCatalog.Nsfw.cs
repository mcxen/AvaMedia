namespace AvaMedia.Core;

public static partial class WordLibraryCatalog
{
    private static WordCandidate[] ReadNudeNetWords() => ReadText("nudenet-labels.tsv")
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.Split('\t');
            if (fields.Length != 4) throw new InvalidDataException("NudeNet 分类字段无效。");
            // These are NudeNet class names, not JoyTag outputs. Keep them semantic-only.
            return new WordCandidate(fields[1], fields[0], fields[3] + " [NudeNet: " + fields[2] + "]", []);
        }).ToArray();
}
