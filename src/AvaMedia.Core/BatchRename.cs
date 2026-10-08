using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public sealed record RenameRules(string Pattern = "{name}_{index}", string Prefix = "", string Suffix = "", string Find = "", string Replace = "", int FirstIndex = 1, int Digits = 3);
public sealed record RenameItem(string Source, string Target, long Length, DateTime LastWriteUtc);

public static partial class BatchRename
{
    private static readonly object RenameWriteLock = new();
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static RenameItem[] PreviewRename(IEnumerable<string> paths, RenameRules rules, IReadOnlyDictionary<string, string>? keywords = null)
    {
        var operations = new List<RenameOperation>();
        if (!string.IsNullOrEmpty(rules.Find)) operations.Add(new(RenameAction.Replace) { Find = rules.Find, Replacement = rules.Replace });
        operations.Add(new(RenameAction.Template) { Text = rules.Pattern, Start = rules.FirstIndex, Digits = rules.Digits });
        if (rules.Prefix.Length > 0) operations.Add(new(RenameAction.Add) { Text = rules.Prefix, Placement = RenamePlacement.Prefix, Separator = "" });
        if (rules.Suffix.Length > 0) operations.Add(new(RenameAction.Add) { Text = rules.Suffix, Placement = RenamePlacement.Suffix, Separator = "" });
        var preview = PreviewRules(paths, operations, keywords: keywords);
        if (preview.Entries.FirstOrDefault(entry => entry.Error is not null) is { } failed) throw new IOException(failed.Error + " · " + failed.Source);
        return preview.Entries.Select(entry => entry.Item!).ToArray();
    }

    public static void ValidateRenameKeyword(string keyword)
    {
        if (keyword.Length > 100 || keyword.Contains('{') || keyword.Contains('}')) throw new ArgumentException("命名关键词不能超过 100 字符或包含花括号。");
        ValidateStem(keyword);
    }

