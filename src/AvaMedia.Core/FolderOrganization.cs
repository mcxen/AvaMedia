using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record FolderOrganizationItem(FolderClassifiedFile File, string Target, string? ReportPath);
public sealed record FolderOrganizationProgress(string Source, string Target, int Completed, int Total, string? Error = null);
public sealed record FolderOrganizationResult(string JournalPath, int Completed, string[] Errors, bool Cancelled);

/// <summary>Copy-first organization, no overwrites, with a durable journal for each run.</summary>
public static class FolderOrganization
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    public sealed class Journal
    {
        public bool Move { get; set; }
        public List<JournalEntry> Entries { get; set; } = [];
    }
    public sealed class JournalEntry
    {
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";
        public string? ReportPath { get; set; }
        public string? ReportText { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public DateTime TargetWriteUtc { get; set; }
        public string Stage { get; set; } = "planned";
    }

    public static FolderOrganizationItem[] Preview(IEnumerable<FolderClassifiedFile> files, string outputFolder,
        bool splitTypes, bool writeText)
    {
        var root = Path.GetFullPath(outputFolder);
        EnsureRegularDirectory(root);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(BatchRename.PathComparer);
        return files.Where(file => seen.Add(file.Media.Path)).Select(file =>
        {
            CheckSource(file.Media);
            var folder = DestinationFolder(file, root, splitTypes);
            EnsureRegularDirectory(folder);
            var stem = Path.GetFileNameWithoutExtension(file.Media.Path);
            var extension = Path.GetExtension(file.Media.Path);
            var target = Path.Combine(folder, stem + extension);
            var number = 2;
            bool Taken(string candidate) => File.Exists(candidate) || Directory.Exists(candidate) || reserved.Contains(candidate)
                || writeText && (File.Exists(candidate + ".tags.txt") || Directory.Exists(candidate + ".tags.txt") || reserved.Contains(candidate + ".tags.txt"));
            while (Taken(target)) target = Path.Combine(folder, stem + " (" + number++ + ")" + extension);
            if (BatchRename.PathComparer.Equals(target, file.Media.Path)) throw new IOException("分类目标不能与源文件相同。");
            if (!FolderClassification.IsWithin(target, root)) throw new IOException("分类目标超出输出目录。");
            reserved.Add(target);
            var report = writeText ? target + ".tags.txt" : null;
            if (report is not null) reserved.Add(report);
            return new FolderOrganizationItem(file, target, report);
        }).ToArray();
    }

    public static string DestinationFolder(FolderClassifiedFile file, string outputFolder, bool splitTypes)
    {
        var root = Path.GetFullPath(outputFolder);
        var folder = root;
        if (splitTypes) folder = Path.Combine(folder, VideoFormats.IsVideo(file.Media.Path) ? "视频" : "图片");
        foreach (var decision in file.Decisions)
        {
            BatchRename.ValidateRenameKeyword(decision.Name);
            BatchRename.ValidateRenameKeyword(decision.CategoryName);
            if (file.Decisions.Count > 1) folder = Path.Combine(folder, decision.Name);
            folder = Path.Combine(folder, decision.CategoryName);
        }
        if (!FolderClassification.IsWithin(folder, root)) throw new IOException("分类目标超出输出目录。");
        return folder;
    }

    public static async Task<FolderOrganizationResult> ExecuteAsync(IReadOnlyList<FolderOrganizationItem> plan, bool move,
        string journalPath, IProgress<FolderOrganizationProgress>? progress, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(journalPath)) throw new IOException("分类操作记录已存在。");
            var journal = new Journal { Move = move };
            var errors = new List<string>();
            var completed = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
            await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
            foreach (var item in plan)
            {
                if (ct.IsCancellationRequested) break;
                var media = item.File.Media;
                JournalEntry? entry = null;
                try
                {
                    CheckSource(media);
                    EnsureRegularDirectory(Path.GetDirectoryName(item.Target)!);
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                    if (item.ReportPath is not null && (File.Exists(item.ReportPath) || Directory.Exists(item.ReportPath)))
                        throw new IOException("标签报告目标已存在，请重新预览。");
                    entry = new() { Source = media.Path, Target = item.Target, ReportPath = item.ReportPath,
                        ReportText = item.ReportPath is null ? null : TextReport(item.File), Length = media.Length, LastWriteUtc = media.LastWriteUtc };
                    journal.Entries.Add(entry);
                    await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    await CopyAsync(media.Path, item.Target, media.Length, media.LastWriteUtc, ct).ConfigureAwait(false);
                    entry.TargetWriteUtc = File.GetLastWriteTimeUtc(item.Target);
                    entry.Stage = "copied";
                    // Persist ownership of the copy before any source can be removed.
                    await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    if (item.ReportPath is not null)
                    {
                        await WriteNewTextAsync(item.ReportPath, entry.ReportText!, ct).ConfigureAwait(false);
                        entry.Stage = "reported";
                        await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    }
                    ct.ThrowIfCancellationRequested();
                    if (move)
                    {
                        CheckSource(media);
                        File.Delete(media.Path);
                    }
                    entry.Stage = "completed";
                    await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    progress?.Report(new(media.Path, item.Target, ++completed, plan.Count));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add(media.Path + " · " + error.Message);
                    // Copies which were published stay recorded even if reporting or source deletion failed.
                    if (entry is not null && entry.Stage == "planned" && !File.Exists(entry.Target)) journal.Entries.Remove(entry);
                    await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    progress?.Report(new(media.Path, item.Target, completed, plan.Count, error.Message));
                }
            }
            return new(journalPath, completed, errors.ToArray(), ct.IsCancellationRequested);
        }
        finally { WriteGate.Release(); }
    }

    public static bool CanUndo(string? journalPath)
    {
        try { return journalPath is not null && ReadJournal(journalPath).Entries.Any(entry => entry.Stage is "copied" or "reported" or "completed" or "undoing"); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    public static async Task<FolderOrganizationResult> UndoAsync(string journalPath,
        IProgress<FolderOrganizationProgress>? progress, CancellationToken ct)
    {
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var journal = ReadJournal(journalPath);
            var entries = journal.Entries.Where(entry => entry.Stage is "copied" or "reported" or "completed" or "undoing").Reverse().ToArray();
            var errors = new List<string>();
            var completed = 0;
            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    EnsureRegularDirectory(Path.GetDirectoryName(entry.Target)!);
                    if (entry.Stage == "undoing" && !File.Exists(entry.Target))
                    {
                        if (journal.Move) { EnsureRegularDirectory(Path.GetDirectoryName(entry.Source)!); CheckFile(entry.Source, entry.Length, entry.LastWriteUtc); }
                        entry.Stage = "undone";
                        await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                        progress?.Report(new(entry.Target, entry.Source, ++completed, entries.Length));
                        continue;
                    }
                    CheckFile(entry.Target, entry.Length, entry.TargetWriteUtc);
                    // A changed report must not be removed by undo.
                    if (entry.ReportPath is not null && File.Exists(entry.ReportPath))
                    {
                        if ((File.GetAttributes(entry.ReportPath) & FileAttributes.ReparsePoint) != 0
                            || await File.ReadAllTextAsync(entry.ReportPath, ct).ConfigureAwait(false) != entry.ReportText)
                            throw new IOException("标签报告已修改，无法撤销。");
                    }
                    if (journal.Move)
                    {
                        EnsureRegularDirectory(Path.GetDirectoryName(entry.Source)!);
                        if (File.Exists(entry.Source)) CheckFile(entry.Source, entry.Length, entry.LastWriteUtc);
                        else await CopyAsync(entry.Target, entry.Source, entry.Length, entry.TargetWriteUtc, ct).ConfigureAwait(false);
                        File.SetLastWriteTimeUtc(entry.Source, entry.LastWriteUtc);
                    }
                    ct.ThrowIfCancellationRequested();
                    entry.Stage = "undoing";
                    await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    if (entry.ReportPath is not null && File.Exists(entry.ReportPath)) File.Delete(entry.ReportPath);
                    File.Delete(entry.Target);
                    entry.Stage = "undone";
                    await SaveJournalAsync(journalPath, journal).ConfigureAwait(false);
                    progress?.Report(new(entry.Target, entry.Source, ++completed, entries.Length));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    errors.Add(entry.Target + " · " + error.Message);
                    progress?.Report(new(entry.Target, entry.Source, completed, entries.Length, error.Message));
                }
            }
            return new(journalPath, completed, errors.ToArray(), ct.IsCancellationRequested);
        }
        finally { WriteGate.Release(); }
    }

    public static string TextReport(FolderClassifiedFile file)
    {
        var text = new StringBuilder().AppendLine(Path.GetFileName(file.Media.Path)).AppendLine();
        foreach (var decision in file.Decisions)
        {
            text.Append(decision.Name).Append(": ").Append(decision.CategoryName);
            if (decision.Manual) text.Append(" · 人工确认");
            else
            {
                text.Append(FormattableString.Invariant($" · 采样一致率 {decision.Agreement:P0}"));
                if (decision.Seconds is { } seconds) text.Append(FormattableString.Invariant($" · {seconds:0.00}s"));
            }
            foreach (var score in decision.Scores)
                text.AppendLine().Append(FormattableString.Invariant($"  {score.Name}: {score.Similarity:0.000} · 命中 {score.MatchedFrames}/{decision.Frames.Count} 帧"));
            text.AppendLine().AppendLine(decision.Evidence);
        }
        text.AppendLine().AppendLine("标签: " + string.Join(" · ", file.Tags));
        text.AppendLine($"视频采样帧数: {file.Media.SampledFrames}");
        text.AppendLine("分类来自采样识别，需人工核对。");
        return text.ToString();
    }

    private static Journal ReadJournal(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("分类操作记录过大。");
        return JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("分类操作记录无效。");
    }

    private static async Task SaveJournalAsync(string path, Journal journal)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, journal, Json).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task CopyAsync(string source, string target, long length, DateTime modified, CancellationToken ct)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(true);
                CheckFile(source, length, modified);
                if (output.Length != length) throw new IOException("文件复制不完整。");
            }
            File.SetLastWriteTimeUtc(temporary, modified);
            ct.ThrowIfCancellationRequested();
            EnsureRegularDirectory(Path.GetDirectoryName(target)!);
            File.Move(temporary, target); // Publish without replacing an existing file.
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteNewTextAsync(string path, string text, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CheckSource(MediaTagResult media) => CheckFile(media.Path, media.Length, media.LastWriteUtc);
    private static void CheckFile(string path, long length, DateTime modified)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0
            || file.Length != length || file.LastWriteTimeUtc != modified)
            throw new IOException("文件已改变或不存在，请重新分析：" + path);
    }
    private static void EnsureRegularDirectory(string folder)
    {
        for (var path = Path.GetFullPath(folder); path is not null; path = Path.GetDirectoryName(path))
        {
            if (File.Exists(path)) throw new IOException("分类目录被文件占用：" + path);
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("分类目录不能经过符号链接：" + path);
        }
    }
}
