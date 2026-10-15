using System.Text.Json;

namespace AvaMedia.Core;

internal static class VideoSummaryRetry
{
    internal static string SourceKey(Job job)
    {
        object Stamp(string path) { var file = new FileInfo(path); return new { file.FullName, file.Length, file.LastWriteTimeUtc }; }
        return SummaryCheckpointStore.Hash(new { source = Stamp(job.Inputs.Single()), job.Options.VideoStreamIndex,
            options = job.Options.VideoSummary, external = job.Options.VideoSummary?.TranscriptSource == VideoTranscriptSource.External
                ? Stamp(job.Options.VideoSummary.SubtitleFile) : null });
    }
    internal static async Task<VideoSummaryStageSummary?> LoadAsync(Job job, CancellationToken ct = default)
    {
        try
        {
            var summary = await AiTaskResults.LoadAsync<VideoSummaryStageSummary>(VideoSummaryTaskState.StatePath(job), ct).ConfigureAwait(false);
            return summary?.Stages is { Length: 9 } && summary.Stages.Select(s => s.Stage.Id).SequenceEqual(VideoSummaryStageIds.All.Select(s => s.Id)) ? summary : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    internal static async Task<bool> CanRetryAsync(Job job, VideoSummaryStageSummary? summary, CancellationToken ct = default)
    {
        try
        {
            if (summary is not { IsLong: true, SignalHash.Length: > 0 } || summary.SourceKey != SourceKey(job)) return false;
            var boundary = VideoSummaryTaskState.Index(VideoSummaryStageIds.UnderstandSegments);
            if (summary.Stages.Take(boundary).Any(s => s.State is not (StageState.Done or StageState.Skipped))) return false;
            if (!summary.Stages.Skip(boundary).Any(s => s.State is StageState.Failed or StageState.Cancelled or StageState.Active or StageState.Paused)) return false;
            var store = new SummaryCheckpointStore(Path.Combine(AiPartialResults.SummaryFolder(job), "work"));
            return await store.ReadAsync<SummarySignals>("signals.json", summary.SignalHash, ct).ConfigureAwait(false) is not null
                && await store.ReadAvailableAsync<SegmentPlan>("segments.json", ct).ConfigureAwait(false) is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
}