    private static void ValidateStem(string stem)
    {
        if (stem.Length > 220 || !OperatingSystem.IsWindows() && System.Text.Encoding.UTF8.GetByteCount(stem) > 255)
            throw new ArgumentException("文件名过长，请缩短名称。");
        if (string.IsNullOrWhiteSpace(stem) || stem.EndsWith('.') || stem.EndsWith(' ') || stem is "." or ".." || stem.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException($"文件名无效：{stem}");
        var first = stem.Split('.')[0].ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" || first.Length == 4 && (first.StartsWith("COM") || first.StartsWith("LPT")) && first[3] is >= '0' and <= '9')
            throw new ArgumentException($"不能使用系统保留文件名：{stem}");
    }

    public static void ValidateRenamePlan(IReadOnlyList<RenameItem> items)
    {
        var sources = new HashSet<string>(items.Select(i => i.Source), PathComparer);
        var folders = items.GroupBy(item => Path.GetDirectoryName(item.Source)!, PathComparer)
            .ToDictionary(group => group.Key, group => DirectoryComparer(group.Key, group.Select(item => item.Source)), PathComparer);
        foreach (var group in items.GroupBy(item => Path.GetDirectoryName(item.Source)!, PathComparer))
            if (group.Select(item => Path.GetFileName(item.Source)).Distinct(folders[group.Key]).Count() != group.Count())
                throw new ArgumentException("重命名列表包含重复源文件。");
        var targets = new Dictionary<string, HashSet<string>>(PathComparer);
        if (sources.Count != items.Count) throw new ArgumentException("重命名列表包含重复源文件。");
        foreach (var item in items)
        {
            var folder = Path.GetDirectoryName(item.Source)!;
            var comparer = folders[folder];
            if (!targets.TryGetValue(folder, out var names)) targets[folder] = names = new(comparer);
            if (!names.Add(Path.GetFileName(item.Target))) throw new IOException("重命名后会产生同名文件：" + item.Target);
            if (!PathComparer.Equals(Path.GetDirectoryName(item.Source), Path.GetDirectoryName(item.Target))) throw new ArgumentException("批量重命名仅支持原目录内改名。");
            ValidateStem(Path.GetFileName(item.Target));
            if (Directory.Exists(item.Target) || File.Exists(item.Target) && !items.Any(source => PathComparer.Equals(Path.GetDirectoryName(source.Source), folder)
                && comparer.Equals(Path.GetFileName(source.Source), Path.GetFileName(item.Target)))) throw new IOException("目标名称已存在：" + item.Target);
            var info = new FileInfo(item.Source);
            if (!info.Exists || info.Length != item.Length || info.LastWriteTimeUtc != item.LastWriteUtc) throw new IOException("预览后源文件已改变，请重新预览：" + item.Source);
        }
    }

    /// <summary>Stages every changed source before assigning targets, permitting name swaps. On failure rolls back.</summary>
    public static RenameItem[] ApplyRename(IReadOnlyList<RenameItem> plan, string journalPath)
    {
        lock (RenameWriteLock) return ApplyRenameCore(plan, journalPath, "completed");
    }

    private static RenameItem[] ApplyRenameCore(IReadOnlyList<RenameItem> plan, string journalPath, string completedState)
    {
        ValidateRenamePlan(plan);
        var changed = plan.Where(i => !StringComparer.Ordinal.Equals(i.Source, i.Target)).ToArray();
        if (changed.Length == 0) return [];
        var stages = changed.Select(i => new RenameStage(i, Path.Combine(Path.GetDirectoryName(i.Source)!, ".avamedia-rename-" + Guid.NewGuid().ToString("N") + ".tmp"))).ToArray();
        var pendingJournal = journalPath + ".pending";
        if (File.Exists(pendingJournal))
        {
            var pending = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(pendingJournal));
            var finished = File.Exists(journalPath) ? JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(journalPath)) : null;
            var resolved = pending is { State: "rolled-back" } || pending is { Stages: not null } && finished is { State: "completed" or "undone", Stages: not null }
                && pending.Stages.SequenceEqual(finished.Stages);
            if (!resolved) throw new IOException("仍有未完成的重命名，请先处理恢复记录：" + pendingJournal);
        }
        SaveJournal(pendingJournal, new("in-progress", stages));
        int staged = 0, assigned = 0;
        try
        {
            foreach (var stage in stages) { File.Move(stage.Item.Source, stage.Temporary, false); staged++; }
            foreach (var stage in stages) { File.Move(stage.Temporary, stage.Item.Target, false); assigned++; }
            SaveJournal(journalPath, new(completedState, stages));
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            for (var i = assigned - 1; i >= 0; i--)
                try { File.Move(stages[i].Item.Target, stages[i].Temporary, false); } catch (Exception ex) { rollbackErrors.Add(ex); }
            for (var i = staged - 1; i >= 0; i--)
                try { if (File.Exists(stages[i].Temporary)) File.Move(stages[i].Temporary, stages[i].Item.Source, false); } catch (Exception ex) { rollbackErrors.Add(ex); }
            try { SaveJournal(pendingJournal, new(rollbackErrors.Count == 0 ? "rolled-back" : "recovery-required", stages)); }
            catch (Exception journalError) { rollbackErrors.Add(journalError); }
            if (rollbackErrors.Count > 0) throw new AggregateException("重命名失败，部分文件需按恢复记录手动还原：" + pendingJournal, new[] { error }.Concat(rollbackErrors));
            throw new IOException("重命名失败，已还原原始文件名。", error);
        }
        try { File.Delete(pendingJournal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return changed;
    }

    public static RenameItem[] UndoRename(string journalPath)
    {
        lock (RenameWriteLock) return UndoRenameCore(journalPath);
    }

    private static RenameItem[] UndoRenameCore(string journalPath)
    {
        var journal = JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(journalPath)) ?? throw new InvalidDataException("恢复记录无效。");
        if (journal.State != "completed") throw new InvalidOperationException("没有可撤销的成功重命名记录。");
        var reverse = journal.Stages.Select(s => new RenameItem(s.Item.Target, s.Item.Source, s.Item.Length, s.Item.LastWriteUtc)).ToArray();
        return ApplyRenameCore(reverse, journalPath, "undone");
    }

    private static void SaveJournal(string path, RenameJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
    public sealed record RenameStage(RenameItem Item, string Temporary);
    public sealed record RenameJournal(string State, RenameStage[] Stages);

    public static bool CanUndo(string journalPath)
    {
        try { return JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(journalPath)) is { State: "completed", Stages.Length: > 0 }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    // Probe existing names without creating files; macOS volumes can be either case sensitive or insensitive.
    private static StringComparer DirectoryComparer(string folder, IEnumerable<string> sources)
    {
        if (OperatingSystem.IsWindows()) return StringComparer.OrdinalIgnoreCase;
        if (!OperatingSystem.IsMacOS()) return StringComparer.Ordinal;
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            for (var i = 0; i < name.Length; i++)
            {
                var changed = char.IsUpper(name[i]) ? char.ToLowerInvariant(name[i]) : char.ToUpperInvariant(name[i]);
                if (changed == name[i]) continue;
                var alternate = name[..i] + changed + name[(i + 1)..];
                if (!File.Exists(Path.Combine(folder, alternate))) break;
                if (!Directory.EnumerateFiles(folder).Any(file => StringComparer.Ordinal.Equals(Path.GetFileName(file), alternate)))
                    return StringComparer.OrdinalIgnoreCase;
            }
        }
        return StringComparer.Ordinal;
    }

}
