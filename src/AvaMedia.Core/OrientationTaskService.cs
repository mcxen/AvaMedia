namespace AvaMedia.Core;

public sealed record OrientationTaskOptions(string Format, string OutputFolder, bool OutputToSource, bool SettingName);
public sealed record OrientationTaskResult(VideoOrientationResult Detection, MediaInfo Info, long SourceLength, DateTime SourceWriteUtc)
{ public bool IsPartial { get; init; } }

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
        var stages = new OrientationTaskState(job);
        job.OrientationStages = stages;
        try
        {
            stages.Start(OrientationStageIds.ReadVideo);
            await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
            Validate(job); job.OrientationResult = null; AiTaskResults.DeleteResult(job, "orientation");
            var source = new FileInfo(job.Inputs[0]); var length = source.Length; var write = source.LastWriteTimeUtc;
            var activity = new AiActivityReporter(value => job.Activity = value, "YuNet · 自动转正", "帧", ["读取视频", "检测方向", "保存结果"]);
            activity.Stage("读取视频"); progress(0);
            var info = await engine.Probe(job.Inputs[0], ct, job.Options.VideoStreamIndex, job.Options.AudioStreamIndex).ConfigureAwait(false);
            stages.Complete(OrientationStageIds.ReadVideo, $"{info.Width} × {info.Height} · {MediaEngine.Number(info.Duration)} 秒 · 视频轨 {info.VideoStreamIndex}");
            await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
            stages.Start(OrientationStageIds.Sample);
            activity.Node("检测方向");
            var deciding = false;
            void BeginDecision()
            {
                if (deciding) return;
                deciding = true;
                stages.Complete(OrientationStageIds.Sample, "方向采样结束");
                stages.Start(OrientationStageIds.Decide);
            }
            var result = await (detector ?? new VideoOrientationDetector(engine)).DetectAsync(job.Inputs[0], info,
                new InlineProgress(value =>
                {
                    if (value.PreviewResult is { } partial)
                        job.OrientationResult = new(partial, info, length, write) { IsPartial = true };
                    stages.Update(value);
                    job.ProgressDetail = $"检测方向 {value.CompletedFrames}/{value.TotalFrames} 帧次";
                    activity.Stage("检测方向", value.CompletedFrames, value.TotalFrames, "帧");
                    progress(90d * value.CompletedFrames / Math.Max(1, value.TotalFrames));
                    if (value.IsFinalDecision) BeginDecision();
                }), ct).ConfigureAwait(false);
            BeginDecision();
            await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
            stages.Runtime(result);
            source.Refresh();
            if (!source.Exists || source.Length != length || source.LastWriteTimeUtc != write)
                throw new IOException("检测期间源视频发生变化，请重新检测。");
            stages.Complete(OrientationStageIds.Decide, $"{result.Description} · {result.Reason}");
            // This job only proposes a correction. Confirmed rotation uses BatchRotate.CreateJobs.
            stages.Complete(OrientationStageIds.Export, result.Rotation switch
            {
                0 => "无需旋转，保留原文件",
                null => "无法确定方向，需手动确认；本任务仅检测，不导出",
                _ => "本任务仅检测方向；确认后由独立旋转任务导出"
            }, skipped: true);
            stages.Start(OrientationStageIds.SaveResult);
            var saved = new OrientationTaskResult(result, info, length, write);
            activity.Node("保存结果");
            await AiTaskResults.SaveAsync(job.Output, saved, ct).ConfigureAwait(false);
            job.OrientationResult = saved; job.Duration = info.Duration;
            job.ProgressDetail = result.IsCertain ? result.Description : "待确认 · 请手动选择方向";
            stages.Complete(OrientationStageIds.SaveResult, "已保存方向检测结果");
            stages.Finish(JobState.Completed);
            job.AppendLog(result.Reason); activity.Finish(job.ProgressDetail); progress(100);
        }
        catch (OperationCanceledException) { stages.Finish(JobState.Cancelled); throw; }
        catch (Exception error) { stages.Finish(JobState.Failed, error.Message); throw; }
    }
    private sealed class InlineProgress(Action<OrientationDetectionProgress> report) : IProgress<OrientationDetectionProgress>
    { public void Report(OrientationDetectionProgress value) => report(value); }
}
