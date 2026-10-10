namespace AvaMedia.Core;

public sealed record OrientationTaskOptions(string Format, string OutputFolder, bool OutputToSource, bool SettingName);
public sealed record OrientationTaskResult(VideoOrientationResult Detection, MediaInfo Info, long SourceLength, DateTime SourceWriteUtc);

/// <summary>Direction detection belongs to a queue job; opening a preview only reads its result.</summary>
public sealed class OrientationTaskService(IMediaEngine engine, IVideoOrientationDetector? detector = null) : IJobExecutor
{
    public static void Validate(Job job)
    {
        if (job.FeatureId != "rotate" || job.Inputs.Length != 1 || !VideoFormats.IsVideo(job.Inputs[0])
            || job.Options.Orientation is null || job.Options.Format != "json" || job.InputOptions is not null)
            throw new ArgumentException("方向检测任务须包含一个视频。");
        if (!File.Exists(job.Inputs[0])) throw new FileNotFoundException("源文件不存在", job.Inputs[0]);
        if (!BatchRename.PathComparer.Equals(Path.GetFullPath(job.Output), AiTaskResults.PathFor(job, "orientation")))
            throw new ArgumentException("方向检测结果路径无效。");
    }

    public async Task Execute(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job); job.OrientationResult = null;
        var source = new FileInfo(job.Inputs[0]); var length = source.Length; var write = source.LastWriteTimeUtc;
        var activity = new AiActivityReporter(value => job.Activity = value, "YuNet · 自动转正", "帧", ["读取视频", "检测方向", "保存结果"]);
        activity.Stage("读取视频"); progress(0);
        var info = await engine.Probe(job.Inputs[0], ct, job.Options.VideoStreamIndex, job.Options.AudioStreamIndex).ConfigureAwait(false);
        activity.Node("检测方向");
        var result = await (detector ?? new VideoOrientationDetector(engine)).DetectAsync(job.Inputs[0], info,
            new InlineProgress(value =>
            {
                job.ProgressDetail = $"检测方向 {value.CompletedFrames}/{value.TotalFrames} 帧";
                activity.Stage("检测方向", value.CompletedFrames, value.TotalFrames, "帧");
                progress(90d * value.CompletedFrames / Math.Max(1, value.TotalFrames));
            }), ct).ConfigureAwait(false);
        source.Refresh();
        if (!source.Exists || source.Length != length || source.LastWriteTimeUtc != write)
            throw new IOException("检测期间源视频发生变化，请重新检测。");
        var saved = new OrientationTaskResult(result, info, length, write);
        activity.Node("保存结果");
        await AiTaskResults.SaveAsync(job.Output, saved, ct).ConfigureAwait(false);
        job.OrientationResult = saved; job.Duration = info.Duration;
        job.ProgressDetail = result.IsCertain ? result.Description : "待确认 · 请手动选择方向";
        job.AppendLog(result.Reason); activity.Finish(job.ProgressDetail); progress(100);
    }
    private sealed class InlineProgress(Action<OrientationDetectionProgress> report) : IProgress<OrientationDetectionProgress>
    { public void Report(OrientationDetectionProgress value) => report(value); }
}
