using System.Globalization;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public enum RenameAction { Template, Add, Replace, Remove, Move, Case, Cleanup, Number, Date, Extension, ExtensionCase }
public enum RenamePlacement { Prefix, Suffix, Position }
public enum RenameCase { Lower, Upper, Title, Sentence }
public enum RenameCleanup { Trim, Collapse, Underscore, RemoveSpaces }
public enum RenameDate { Modified, Created }
public sealed record RenameOperation(RenameAction Action)
{
    public bool Enabled { get; init; } = true;
    public string Text { get; init; } = "";
    public string Find { get; init; } = "";
    public string Replacement { get; init; } = "";
    public bool Regex { get; init; }
    public bool IgnoreCase { get; init; }
    public bool FromEnd { get; init; }
    public int Position { get; init; }
    public int Count { get; init; } = 1;
    public int Destination { get; init; }
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Digits { get; init; } = 3;
    public string Separator { get; init; } = "_";
    public RenamePlacement Placement { get; init; } = RenamePlacement.Suffix;
    public RenameCase Case { get; init; }
    public RenameCleanup Cleanup { get; init; }
    public RenameDate Date { get; init; }
    public string DateFormat { get; init; } = "yyyyMMdd";
}
public sealed record RenameMediaInfo(int Width, int Height, double Duration);
public sealed record RenamePreviewEntry(string Source, string NewName, RenameItem? Item, string? Error)
{
    public bool Changed => Item is not null && !StringComparer.Ordinal.Equals(Item.Source, Item.Target);
}
public sealed record RenamePreview(IReadOnlyList<RenamePreviewEntry> Entries)
{
    public int Errors => Entries.Count(entry => entry.Error is not null);
    public int Changes => Entries.Count(entry => entry.Changed && entry.Error is null);
    public bool CanApply => Errors == 0 && Changes > 0;
    public RenameItem[] Plan => CanApply ? Entries.Select(entry => entry.Item!).ToArray() : [];
}

