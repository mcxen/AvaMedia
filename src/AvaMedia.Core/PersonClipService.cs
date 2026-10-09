using System.Text;
using System.Diagnostics;

namespace AvaMedia.Core;

public sealed record PersonClipTaskOptions
{
    public PersonClipOptions Detection { get; init; } = new();
    public string ExportPreset { get; init; } = QuickClipBatch.DefaultPreset;
    public PersonClipTaskOptions Copy() => this with
    {
        Detection = Detection with { DetectorIds = Detection.DetectorIds?.ToArray(), ExcludedRanges = Detection.ExcludedRanges?.ToArray() }
    };
    public void Validate()
    {
        Detection.Validate();
        if (!QuickClipBatch.Presets.Contains(ExportPreset)) throw new ArgumentException("请选择有效的导出格式。");
    }
}

/// <summary>One queue job owns detection and exports its accepted ranges to one output file.</summary>
public sealed class PersonClipService(MediaEngine engine)
{
    private static readonly SemaphoreSlim DetectionGate = new(1, 1);

    public static void Validate(Job job)
    {
        if (job.Inputs.Length != 1 || !VideoFormats.IsVideo(job.Inputs[0]) || job.InputOptions is not null)
            throw new ArgumentException("每个保留有人片段任务处理一个视频。");
        var spec = job.Options.PersonClip ?? throw new ArgumentException("缺少人物检测参数。");
        spec.Validate();
        if (job.Options.Start != 0 || job.Options.End != 0 || MediaEngine.HasFilters(job.Options))
            throw new ArgumentException("人物检测使用源视频时间轴，请在免检测区间中标记要排除的部分。");
        var export = QuickClipBatch.ResolveOptions(job.Inputs[0], spec.ExportPreset, job.Options);
        if (export.Format != job.Options.Format || export.CopyStreams != job.Options.CopyStreams)
            throw new ArgumentException("人物检测的导出格式与队列参数不一致。");
        export.PersonClip = null;
        MediaEngine.Validate(new() { FeatureId = "clip", Inputs = job.Inputs, Output = job.Output, Options = export });
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job);
        var spec = job.Options.PersonClip!;
        var source = new FileInfo(job.Inputs[0]); var bytes = source.Length; var modified = source.LastWriteTimeUtc;
        var activity = new AiActivityReporter(value => job.Activity = value, "保留有人片段", "个片段", ["人物检测", "导出片段"]);
        activity.Stage("等待人物检测"); job.ProgressDetail = "等待人物检测"; progress(0);
        PersonClipResult result;
        await DetectionGate.WaitAsync(ct).ConfigureAwait(false);
        var analysisClock = Stopwatch.StartNew();
        try
        {
            var latest = 0d;
            result = await new PersonClipAnalysis(engine).AnalyzeAsync(job.Inputs[0], spec.Detection,
                new InlineProgress(value =>
                {
                    if (value.Activity is { } observed) activity.Observe(observed);
                    job.ProgressDetail = value.Stage;
                    var fraction = value.Duration > 0 ? Math.Clamp(value.Seconds / value.Duration, 0, 1) * 80 : 0;
                    if (value.Stage == "细化片段边界" && value.Activity is { Current: { } current, Total: > 0 } detail)
                        fraction = 80 + 9 * Math.Clamp(current / detail.Total!.Value, 0, 1);
                    latest = Math.Max(latest, fraction); progress(latest);
                }), ct).ConfigureAwait(false);
        }
        finally { DetectionGate.Release(); }
        analysisClock.Stop();
        ct.ThrowIfCancellationRequested(); CheckSource();
        job.Duration = result.Info.Duration;
        job.AppendLog(result.FromCache ? $"复用检测缓存：{result.Segments.Count} 个片段，本次无需模型计算。"
            : $"人物检测：模型计算 {result.InferredFrames} 帧；复用 {result.ReusedFrames} 帧；黑灯排除 {result.DarkFrames} 帧；无画面排除 {result.BlankFrames} 帧；免检测 {MediaTime.Format(result.ExcludedSeconds)}；{result.Backend}");
        job.AppendLog($"人物检测耗时：{MediaEngine.Number(analysisClock.Elapsed.TotalSeconds)} 秒");
        foreach (var detector in result.FromCache ? [] : result.Detectors)
            if (detector.BackendSelectionReason is { } reason) job.AppendLog($"{detector.Name}: {reason}");
        foreach (var segment in result.Segments) job.AppendLog($"保留 {MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}");
        if (result.Segments.Count == 0)
        {
            job.ProgressDetail = "没有可保留片段，已跳过输出";
            activity.Finish(job.ProgressDetail); job.AppendLog(job.ProgressDetail); progress(100); return;
        }
        activity.Node("导出片段"); activity.Stage("导出片段", 0, 100, "%"); progress(90);
        var exportClock = Stopwatch.StartNew();
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!, ".AvaMedia-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var output = Path.Combine(folder, "output." + job.Options.Format);
            var export = job.Options.Clone(); export.PersonClip = null;
            if (export.CopyStreams || result.Segments.Count <= 64)
                await ExportAsync(result.Segments, output, value => Advance(value)).ConfigureAwait(false);
            else
            {
                // Bound re-encoding's simultaneous inputs; source stream-copy already processes ranges serially.
                var groups = result.Segments.Chunk(64).ToArray();
                var manifest = new StringBuilder("ffconcat version 1.0\n");
                for (var index = 0; index < groups.Length; index++)
                {
                    var current = index; var name = $"part-{index:000}." + export.Format;
                    await ExportAsync(groups[index], Path.Combine(folder, name), value => Advance(85 * (current + value / 100) / groups.Length)).ConfigureAwait(false);
                    manifest.Append("file '").Append(name).Append("'\n");
                }
                var list = Path.Combine(folder, "parts.ffconcat");
                await File.WriteAllTextAsync(list, manifest.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
                List<string> args = ["-v", "error", "-nostdin", "-n", "-f", "concat", "-safe", "0", "-i", list,
                    "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", "-progress", "pipe:1", "-nostats"];
                VideoFormats.AppendMuxerArguments(args, export.Format); args.Add(output);
                var duration = result.Segments.Sum(segment => segment.End - segment.Start);
                var joined = await ProcessRunner.Run(engine.FFmpeg, args, ct, line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var microseconds))
                        Advance(85 + 15 * Math.Clamp(microseconds / 1_000_000d / duration, 0, 1));
                }).ConfigureAwait(false);
                job.AppendLog(joined.Error);
                if (joined.ExitCode != 0) throw new InvalidDataException(joined.Error);
            }
            CheckSource(); ct.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new InvalidDataException("保留片段未生成有效输出。");
            File.Move(output, job.Output);
            job.AppendLog($"导出片段耗时：{MediaEngine.Number(exportClock.Elapsed.TotalSeconds)} 秒");
            job.ProgressDetail = $"保留 {result.Segments.Count} 个片段";
            activity.Result(job.ProgressDetail, result.Segments.Count); activity.Finish("导出完成"); progress(100);

            async Task ExportAsync(IReadOnlyList<ConversionOptions> ranges, string target, Action<double> advance)
            {
                var parts = ranges.Select(range =>
                {
                    var part = export.Clone(); part.Start = range.Start; part.End = range.End; return part;
                }).ToList();
                var draft = new Job { FeatureId = parts.Count == 1 ? "clip" : "join", Output = target,
                    Inputs = Enumerable.Repeat(job.Inputs[0], parts.Count).ToArray(),
                    Options = parts.Count == 1 ? parts[0] : export.Clone(), InputOptions = parts.Count == 1 ? null : parts };
                try
                {
                    void Publish(double value) { job.ProgressDetail = "导出片段"; advance(value); }
                    if (SourceClipCopy.IsJoined(draft))
                    {
                        var infos = Enumerable.Repeat(result.Info, parts.Count).ToArray();
                        draft.Duration = MediaEngine.ValidateEdits(draft, infos);
                        await SourceClipCopy.ExecuteAsync(engine, draft, infos, Publish, ct).ConfigureAwait(false);
                    }
                    else await engine.Execute(draft, Publish, ct).ConfigureAwait(false);
                }
                finally
                {
                    job.AppendLog(await draft.ReadLogAsync().ConfigureAwait(false));
                    draft.Log = "";
                }
            }
            void Advance(double value) { activity.Advance(value, 100, "%"); progress(90 + Math.Clamp(value, 0, 100) * .099); }
        }
        finally { Directory.Delete(folder, true); }

        void CheckSource()
        {
            source.Refresh();
            if (!source.Exists || source.Length != bytes || source.LastWriteTimeUtc != modified)
                throw new IOException("检测期间源视频发生变化，请重新运行任务。");
        }
    }

    private sealed class InlineProgress(Action<PersonClipProgress> report) : IProgress<PersonClipProgress>
    { public void Report(PersonClipProgress value) => report(value); }
}
