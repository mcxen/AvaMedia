using System.Text.Json;

namespace AvaMedia.Core;

internal sealed record PersonClipRetryPlan(string? StageId, PersonClipStageSummary? Summary = null,
    PersonClipDetectionCheckpoint? Detection = null, PersonDetectionTaskResult? Result = null, string FallbackReason = "");

internal interface IPersonClipModelAvailability
{
    string Stamp(string id);
    Task<bool> IsInstalledAsync(string id, CancellationToken ct);
}
internal sealed class PersonClipModelAvailability(ModelStore store) : IPersonClipModelAvailability
{
    public string Stamp(string id) => store.InstalledStamp(id);
    public Task<bool> IsInstalledAsync(string id, CancellationToken ct) => store.IsInstalledAsync(id, ct: ct);
}

internal static class PersonClipRetry
{
    internal static bool SourceMatches(Job job, PersonClipStageSummary summary)
    {
        try
        {
            var source = new FileInfo(job.Inputs.Single());
            return source.Exists && Path.GetFullPath(source.FullName) == Path.GetFullPath(summary.SourcePath)
                && source.Length == summary.SourceLength && source.LastWriteTimeUtc == summary.SourceWriteUtc
                && summary.OptionsKey == PersonClipTaskState.OptionsKey(job);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
    internal static bool SourceMatches(Job job, PersonDetectionTaskResult result)
    {
        try
        {
            var source = new FileInfo(job.Inputs.Single());
            return source.Exists && source.Length == result.SourceLength && source.LastWriteTimeUtc == result.SourceWriteUtc
                && Path.GetFullPath(source.FullName) == Path.GetFullPath(result.Result.Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
    internal static async Task<T?> LoadAsync<T>(string path, CancellationToken ct = default)
    {
        try { return await AiTaskResults.LoadAsync<T>(path, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return default; }
    }
    internal static async Task<PersonClipRetryPlan> ResolveAsync(Job job, string? id, ModelStore store, CancellationToken ct)
        => await ResolveAsync(job, id, new PersonClipModelAvailability(store), ct).ConfigureAwait(false);

    internal static async Task<PersonClipRetryPlan> ResolveAsync(Job job, string? id, IPersonClipModelAvailability models, CancellationToken ct)
    {
        if (id is null) return new(null);
        if (id is not (PersonClipStageIds.Detect or PersonClipStageIds.Refine or PersonClipStageIds.Export))
            throw new ArgumentException("不支持从该阶段重试。", nameof(id));
        var summary = await LoadAsync<PersonClipStageSummary>(AiTaskResults.PathFor(job, "people-stages"), ct).ConfigureAwait(false);
        if (summary?.Info is null || summary.Stages is null || summary.ModelStamps is null || summary.Stages.Length != PersonClipStageIds.All.Count || !summary.Stages.Select(s => s.Status.Stage.Id).SequenceEqual(PersonClipStageIds.All.Select(s => s.Id)) || !SourceMatches(job, summary))
            return new(null, FallbackReason: "源文件变化或读取视频产出缺失，回退全量执行。");
        bool Done(string stage) => summary.Stages.Any(item => item.Status.Stage.Id == stage && item.Status.State is (StageState.Done or StageState.Skipped));
        if (!Done(PersonClipStageIds.ReadVideo) || !Done(PersonClipStageIds.PrepareModel))
            return new(null, FallbackReason: "前序阶段产出缺失，回退全量执行。");
        if (id != PersonClipStageIds.Export)
            foreach (var model in PersonClipTaskState.RequiredModels(job))
                if (!summary.ModelStamps.TryGetValue(model, out var stamp) || stamp == "missing" || models.Stamp(model) != stamp
                    || !await models.IsInstalledAsync(model, ct).ConfigureAwait(false))
                    return new(null, FallbackReason: "模型安装状态变化，回退全量执行。");
        if (id == PersonClipStageIds.Detect) return new(id, summary);
        if (!Done(PersonClipStageIds.Detect)) return new(null, FallbackReason: "完整检测产出缺失，回退全量执行。");
        if (id == PersonClipStageIds.Refine)
        {
            var detection = await LoadAsync<PersonClipDetectionCheckpoint>(AiTaskResults.PathFor(job, "people-detection"), ct).ConfigureAwait(false);
            if (detection is null || !ValidResult(job, detection.Result, summary.Info) || detection.Result.IsPartial
                || detection.Samples is not { Length: > 0 } || detection.Candidates is null
                || detection.Samples.Any(sample => sample is null || !double.IsFinite(sample.Seconds) || sample.Seconds < 0 || sample.Seconds > summary.Info.Duration)
                || !detection.Samples.Select(sample => sample.Seconds).SequenceEqual(detection.Samples.Select(sample => sample.Seconds).Order())
                || detection.Candidates.Any(c => c is null || c.Start < 0 || c.End <= c.Start || c.End > detection.Samples.Length))
                return new(null, FallbackReason: "完整采样判定缺失，回退全量执行。");
            return new(id, summary, detection);
        }
        var result = job.PersonDetectionResult ?? await LoadAsync<PersonDetectionTaskResult>(AiTaskResults.PathFor(job, "people"), ct).ConfigureAwait(false);
        if (!Done(PersonClipStageIds.Refine) || result is null || result.Result.IsPartial || !ValidResult(job, result.Result, summary.Info) || !SourceMatches(job, result))
            return new(null, FallbackReason: "最终片段产出缺失，回退全量执行。");
        return new(id, summary, Result: result);
    }
    private static bool ValidResult(Job job, PersonClipResult? result, MediaInfo info)
    {
        if (result?.Info is null || result.Segments is null || result.Info.Duration != info.Duration
            || !BatchRename.PathComparer.Equals(Path.GetFullPath(result.Path), Path.GetFullPath(job.Inputs[0]))) return false;
        var end = 0d;
        foreach (var segment in result.Segments)
        {
            if (segment is null || !double.IsFinite(segment.Start) || !double.IsFinite(segment.End)
                || segment.Start < end || segment.End <= segment.Start || segment.End > info.Duration
                || (job.Options.PersonClip!.Detection.ExcludedRanges ?? []).Any(range => segment.Start < range.End && segment.End > range.Start)) return false;
            end = segment.End;
        }
        return true;
    }

}
