namespace AvaMedia.Core;

public sealed partial class VideoSummaryService
{
    public async Task<VideoSummaryResult> ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        var requested = job.VideoSummaryStartStageId;
        var previous = requested is null ? null : await VideoSummaryRetry.LoadAsync(job, ct).ConfigureAwait(false);
        using var stages = new VideoSummaryTaskState(job);
        job.VideoSummaryStages = stages;
        try
        {
            stages.Start(VideoSummaryStageIds.ReadVideo);
            Validate(job);
            await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
            var options = job.Options.VideoSummary!;
            var info = await engine.Probe(job.Inputs[0], ct, audioStreamIndex: options.AudioTrack).ConfigureAwait(false);
            if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("请选择有画面和有效时长的视频。");
            job.Duration = info.Duration;
            bool isLong = LongVideoSummaryOrchestrator.UsesLongPath(info.Duration, options);
            var environmentKey = SummaryCheckpointStore.Hash(engine.Settings.OnlineAi);
            stages.Configure(isLong, VideoSummaryRetry.SourceKey(job), environmentKey, info);
            stages.Complete(VideoSummaryStageIds.ReadVideo, $"{MediaTime.Format(info.Duration)} · {info.Width} × {info.Height}");
            bool retry = requested == VideoSummaryStageIds.UnderstandSegments && isLong && previous?.EnvironmentKey == environmentKey
                && await VideoSummaryRetry.CanRetryAsync(job, previous, ct).ConfigureAwait(false);
            if (retry) stages.Retain(previous!, requested!);
            else
            {
                if (requested is not null) job.AppendLog("视频总结阶段检查点不可复用，重新执行全部阶段。");
                var partial = AiPartialResults.SummaryFolder(job);
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
            }
            var result = isLong ? await ExecuteLongAsync(job, info, progress, ct).ConfigureAwait(false)
                : await ExecuteShortAsync(job, info, progress, ct).ConfigureAwait(false);
            stages.Finish(); job.VideoSummaryStartStageId = null; return result;
        }
        catch (Exception error) { stages.Finish(error); throw; }
    }
}
