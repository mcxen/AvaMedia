using AvaMedia.Core;

internal static class VideoSummaryTaskViewChecks
{
    private static void Check(bool value, string message) => LongSummaryPhaseChecks.Check(value, message);
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var jobs = new List<Job>();
        Job NewJob(JobState state = JobState.Completed)
        {
            var job = new Job { FeatureId = "video-summary", Inputs = [Path.Combine(root, "source.mp4")],
                Output = Path.Combine(root, Guid.NewGuid().ToString("N")), State = state, Options = new() { VideoSummary = new() } };
            jobs.Add(job); return job;
        }
        try
        {
            var queued = NewJob(JobState.Waiting);
            using (IVideoSummaryTask session = new VideoSummaryTaskSession(queued, new Host()))
            {
                Check(session.View is { HasResult: false, IsPartial: true, Info: null, ModelName: "", TranscriptSource: "", FramesAnalyzed: 0,
                    TranscriptLines: 0, IsLongVideo: false, Segments.Count: 0, Chapters.Count: 0, Summary: "" }, "queued view has no invented results or counts");
                Check(session.View.SourcePath == queued.Inputs[0] && session.View.Output.Path == Path.Combine(queued.Output, "report.json"), "queued view exposes source and planned report path");
            }
            var info = new MediaInfo(60, 1920, 1080, true, true, "{\"streams\":[]}");
            var job = NewJob();
            var report = ShortReport();
            job.SummaryReport = report;
            var shortView = VideoSummaryTaskViewProjection.Project(job, new(VideoSummaryTaskState.Plans(), Info: info), new());
            Check(shortView is { Info.Width: 1920, Summary: "完整正文", FramesAnalyzed: 2, TranscriptLines: 3, TranscriptSource: "内嵌字幕",
                ModelName: "vision / text", HasResult: true, IsPartial: false, Segments.Count: 0 }, "short report projects actual media, models, transcript and understood frame counts");
            Check(shortView.Highlights.SequenceEqual(["要点"]) && shortView.Keywords.SequenceEqual(["关键词"])
                && shortView.Chapters.Single() is { Start: 5, End: 12, Title: "章节", Text: "章节正文" }, "short outline uses cited evidence range, title and body");
            report.Highlights[0] = "mutated";
            Check(shortView.Highlights.Single() == "要点" && shortView.Highlights is not string[], "published collections cannot change with report arrays");
            var partial = ShortReport() with { IsPartial = true, Result = null,
                Sections = [new("视频内容总结", "已生成正文")], TranscriptSource = "Whisper" };
            job.SummaryReport = partial;
            var partialView = VideoSummaryTaskViewProjection.Project(job, null, new());
            Check(partialView is { Summary: "已生成正文", TranscriptSource: "语音转写", HasResult: true, IsPartial: true, Output.IsFixedLocation: true }
                && partialView.Output.Path == Path.Combine(AiPartialResults.SummaryFolder(job), "report.json"), "partial sections remain readable before report export");
            Check(VideoSummaryTaskViewProjection.Project(job, null, new(), false) is { HasResult: false, Summary: "", TranscriptSource: "" }, "rerun configuration suppresses previous results");
            foreach (var (source, label) in new[] { ("外部字幕", "外部字幕"), ("无音轨", "无字幕"), ("", "") })
            {
                job.SummaryReport = partial with { TranscriptSource = source };
                Check(VideoSummaryTaskViewProjection.Project(job, null, new()).TranscriptSource == label, "transcript label: " + source);
            }
            await RestoreShortAsync(job, info);
            await RestoreLongAsync(NewJob(JobState.Failed), info);
            await ProgressStatesAsync(NewJob(JobState.Running));
            Console.WriteLine("PASS video-summary view projection unit checks (no media or network)");
        }
        finally
        {
            foreach (var job in jobs)
            {
                var folder = Path.GetDirectoryName(AiTaskResults.PathFor(job, "summary"))!;
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            Directory.Delete(root, true);
        }
    }

    private static VideoSummaryReport ShortReport() => new("source.mp4", 60, "视频字幕", "zh", ["vision", "text"], 3,
        [], [], [], [])
    {
        Highlights = ["要点"], Keywords = ["关键词"],
        Result = new([new(TimeSpan.FromSeconds(5), "画面", false, "vision"), new(TimeSpan.FromSeconds(9), "画面", false, "vision"),
            new(TimeSpan.FromSeconds(15), null, true, "vision")], "完整正文", "text"),
        Evidence = [new("e1", "frame", 5, 5, "画面", ["f1"]), new("e2", "sequence", 5, 12, "连续观察", ["f1", "f2"])],
        Chapters = [new("章节", "章节正文", 5) { EvidenceIds = ["e1", "e2"] }, new("无区间", "无时间依据", null)]
    };

