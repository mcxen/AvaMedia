using System.Globalization;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record RenameRules(string Pattern = "{name}_{index}", string Prefix = "", string Suffix = "", string Find = "", string Replace = "", int FirstIndex = 1, int Digits = 3);
public sealed record RenameItem(string Source, string Target, long Length, DateTime LastWriteUtc);
public sealed record ContactSheetOptions(int Columns = 3, int Rows = 3, int CellWidth = 320, int CellHeight = 180, int SheetsPerVideo = 1, string Format = "jpg", bool Timestamps = true, double StartSeconds = 0, double EndSeconds = 0);
public sealed record ContactSheetProgress(string Input, int Sheet, double Percent, string Message);

/// <summary>Independent batch file and contact-sheet tools. Renaming never overwrites a file.</summary>
public static class BatchVideoTools
{
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".m4v", ".ts", ".mts", ".m2ts", ".vob", ".3gp", ".ogv", ".asf" };

    public static string[] CollectVideos(IEnumerable<string> paths, bool recursive)
    {
        var result = new HashSet<string>(PathComparer);
        foreach (var input in paths)
        {
            var path = Path.GetFullPath(input);
            if (File.Exists(path))
            {
                if (VideoExtensions.Contains(Path.GetExtension(path))) result.Add(path);
            }
            else if (Directory.Exists(path))
            {
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden
                };
                foreach (var file in Directory.EnumerateFiles(path, "*", enumeration))
                    if (VideoExtensions.Contains(Path.GetExtension(file))) result.Add(Path.GetFullPath(file));
            }
        }
        return result.OrderBy(p => p, PathComparer).ToArray();
    }

    public static RenameItem[] PreviewRename(IEnumerable<string> paths, RenameRules rules)
    {
        if (rules.FirstIndex < 0 || rules.Digits is < 1 or > 12) throw new ArgumentException("起始序号不能为负，序号位数须在 1–12 之间。");
        if (string.IsNullOrWhiteSpace(rules.Pattern)) throw new ArgumentException("命名模板不能为空。");
        var result = new List<RenameItem>();
        foreach (var (path, index) in paths.Select(Path.GetFullPath).Distinct(PathComparer).Select((p, i) => (p, i)))
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("源文件不存在，请刷新文件列表。", path);
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(rules.Find)) name = name.Replace(rules.Find, rules.Replace, StringComparison.Ordinal);
            var stem = rules.Prefix + rules.Pattern.Replace("{name}", name, StringComparison.Ordinal)
                .Replace("{index}", checked(rules.FirstIndex + index).ToString("D" + rules.Digits, CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{parent}", info.Directory?.Name ?? "", StringComparison.Ordinal) + rules.Suffix;
            ValidateStem(stem);
            var target = Path.Combine(info.DirectoryName!, stem + info.Extension);
            result.Add(new(path, target, info.Length, info.LastWriteTimeUtc));
        }
        ValidateRenamePlan(result);
        return result.ToArray();
    }

    private static void ValidateStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem) || stem.Length > 220 || stem.EndsWith('.') || stem.EndsWith(' ') || stem is "." or ".." || stem.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException($"文件名无效：{stem}");
        var first = stem.Split('.')[0].ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" || first.Length == 4 && (first.StartsWith("COM") || first.StartsWith("LPT")) && first[3] is >= '0' and <= '9')
            throw new ArgumentException($"不能使用系统保留文件名：{stem}");
    }

    public static void ValidateRenamePlan(IReadOnlyList<RenameItem> items)
    {
        var sources = new HashSet<string>(items.Select(i => i.Source), PathComparer);
        var targets = new HashSet<string>(PathComparer);
        if (sources.Count != items.Count) throw new ArgumentException("重命名列表包含重复源文件。");
        foreach (var item in items)
        {
            if (!targets.Add(item.Target)) throw new IOException("重命名后会产生同名文件：" + item.Target);
            if (!PathComparer.Equals(Path.GetDirectoryName(item.Source), Path.GetDirectoryName(item.Target))) throw new ArgumentException("批量重命名仅支持原目录内改名。");
            ValidateStem(Path.GetFileNameWithoutExtension(item.Target));
            if (Directory.Exists(item.Target) || File.Exists(item.Target) && !sources.Contains(item.Target)) throw new IOException("目标名称已存在：" + item.Target);
            var info = new FileInfo(item.Source);
            if (!info.Exists || info.Length != item.Length || info.LastWriteTimeUtc != item.LastWriteUtc) throw new IOException("预览后源文件已改变，请重新预览：" + item.Source);
        }
    }

    /// <summary>Stages every changed source before assigning targets, permitting name swaps. On failure rolls back.</summary>
    public static RenameItem[] ApplyRename(IReadOnlyList<RenameItem> plan, string journalPath)
        => ApplyRenameCore(plan, journalPath, "completed");

    private static RenameItem[] ApplyRenameCore(IReadOnlyList<RenameItem> plan, string journalPath, string completedState)
    {
        ValidateRenamePlan(plan);
        var changed = plan.Where(i => !StringComparer.Ordinal.Equals(i.Source, i.Target)).ToArray();
        if (changed.Length == 0) return [];
        var stages = changed.Select(i => new RenameStage(i, Path.Combine(Path.GetDirectoryName(i.Source)!, ".avamedia-rename-" + Guid.NewGuid().ToString("N") + ".tmp"))).ToArray();
        var pendingJournal = journalPath + ".pending";
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
            SaveJournal(pendingJournal, new(rollbackErrors.Count == 0 ? "rolled-back" : "recovery-required", stages));
            if (rollbackErrors.Count > 0) throw new AggregateException("重命名失败，部分文件需按恢复记录手动还原：" + pendingJournal, new[] { error }.Concat(rollbackErrors));
            throw new IOException("重命名失败，已还原原始文件名。", error);
        }
        try { File.Delete(pendingJournal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return changed;
    }

    public static RenameItem[] UndoRename(string journalPath)
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

    public static void ValidateContactSheet(ContactSheetOptions o)
    {
        if (o.Columns is < 1 or > 10 || o.Rows is < 1 or > 10 || o.SheetsPerVideo is < 1 or > 100) throw new ArgumentException("行列数须在 1–10，每个视频拼图数量须在 1–100。");
        if (o.CellWidth is < 64 or > 1920 || o.CellHeight is < 64 or > 1080 || (long)o.CellWidth * o.CellHeight * o.Rows * o.Columns > 40000000)
            throw new ArgumentException("单格尺寸须在 64–1920 × 64–1080，整张拼图不能超过 4000 万像素。");
        if (o.Format is not ("jpg" or "png")) throw new ArgumentException("截图格式须为 JPG 或 PNG。");
        if (!double.IsFinite(o.StartSeconds) || !double.IsFinite(o.EndSeconds) || o.StartSeconds < 0 || o.EndSeconds < 0 || o.EndSeconds > 0 && o.EndSeconds <= o.StartSeconds)
            throw new ArgumentException("结束时间必须晚于开始时间；结束为 0 表示视频末尾。");
    }

    public static async Task<string[]> GenerateContactSheets(IMediaEngine engine, string input, string outputFolder, ContactSheetOptions options, IProgress<ContactSheetProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateContactSheet(options);
        var info = await engine.Probe(input, ct);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new InvalidDataException("文件不含可截图的视频或无法读取时长。");
        var (videoDuration, frameRate) = VideoTiming(info);
        var end = options.EndSeconds > 0 ? Math.Min(options.EndSeconds, videoDuration) : videoDuration;
        if (options.StartSeconds >= end) throw new ArgumentException("开始时间超出视频时长。");
        Directory.CreateDirectory(outputFolder);
        var scratch = Path.Combine(Path.GetTempPath(), "AvaMedia-contactsheet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var outputs = new List<string>();
        var count = checked(options.Rows * options.Columns);
        var total = checked(count * options.SheetsPerVideo);
        try
        {
            for (var sheet = 0; sheet < options.SheetsPerVideo; sheet++)
            {
                for (var cell = 0; cell < count; cell++)
                {
                    ct.ThrowIfCancellationRequested();
                    var index = sheet * count + cell;
                    var requested = options.StartSeconds + (end - options.StartSeconds) * (index + .5) / total;
                    // Even very short/low-frame-rate clips must fill every cell: avoid seeking past their last frame.
                    var seconds = Math.Min(requested, Math.Max(0, videoDuration - Math.Max(.08, 1 / frameRate)));
                    var frame = Path.Combine(scratch, $"frame-{cell:0000}.png");
                    // The text file avoids quoting user paths inside a filter expression.
                    var vf = $"scale={options.CellWidth}:{options.CellHeight}:force_original_aspect_ratio=decrease,pad={options.CellWidth}:{options.CellHeight}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1";
                    if (options.Timestamps)
                    {
                        var stamp = Path.Combine(scratch, $"stamp-{cell:0000}.txt");
                        await File.WriteAllTextAsync(stamp, TimeSpan.FromSeconds(seconds).ToString(options.CellWidth >= 140 ? @"hh\:mm\:ss\.fff" : @"hh\:mm\:ss"), ct);
                        vf += $",drawtext=textfile='{FilterPath(stamp)}':fontsize={Math.Max(12, options.CellWidth / 22)}:fontcolor=white:box=1:boxcolor=black@0.65:boxborderw=4:x=w-tw-8:y=h-th-8";
                    }
                    var captured = await ProcessRunner.Run(engine.FFmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", MediaEngine.Number(seconds), "-i", input, "-an", "-frames:v", "1", "-vf", vf, "-update", "1", frame], ct);
                    if (captured.ExitCode != 0 || !File.Exists(frame) || new FileInfo(frame).Length == 0) throw new InvalidOperationException("抽帧失败：" + captured.Error);
                    progress?.Report(new(input, sheet + 1, (index + 1) * 95d / total, $"抽帧 {index + 1}/{total}"));
                }
                var target = MediaEngine.UniqueOutput(outputFolder, Path.GetFileNameWithoutExtension(input) + $"-grid-{options.Columns}x{options.Rows}-{sheet + 1:000}", options.Format);
                var stagedOutput = Path.Combine(scratch, "sheet." + options.Format);
                List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-framerate", "1", "-start_number", "0", "-i", Path.Combine(scratch, "frame-%04d.png"), "-vf", $"tile={options.Columns}x{options.Rows}:nb_frames={count}:padding=6:margin=12:color=0x222222", "-frames:v", "1", "-update", "1"];
                if (options.Format == "jpg") args.AddRange(["-q:v", "2"]);
                args.Add(stagedOutput);
                var assembled = await ProcessRunner.Run(engine.FFmpeg, args, ct);
                if (assembled.ExitCode != 0 || !File.Exists(stagedOutput)) throw new InvalidOperationException("拼图失败：" + assembled.Error);
                ct.ThrowIfCancellationRequested();
                File.Move(stagedOutput, target, false);
                outputs.Add(target);
                progress?.Report(new(input, sheet + 1, (sheet + 1) * 100d / options.SheetsPerVideo, "已生成：" + target));
            }
            return outputs.ToArray();
        }
        finally
        {
            // This directory is a newly allocated task scratch path, never an input/output folder.
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string FilterPath(string path) => path.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);

    private static (double Duration, double FrameRate) VideoTiming(MediaInfo info)
    {
        double duration = info.Duration, frameRate = 25;
        try
        {
            using var json = JsonDocument.Parse(info.RawJson);
            var video = json.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
            if (video.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var streamDuration) && streamDuration > 0)
                duration = Math.Min(duration, streamDuration);
            if (video.TryGetProperty("avg_frame_rate", out var rate))
            {
                var parts = (rate.GetString() ?? "").Split('/');
                if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0)
                    frameRate = numerator / denominator;
            }
        }
        catch (JsonException) { }
        return (duration, frameRate);
    }
}