public static partial class BatchRename
{
    public static bool Supports(string path) => new MediaFileRouter().Classify(path) is MediaFileKind.Image or MediaFileKind.Video;
    public static string[] CollectMedia(IEnumerable<string> paths, bool recursive, CancellationToken ct = default)
    {
        var files = new List<string>(); var seen = new HashSet<string>(PathComparer);
        void Add(string path) { if (Supports(path) && seen.Add(Path.GetFullPath(path))) files.Add(Path.GetFullPath(path)); }
        foreach (var input in paths)
        {
            ct.ThrowIfCancellationRequested(); var path = Path.GetFullPath(input);
            if (File.Exists(path)) Add(path);
            else if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden }))
                { ct.ThrowIfCancellationRequested(); Add(file); }
        }
        return files.ToArray();
    }
    public static bool NeedsMediaInfo(IEnumerable<RenameOperation> operations) => operations.Any(rule => rule.Enabled && rule.Action == RenameAction.Template
        && Regex.IsMatch(rule.Text, @"\{(width|height|duration)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)));

    public static RenamePreview PreviewRules(IEnumerable<string> paths, IReadOnlyList<RenameOperation> operations,
        IReadOnlyDictionary<string, RenameMediaInfo>? media = null, IReadOnlyDictionary<string, string>? keywords = null, CancellationToken ct = default)
    {
        var rules = operations.Where(rule => rule.Enabled).ToArray();
        foreach (var rule in rules)
        {
            if (rule.Start < 0 || rule.Step < 1 || rule.Digits is < 1 or > 12 || rule.Position < 0 || rule.Count < 1 || rule.Destination < 0)
                throw new ArgumentException("编号、位置或长度参数无效。");
            if (rule.Action == RenameAction.Replace && rule.Find.Length == 0) throw new ArgumentException("请输入要查找的文本。");
            if (rule.Regex) _ = new Regex(rule.Find, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            if (rule.Action == RenameAction.Date) _ = DateTime.Now.ToString(rule.DateFormat, CultureInfo.InvariantCulture);
        }
        var rows = new List<RenamePreviewEntry>(); var index = 0;
        foreach (var path in paths.Select(Path.GetFullPath).Distinct(PathComparer))
        {
            ct.ThrowIfCancellationRequested(); string preview = "";
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("源文件不存在，请刷新文件列表。", path);
                var stem = Path.GetFileNameWithoutExtension(path); var extension = file.Extension;
                foreach (var rule in rules)
                {
                    ct.ThrowIfCancellationRequested();
                    var number = checked((long)rule.Start + (long)index * rule.Step).ToString("D" + rule.Digits, CultureInfo.InvariantCulture);
                    stem = rule.Action switch
                    {
                        RenameAction.Template => FormatTemplate(rule.Text, stem, extension, file, number, media?.GetValueOrDefault(path), keywords?.GetValueOrDefault(path)),
                        RenameAction.Add => Insert(stem, rule.Text, rule),
                        RenameAction.Replace => rule.Regex ? Regex.Replace(stem, rule.Find, rule.Replacement, RegexOptions.CultureInvariant | (rule.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(250))
                            : stem.Replace(rule.Find, rule.Replacement, rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                        RenameAction.Remove => Remove(stem, rule),
                        RenameAction.Move => Move(stem, rule),
                        RenameAction.Case => ChangeCase(stem, rule.Case),
                        RenameAction.Cleanup => rule.Cleanup switch
                        {
                            RenameCleanup.Trim => stem.Trim(),
                            RenameCleanup.Collapse => Regex.Replace(stem.Trim(), @"\s+", " "),
                            RenameCleanup.Underscore => Regex.Replace(stem.Trim(), @"\s+", "_"),
                            _ => Regex.Replace(stem, @"\s+", "")
                        },
                        RenameAction.Number => Insert(stem, number, rule with { Text = number }),
                        RenameAction.Date => Insert(stem, (rule.Date == RenameDate.Modified ? file.LastWriteTime : file.CreationTime).ToString(rule.DateFormat, CultureInfo.InvariantCulture), rule),
                        _ => stem
                    };
                    if (rule.Action == RenameAction.Extension)
                    {
                        var value = rule.Text.Trim().TrimStart('.');
                        if (!Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_-]{0,15}$")) throw new ArgumentException("扩展名须为 1–16 个字母、数字、下划线或短横线。");
                        extension = "." + value;
                    }
                    else if (rule.Action == RenameAction.ExtensionCase) extension = ChangeCase(extension, rule.Case);
                }
                preview = stem + extension; ValidateStem(stem); ValidateStem(preview);
                rows.Add(new(path, preview, new(path, Path.Combine(file.DirectoryName!, preview), file.Length, file.LastWriteTimeUtc), null));
            }
            catch (Exception error) when (error is not OperationCanceledException) { rows.Add(new(path, preview, null, error.Message)); }
            index++;
        }
        foreach (var group in rows.Select((row, i) => (row, i)).Where(value => value.row.Item is not null).GroupBy(value => Path.GetDirectoryName(value.row.Source)!, PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            var comparer = DirectoryComparer(group.Key, group.Select(value => value.row.Source));
            var sourceNames = group.Select(value => Path.GetFileName(value.row.Source)).ToHashSet(comparer);
            var duplicateSources = group.GroupBy(value => Path.GetFileName(value.row.Source), comparer).Where(names => names.Count() > 1).SelectMany(names => names).Select(value => value.i).ToHashSet();
            var duplicates = group.GroupBy(value => value.row.NewName, comparer).Where(names => names.Count() > 1).SelectMany(names => names).Select(value => value.i).ToHashSet();
            foreach (var (row, i) in group)
            {
                var target = row.Item!.Target;
                if (duplicateSources.Contains(i)) rows[i] = row with { Error = "重命名列表包含重复源文件。" };
                else if (duplicates.Contains(i)) rows[i] = row with { Error = "重命名后会产生同名文件。" };
                else if (Directory.Exists(target) || File.Exists(target) && !sourceNames.Contains(Path.GetFileName(target))) rows[i] = row with { Error = "目标名称已存在。" };
            }
        }
        return new(rows);
    }
    private static string FormatTemplate(string pattern, string name, string extension, FileInfo file, string number, RenameMediaInfo? media, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("命名模板不能为空。");
        return Regex.Replace(pattern, @"\{([^{}]+)\}", match => match.Groups[1].Value switch
        {
            "name" => name, "index" => number, "parent" => file.Directory?.Name ?? "", "ext" => extension.TrimStart('.'), "size" => file.Length.ToString(CultureInfo.InvariantCulture),
            "keyword" => Keyword(keyword),
            "width" => (media ?? throw new ArgumentException("请先读取媒体信息。")).Width.ToString(CultureInfo.InvariantCulture),
            "height" => (media ?? throw new ArgumentException("请先读取媒体信息。")).Height.ToString(CultureInfo.InvariantCulture),
            "duration" => (media ?? throw new ArgumentException("请先读取媒体信息。")).Duration.ToString("0.###", CultureInfo.InvariantCulture),
            var token when token.StartsWith("modified:", StringComparison.Ordinal) => file.LastWriteTime.ToString(token[9..], CultureInfo.InvariantCulture),
            var token when token.StartsWith("created:", StringComparison.Ordinal) => file.CreationTime.ToString(token[8..], CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("未知的命名字段：" + match.Value)
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
    private static string Keyword(string? value)
    {
        var keyword = value ?? "";
        if (keyword.Contains('{') || keyword.Contains('}')) throw new ArgumentException("命名关键词不能包含花括号。");
        ValidateStem(keyword);
        return keyword;
    }
    private static string ChangeCase(string text, RenameCase mode) => mode switch
    {
        RenameCase.Lower => text.ToLowerInvariant(), RenameCase.Upper => text.ToUpperInvariant(),
        RenameCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
        _ => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant()
    };
    private static int[] Boundaries(string text) => [.. StringInfo.ParseCombiningCharacters(text), text.Length];
    private static int Position(string text, RenameOperation rule)
    { var boundaries = Boundaries(text); var count = boundaries.Length - 1; return boundaries[Math.Clamp(rule.FromEnd ? count - rule.Position : rule.Position, 0, count)]; }
    private static string Insert(string text, string value, RenameOperation rule) => rule.Placement switch
    {
        RenamePlacement.Prefix => value + rule.Separator + text,
        RenamePlacement.Suffix => text + rule.Separator + value,
        _ => text.Insert(Position(text, rule), value)
    };
    private static string Remove(string text, RenameOperation rule)
    { var (start, length) = CharacterRange(text, rule); return text.Remove(start, length); }
    private static string Move(string text, RenameOperation rule)
    { var (start, length) = CharacterRange(text, rule); var value = text.Substring(start, length); var remaining = text.Remove(start, length); var boundaries = Boundaries(remaining); return remaining.Insert(boundaries[Math.Min(rule.Destination, boundaries.Length - 1)], value); }
    private static (int Start, int Length) CharacterRange(string text, RenameOperation rule)
    {
        var boundaries = Boundaries(text); var count = boundaries.Length - 1;
        var end = rule.FromEnd ? Math.Max(0, count - rule.Position) : Math.Min(count, (long)Math.Min(rule.Position, count) + rule.Count);
        var start = rule.FromEnd ? (int)Math.Max(0, end - rule.Count) : Math.Min(rule.Position, count);
        return (boundaries[start], boundaries[(int)end] - boundaries[start]);
    }
}
