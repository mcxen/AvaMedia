using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Disk observations are loaded by the session worker; projection never touches the filesystem.</summary>
internal sealed record VideoSummaryViewData
{
    internal VideoSummaryReport? Short { get; init; }
    internal LongSummaryReport? Long { get; init; }
    internal MediaInfo? Info { get; init; }
    internal SegmentPlan? Plan { get; init; }
    internal SegmentOutput[] Outputs { get; init; } = [];
    internal SummarySegmentProgress[] Progress { get; init; } = [];
    internal int Frames { get; init; }
    internal int? TranscriptLines { get; init; }
    internal string TranscriptSource { get; init; } = "";
    internal TaskOutputView PublicOutput { get; init; } = new();
    internal TaskOutputView InternalOutput { get; init; } = new();
}

internal static class VideoSummaryTaskViewProjection
{
    private sealed record TranscriptData(SummaryCue[] Cues, string Source);
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());

    internal static async Task<VideoSummaryViewData> LoadAsync(Job job)
    {
        var internalFolder = AiPartialResults.SummaryFolder(job);
        var publicFolder = job.Output;
        var publicOutput = ObserveOutput(publicFolder, publicFolder == internalFolder);
        var internalOutput = ObserveOutput(internalFolder, true);
        var folder = job.State == JobState.Completed && publicOutput.Exists ? publicFolder
            : publicFolder.Length == 0 || internalOutput.Exists || Directory.Exists(Path.Combine(internalFolder, "work")) ? internalFolder : publicFolder;
        VideoSummaryReport? shortReport = null; LongSummaryReport? longReport = null;
        await TryRead(async () =>
        {
            var path = Path.Combine(folder, "report.json");
            if (!File.Exists(path)) return;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("version", out var version) && version.GetInt32() == 2)
                longReport = json.RootElement.Deserialize<LongSummaryReport>(LongSummaryJson.Options);
            else if (json.RootElement.TryGetProperty("Source", out _))
                shortReport = json.RootElement.Deserialize<VideoSummaryReport>();
        }).ConfigureAwait(false);
        var store = new SummaryCheckpointStore(Path.Combine(folder, "work"));
        var info = await Read<MediaInfo>(store, "probe.json").ConfigureAwait(false);
        var plan = await Read<SegmentPlan>(store, "segments.json").ConfigureAwait(false);
        var run = await Read<SummaryRunIdentity>(store, "run.json").ConfigureAwait(false);
        var outputs = new Dictionary<string, SegmentOutput>(StringComparer.Ordinal);
        SummarySegmentProgress[] progress = [];
        if (run is not null && run.PlanHash == plan?.PlanHash)
        {
            var savedProgress = await Read<SummarySegmentCheckpoint>(store, "segment-progress.json").ConfigureAwait(false);
            if (savedProgress?.RunHash == run.RunHash) progress = savedProgress.Segments;
            // Read only plan members; repaired results take precedence over their first pass.
            foreach (var range in plan.Segments)
                foreach (var suffix in new[] { "", "-repair" })
                {
                    var saved = await Read<PublishedSummarySegment>(store, $"map/{range.Id}{suffix}.json").ConfigureAwait(false);
                    if (saved?.RunHash == run.RunHash && saved.Output.Range == range) outputs[range.Id] = saved.Output;
                }
        }
        var signals = await Read<SummarySignals>(store, "signals.json").ConfigureAwait(false);
        var transcript = await Read<TranscriptData>(store, "asr/transcript.json").ConfigureAwait(false);
        var frameIds = new HashSet<string>(StringComparer.Ordinal);
        if (signals is not null)
            foreach (var id in signals.Frames.SelectMany(f => f.FrameIds)) frameIds.Add(id);
        // Completed and partial agent checkpoints also expose progress before signals.json is ready.
        await TryRead(async () =>
        {
            var framesFolder = store.PathFor("frames");
            if (!Directory.Exists(framesFolder)) return;
            foreach (var path in Directory.EnumerateFiles(framesFolder, "*-agent*.json"))
            {
                var agent = await Read<VideoSummaryFrameAgentResult>(store, Path.GetRelativePath(store.Root, path)).ConfigureAwait(false);
                if (agent is not null)
                    foreach (var id in agent.Observations.SelectMany(o => o.FrameIds)) frameIds.Add(id);
            }
        }).ConfigureAwait(false);
        var cues = signals?.RawCues ?? signals?.Cues ?? transcript?.Cues;
        return new()
        {
            Short = shortReport, Long = longReport, Info = info, Plan = plan, Outputs = outputs.Values.OrderBy(o => o.Range.Start).ToArray(),
            Progress = progress, Frames = frameIds.Count, TranscriptLines = cues?.Count,
            TranscriptSource = Labels((cues ?? []).Select(c => c.Source).Append(transcript?.Source ?? "")),
            PublicOutput = publicOutput, InternalOutput = internalOutput
        };
    }

    private static TaskOutputView ObserveOutput(string folder, bool fixedLocation)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new();
        var path = Path.Combine(folder, "report.json");
        var file = new FileInfo(path);
        try { return new() { Path = path, Folder = folder, Exists = file.Exists, Bytes = file.Exists ? file.Length : null, IsFixedLocation = fixedLocation }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new() { Path = path, Folder = folder, IsFixedLocation = fixedLocation }; }
    }
    private static async Task<T?> Read<T>(SummaryCheckpointStore store, string path) where T : class
    {
        T? value = null;
        await TryRead(async () => { value = await store.ReadAvailableAsync<T>(path).ConfigureAwait(false); }).ConfigureAwait(false);
        return value;
    }
    private static async Task TryRead(Func<Task> read)
    {
        try { await read().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Video summary view: {0}", error.Message); }
    }

    internal static VideoSummaryTaskView Project(Job job, VideoSummaryStageSummary? stages, VideoSummaryViewData saved, bool allowResults = true)
    {
        var source = job.Inputs.FirstOrDefault() ?? "";
        var plannedOutput = new TaskOutputView { Path = job.Output.Length == 0 ? "" : Path.Combine(job.Output, "report.json"), Folder = job.Output };
        if (!allowResults) return new() { SourcePath = source, Info = stages?.Info, IsLongVideo = stages?.IsLong ?? false, Output = plannedOutput };
        var shortReport = job.SummaryReport ?? saved.Short;
        var longReport = job.LongSummaryReport ?? saved.Long;
        bool isLong = stages?.IsLong ?? (longReport is not null || saved.Plan is not null);
        var liveOutputs = job.SummarySegments;
        var outputs = liveOutputs.Length > 0 ? liveOutputs : saved.Outputs;
        var liveProgress = job.VideoSummaryStages is not null ? SummaryProgressStore.Get(job.Id.ToString()) : null;
        var progress = (liveProgress ?? saved.Progress).ToDictionary(p => p.SegmentId, StringComparer.Ordinal);
        var byId = outputs.ToDictionary(o => o.Range.Id, StringComparer.Ordinal);
        var ranges = saved.Plan?.Segments ?? outputs.Select(o => o.Range).ToArray();
        var segments = isLong ? ranges.Select((range, index) =>
        {
            byId.TryGetValue(range.Id, out var output); progress.TryGetValue(range.Id, out var observed);
            var state = observed?.State switch
            {
                "running" => job.State switch { JobState.Paused => StageState.Paused, JobState.Failed => StageState.Failed,
                    JobState.Cancelled => StageState.Cancelled, _ => StageState.Active },
                "failed" => StageState.Failed, "cancelled" => StageState.Cancelled,
                "done" or "cached" or "degraded" => StageState.Done,
                _ => output is not null ? StageState.Done : StageState.Pending
            };
            return new VideoSummarySegmentView(index + 1, range.Start, range.End, state) { Title = output?.Result.TopicLabel ?? "" };
        }).ToArray() : [];
        string summary;
        IReadOnlyList<VideoSummaryChapterView> chapters;
        if (isLong)
        {
            summary = longReport?.Summary ?? "";
            if (summary.Length == 0) summary = string.Join("\n\n", outputs.OrderBy(o => o.Range.Start).Select(o => o.Result.Summary).Where(s => s.Length > 0));
            chapters = Freeze((longReport?.Chapters ?? []).Select(c => new VideoSummaryChapterView(c.Start, c.End, c.Title) { Text = c.Summary }));
        }
        else
        {
            summary = shortReport?.Result?.Summary
                ?? shortReport?.Sections.FirstOrDefault(s => s.Title == "摘要")?.Text
                ?? shortReport?.Sections.FirstOrDefault(s => s.Title == "视频内容总结")?.Text
                ?? shortReport?.Sections.FirstOrDefault(s => s.Title == "内容分析")?.Text ?? "";
            chapters = Freeze((shortReport?.Chapters ?? []).Select(c => ShortChapter(c, shortReport!)).OfType<VideoSummaryChapterView>());
        }
        bool hasReport = isLong ? longReport is not null : shortReport is not null;
        bool partial = isLong ? longReport?.IsPartial ?? true : shortReport?.IsPartial ?? true;
        var internalFolder = AiPartialResults.SummaryFolder(job);
        var internalOutput = saved.InternalOutput.Path.Length > 0 ? saved.InternalOutput
            : new TaskOutputView { Path = Path.Combine(internalFolder, "report.json"), Folder = internalFolder, IsFixedLocation = true };
        var outputView = job.State == JobState.Completed && saved.PublicOutput.Exists ? saved.PublicOutput
            : internalOutput.Exists || hasReport && (partial || !saved.PublicOutput.Exists) ? internalOutput
            : saved.PublicOutput.Path.Length > 0 ? saved.PublicOutput : plannedOutput;
        return new()
        {
            SourcePath = source, Info = stages?.Info ?? saved.Info, IsLongVideo = isLong, Segments = Freeze(segments),
            FramesAnalyzed = isLong ? saved.Frames : ShortFrames(shortReport),
            TranscriptLines = isLong ? saved.TranscriptLines ?? job.SubtitleResult?.Cues.Length ?? 0
                : shortReport?.SubtitleCount ?? job.SubtitleResult?.Cues.Length ?? 0,
            TranscriptSource = isLong ? saved.TranscriptSource : Labels([shortReport?.TranscriptSource ?? ""]),
            ModelName = !isLong && shortReport?.Models is { Length: > 0 } models ? string.Join(" / ", models)
                : shortReport?.Result?.SummaryModel ?? stages?.Runtime?.Model ?? "",
            Summary = summary, Highlights = Freeze(isLong ? (longReport?.KeyPoints ?? []).Select(p => p.Text) : shortReport?.Highlights ?? []),
            Keywords = Freeze(isLong ? [] : shortReport?.Keywords ?? []), Chapters = chapters,
            HasResult = hasReport || outputs.Length > 0, IsPartial = partial,
            Output = outputView
        };
    }

    private static int ShortFrames(VideoSummaryReport? report)
    {
        if (report is null) return 0;
        var ids = report.Evidence.SelectMany(e => e.FrameIds).Distinct(StringComparer.Ordinal).Count();
        return ids > 0 ? ids : report.Result?.Frames.Count(f => !f.Refused && !string.IsNullOrWhiteSpace(f.Description)) ?? 0;
    }
    private static VideoSummaryChapterView? ShortChapter(VideoSummaryChapter chapter, VideoSummaryReport report)
    {
        var evidence = report.Evidence.Where(e => chapter.EvidenceIds.Contains(e.Id, StringComparer.Ordinal)).ToArray();
        // Short reports have a navigation point, not an interval. Only cited evidence supplies a real end.
        if (evidence.Length == 0 || chapter.Seconds is not { } start) return null;
        return new(start, evidence.Max(e => e.End), chapter.Title) { Text = chapter.Text };
    }
    private static string Labels(IEnumerable<string> sources) => string.Join("、", sources.Select(s => s switch
    {
        "Whisper" => "语音转写", "视频字幕" => "内嵌字幕", "外部字幕" => "外部字幕", "无音轨" => "无字幕", _ => ""
    }).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal));
}
