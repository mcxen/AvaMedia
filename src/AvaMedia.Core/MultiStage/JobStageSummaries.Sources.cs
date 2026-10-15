using System.Text.Json;

namespace AvaMedia.Core;

internal sealed record JobStageObservation(object? Owner, TaskStageStatus[] Stages);

internal static class JobStageSummarySources
{
    internal static object? Owner(Job job) => job.FeatureId switch
    {
        "person-clip" => job.PersonClipStages,
        "rotate" when job.Options.Orientation is not null => job.OrientationStages,
        "video-summary" => job.VideoSummaryStages,
        "media-ai" or "folder-classification" => job.MediaClassificationStages,
        "auto-subtitle" => job.SubtitleStages,
        _ => null
    };

    internal static JobStageObservation Capture(Job job, object? ignored, bool loadSaved = false)
    {
        var owner = Owner(job);
        if (owner is not null && !ReferenceEquals(owner, ignored))
        {
            var stages = owner switch
            {
                PersonClipTaskState state => state.Capture().Summary.Stages.Select(s => s.Status).ToArray(),
                OrientationTaskState state => state.Capture().Stages,
                VideoSummaryTaskState state => state.Capture().Stages,
                MediaClassificationTaskState state => state.Capture().Stages,
                SubtitleTaskState state => state.Capture().Stages,
                _ => []
            };
            return new(owner, stages);
        }
        TaskStageStatus[]? saved = null;
        if (loadSaved)
            saved = job.FeatureId switch
            {
                "person-clip" => Read<PersonClipStageSummary>(job, "people-stages")?.Stages.Select(s => s.Status).ToArray(),
                "rotate" when job.Options.Orientation is not null => Read<OrientationStageSummary>(job, "orientation-stages")?.Stages,
                "video-summary" => Read<VideoSummaryStageSummary>(job, "video-summary-stages")?.Stages,
                "media-ai" or "folder-classification" => Read<MediaClassificationSummary>(job, "classification-stages")?.Stages,
                "auto-subtitle" => Read<SubtitleStageSummary>(job, "subtitle-stages")?.Stages,
                _ => null
            };
        var plans = Plans(job);
        // Never accept a checkpoint for another plan (or a malformed/partial stage array).
        return new(null, saved is not null && saved.Select(s => s.Stage.Id).SequenceEqual(plans.Select(s => s.Stage.Id)) ? saved : plans);
    }

    internal static TaskStageStatus[] Plans(Job job) => job.FeatureId switch
    {
        "person-clip" => PersonClipTaskState.Plans(job),
        "rotate" when job.Options.Orientation is not null => OrientationTaskState.Plans(),
        "video-summary" => VideoSummaryTaskState.Plans(),
        "media-ai" or "folder-classification" => MediaClassificationTaskState.Plans(job),
        "auto-subtitle" => SubtitleTaskState.Plans(job),
        _ => [new(new(job.FeatureId, Catalog.All.FirstOrDefault(f => f.Id == job.FeatureId)?.Label ?? job.FeatureId, ""), StageState.Pending)]
    };

    private static T? Read<T>(Job job, string kind)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(AiTaskResults.PathFor(job, kind))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return default; }
    }

    internal static async Task<string> RetryAsync(Job job, string id)
    {
        if (job.FeatureId != "person-clip" || id is not (PersonClipStageIds.Detect or PersonClipStageIds.Refine or PersonClipStageIds.Export)) return "";
        try
        {
            var plan = await PersonClipRetry.ResolveAsync(job, id, new ModelStore(), CancellationToken.None).ConfigureAwait(false);
            return plan.StageId == id ? id : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return ""; }
    }
}