    private static async Task RestoreShortAsync(Job job, MediaInfo info)
    {
        using (var state = new VideoSummaryTaskState(job))
        {
            state.Start(VideoSummaryStageIds.ReadVideo); state.Configure(false, "source", "env", info);
            state.Complete(VideoSummaryStageIds.ReadVideo, "probed"); state.Finish();
        }
        var report = ShortReport();
        await AiTaskResults.SaveAsync(Path.Combine(job.Output, "report.json"), report, default);
        await AiTaskResults.SaveAsync(Path.Combine(AiPartialResults.SummaryFolder(job), "report.json"), report with { IsPartial = true }, default);
        job.SummaryReport = null; job.VideoSummaryStages = null;
        using var session = new VideoSummaryTaskSession(job, new Host());
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, _) =>
        {
            if (session.View.HasResult && session.View.Info is not null && session.Snapshot.Revision > 0) observed.TrySetResult();
        };
        await Until(() => session.View.HasResult && session.View.Info is not null);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(session.View is { Info.Width: 1920, Summary: "完整正文", IsPartial: false, Output.Exists: true, Output.IsFixedLocation: false }
            && session.View.Output.Bytes > 0 && session.View.Output.Path == Path.Combine(job.Output, "report.json"), "restart restores complete short report, persisted probe and public report metadata before Changed");
    }

    private static async Task RestoreLongAsync(Job job, MediaInfo info)
    {
        var folder = AiPartialResults.SummaryFolder(job); var store = new SummaryCheckpointStore(Path.Combine(folder, "work"));
        var ranges = new[] { new SegmentRange("s0", 0, 20), new SegmentRange("s1", 20, 40), new SegmentRange("s2", 40, 60) };
        await store.WriteAsync("probe.json", "probe", info);
        await store.WriteAsync("segments.json", "plan", new SegmentPlan(60, ranges, "plan-hash"));
        await store.WriteAsync("run.json", "run", new SummaryRunIdentity("run-hash", "plan-hash"));
        var first = new SegmentOutput(ranges[0], new("第一段正文", [], [], [], [], [], "第一段标题"));
        await store.WriteAsync("map/s0.json", "map", new PublishedSummarySegment("run-hash", first));
        await store.WriteAsync("map/s0-repair.json", "repair", new PublishedSummarySegment("run-hash", first with { Result = first.Result with { Summary = "修复后正文" } }));
        await store.WriteAsync("map/s1.json", "stale", new PublishedSummarySegment("old-run", new(ranges[1], first.Result)));
        await store.WriteAsync("segment-progress.json", "states", new SummarySegmentCheckpoint("run-hash",
            [new("s0", 0, 20, "done", ""), new("s1", 20, 40, "failed", ""), new("s2", 40, 60, "pending", "")]));
        var cues = new[] { new SummaryCue("c1", 1, 3, "原字幕") { Source = "视频字幕" }, new SummaryCue("c2", 4, 6, "语音") { Source = "Whisper" } };
        var signals = new SummarySignals(60, [cues[0]], [], [],
            [new("o1", 1, "", "单帧") { FrameIds = ["f1"] }, new("o2", 2, "", "比较") { FrameIds = ["f1", "f2"] }], false, true) { RawCues = cues };
        await store.WriteAsync("signals.json", "signals", signals);
        var report = new LongSummaryReport(60, "zh", "", [], [], new([], [])) { IsPartial = true };
        await LongVideoSummaryOrchestrator.SaveReportAsync(folder, report, default);
        var saved = await VideoSummaryTaskViewProjection.LoadAsync(job);
        var stages = new VideoSummaryStageSummary(VideoSummaryTaskState.Plans(), Runtime: new("actual-summary-model"), IsLong: true);
        var view = VideoSummaryTaskViewProjection.Project(job, stages, saved);
        Check(view is { IsLongVideo: true, Info.Width: 1920, FramesAnalyzed: 2, TranscriptLines: 2, TranscriptSource: "内嵌字幕、语音转写",
            ModelName: "actual-summary-model", Summary: "修复后正文", HasResult: true, IsPartial: true, Output.Exists: true, Output.IsFixedLocation: true }, "long partial projection restores verified checkpoints and raw transcript without counting evidence as frames");
        Check(view.Segments.Select(s => s.State).SequenceEqual([StageState.Done, StageState.Failed, StageState.Pending])
            && view.Segments[0] is { Number: 1, Start: 0, End: 20, Title: "第一段标题" } && view.Segments[1].Title == "",
            "real plan, saved failures and run-matched repaired segments drive semantic rows");
        var final = report with { IsPartial = false, Summary = "全片正文", KeyPoints = [new("全片要点", ["s0"])],
            Chapters = [new("c1", 0, 60, "全片章节", "章节归纳", [], [])] };
        await LongVideoSummaryOrchestrator.SaveReportAsync(folder, final, default);
        using (var session = new VideoSummaryTaskSession(job, new Host()))
        {
            await Until(() => session.View.Summary == "全片正文");
            Check(session.View.Highlights.SequenceEqual(["全片要点"]) && session.View.Keywords.Count == 0
                && session.View.Chapters.Single() is { Start: 0, End: 60, Title: "全片章节", Text: "章节归纳" }, "restart restores long overview, key points and exact chapter ranges; absent keywords remain empty");
        }
        await File.WriteAllTextAsync(store.PathFor("map/s0-repair.json"), "tampered");
        saved = await VideoSummaryTaskViewProjection.LoadAsync(job);
        Check(saved.Outputs.Single().Result.Summary == "第一段正文", "invalid checkpoint content hash falls back to validated first pass");
        await store.WriteAsync("run.json", "wrong-plan", new SummaryRunIdentity("run-hash", "different-plan"));
        saved = await VideoSummaryTaskViewProjection.LoadAsync(job);
        Check(saved.Outputs.Length == 0 && saved.Progress.Length == 0, "mismatched plan rejects old segment results and state checkpoints");
    }

    private static async Task ProgressStatesAsync(Job job)
    {
        using var state = new VideoSummaryTaskState(job); job.VideoSummaryStages = state;
        state.Configure(true, "source", "env");
        var ranges = Enumerable.Range(0, 6).Select(i => new SegmentRange("s" + i, i * 10, (i + 1) * 10)).ToArray();
        SummaryProgressStore.Set(job.Id.ToString(), ranges.Select((r, i) => new SummarySegmentProgress(r.Id, r.Start, r.End,
            new[] { "running", "cached", "degraded", "failed", "cancelled", "pending" }[i], "")));
        var data = new VideoSummaryViewData { Plan = new(60, ranges, "hash") };
        var view = VideoSummaryTaskViewProjection.Project(job, state.Capture(), data);
        Check(view.Segments.Select(s => s.State).SequenceEqual([StageState.Active, StageState.Done, StageState.Done, StageState.Failed, StageState.Cancelled, StageState.Pending]), "live segment states map directly to StageState");
        job.State = JobState.Paused;
        Check(VideoSummaryTaskViewProjection.Project(job, state.Capture(), data).Segments[0].State == StageState.Paused, "active semantic segment follows actual job pause");
        job.State = JobState.Cancelled;
        Check(VideoSummaryTaskViewProjection.Project(job, state.Capture(), data).Segments[0].State == StageState.Cancelled, "interrupted active segment follows job cancellation");
        var store = new SummaryCheckpointStore(Path.Combine(AiPartialResults.SummaryFolder(job), "work"));
        await store.WriteAsync("asr/transcript.json", "transcript", new { Cues = new[] { new SummaryCue("c", 0, 1, "转写") }, Source = "Whisper" });
        await store.WriteAsync("frames/s0-agent-partial.json", "frames", new VideoSummaryFrameAgentResult(
            [new("captured-only", 1, "frame.png"), new("f1", 2, "frame2.png")], [new("o", "观察", ["f1"], false)], [], [], false));
        var loaded = await VideoSummaryTaskViewProjection.LoadAsync(job);
        Check(loaded is { Frames: 1, TranscriptLines: 1, TranscriptSource: "语音转写" }, "unfinished extraction restores understood agent frames and completed transcript independently");
    }

    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("View was not published."); await Task.Delay(25); }
    }
    private sealed class Host : IMultiStageQueueHost
    {
        public bool CanStart(Job job) => false;
        public bool CanPause(Job job) => false;
        public bool CanResume(Job job) => false;
        public bool CanStop(Job job) => false;
        public bool CanRerun(Job job) => false;
        public bool CanChangeOutput(Job job) => false;
        public Task StartAsync(Job job) => throw new NotSupportedException();
        public void Pause(Job job) => throw new NotSupportedException();
        public Task ResumeAsync(Job job) => throw new NotSupportedException();
        public void Stop(Job job) => throw new NotSupportedException();
        public Task RerunAsync(Job job, string? fromStageId) => throw new NotSupportedException();
        public Task ChangeOutputFolderAsync(Job job, string folder) => throw new NotSupportedException();
    }
}
