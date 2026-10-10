using System.Text.Json.Serialization;

namespace AvaMedia.Core;

/// <summary>Keep usable analysis separate from the task's completion state and public export.</summary>
public static class AiPartialResults
{
    public static string SummaryFolder(Job job) => Path.Combine(Path.GetDirectoryName(AiTaskResults.PathFor(job, "summary"))!, "summary");

    public static async Task SaveAvailableAsync(Job job)
    {
        // Cancellation stops inference, not the atomic save of results already produced.
        if (job.ClassificationSnapshot is { } classification)
            foreach (var file in classification.Files.Where(file => file.Pending && file.Result is not null))
                await FolderClassificationTaskStore.SaveFileAsync(job, file, CancellationToken.None).ConfigureAwait(false);
        if (job.MediaTagResult is { } tags)
            await AiTaskResults.SaveAsync(job.HasInternalOutput ? job.Output : AiTaskResults.PathFor(job, "tags"), tags, CancellationToken.None).ConfigureAwait(false);
        if (job.PersonDetectionResult is { } people)
            await AiTaskResults.SaveAsync(job.Options.PersonClip?.AnalysisOnly == true ? job.Output : AiTaskResults.PathFor(job, "people"), people, CancellationToken.None).ConfigureAwait(false);
        if (job.SubtitleResult is { } subtitles && job.Options.VideoSummary is null)
            await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "subtitles"), subtitles, CancellationToken.None).ConfigureAwait(false);
        if (job.OrientationResult is { } orientation)
            await AiTaskResults.SaveAsync(job.Output, orientation, CancellationToken.None).ConfigureAwait(false);
        if (job.SummaryReport is { } report)
            await AiTaskResults.SaveAsync(Path.Combine(SummaryFolder(job), "report.json"), report, CancellationToken.None).ConfigureAwait(false);
    }
}

public partial class Job
{
    public bool OutputIsPartial { get; set; }
    private VideoSummaryReport? _summaryReport;
    [JsonIgnore] public VideoSummaryReport? SummaryReport
    { get => Volatile.Read(ref _summaryReport); set { Volatile.Write(ref _summaryReport, value); Raise(); } }
}
