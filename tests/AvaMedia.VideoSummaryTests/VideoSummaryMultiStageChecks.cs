using System.Text.Json;
using AvaMedia.Core;

internal static class VideoSummaryMultiStageChecks
{
    private static void Check(bool condition, string name) => LongSummaryPhaseChecks.Check(condition, name);
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-stages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var jobs = new List<Job>();
        Job NewJob()
        {
            var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp4"); File.WriteAllText(source, "unit-test source identity");
            var job = new Job { FeatureId = "video-summary", Inputs = [source], Output = Path.Combine(root, Guid.NewGuid().ToString("N")),
                Options = new() { VideoSummary = new() { AnalyzeFrames = false } } };
            jobs.Add(job); return job;
        }
        try
        {
            var job = NewJob(); var host = new Host();
            using (var session = new VideoSummaryTaskSession(job, host))
            {
                Check(session.FeatureId == "video-summary" && session.Snapshot.State == MultiStageTaskState.Configuring && session.Snapshot.CurrentStageIndex == -1,
                    "queued video summary has no invented execution");
                Check(session.Snapshot.Stages.All(s => s.Elapsed is null && s.Current is null && s.Total is null && !s.IsClockRunning)
                    && session.Snapshot.Runtime.Backend == "" && session.Snapshot.StartedUtc is null, "queued counts, clocks and backend remain unknown");
                await session.StartAsync(); await session.PauseAsync(); await session.ResumeAsync(); await session.StopAsync();
                await session.RerunAsync(); await session.ChangeOutputFolderAsync("chosen-folder");
                Check(host.Calls.SequenceEqual(new[] { "start", "pause", "resume", "stop", "rerun:all", "output:chosen-folder" }), "every lifecycle command delegates to host");
                host.Allowed = false;
                await Throws<InvalidOperationException>(() => session.StartAsync());
                await Throws<InvalidOperationException>(() => session.RetryFromStageAsync(VideoSummaryStageIds.Transcript));
            }
            Check(host.Calls.Count == 6, "disposing the session does not stop or requeue the job");
            await Throws<ArgumentException>(() => Task.FromResult(new VideoSummaryTaskSession(new Job(), host)));

            job = NewJob(); job.State = JobState.Running;
            var clock = new ManualTime();
            using (var state = new VideoSummaryTaskState(job, clock))
            {
                job.VideoSummaryStages = state;
                state.Start(VideoSummaryStageIds.ReadVideo); clock.Advance(2);
                var before = state.Capture();
                Check(before.Stages[0].Elapsed == TimeSpan.FromSeconds(2) && before.Stages[0].IsClockRunning, "elapsed derives from execution clock");
                job.State = JobState.Paused; clock.Advance(20);
                Check(state.Capture().Stages[0] is { State: StageState.Paused, IsClockRunning: false, Elapsed.TotalSeconds: 2 }, "pause excludes suspended time without a session");
                job.State = JobState.Running; clock.Advance(3);
                state.Complete(VideoSummaryStageIds.ReadVideo, "实际探测完成");
                state.Configure(false, VideoSummaryRetry.SourceKey(job), "environment");
                state.Start(VideoSummaryStageIds.Transcript); state.Update(VideoSummaryStageIds.Transcript, 7, null, "条字幕", "已读取字幕");
                state.Finish(new IOException("transcript failed")); job.State = JobState.Failed; job.Error = "transcript failed";
                Check(before.Stages[0].State == StageState.Active && state.Capture().Stages[0].Elapsed == TimeSpan.FromSeconds(5), "captures are immutable and resume excludes pause interval");
                job.VideoSummaryStages = null; // Simulate a process restart: restore from durable observations.
                using var session = new VideoSummaryTaskSession(job, new Host());
                await Until(() => session.Snapshot.Stages.Any(s => s.State == StageState.Failed));
                Check(session.Snapshot.InterruptedStageId == VideoSummaryStageIds.Transcript && !session.Snapshot.IsClockRunning
                    && session.Snapshot.Commands.RetryStageId == "", "restart restores failure without claiming short-video retry");
                Check(session.Snapshot.Stages.Single(s => s.Stage.Id == VideoSummaryStageIds.PlanSegments).State == StageState.Skipped, "short pipeline records long-only stages as skipped");
                await File.AppendAllTextAsync(VideoSummaryTaskState.LogPath(job), "{partial");
                var logs = await session.ReadLogAsync();
                Check(logs.Any(l => l.StageId == VideoSummaryStageIds.Transcript && l.Level == TaskLogLevel.Error)
                    && logs.Any(l => l.Message == "已暂停"), "structured execution logs survive restart and partial trailing lines");
            }

            var probeFailure = NewJob(); probeFailure.State = JobState.Running;
            await Throws<IOException>(() => new VideoSummaryService(new ProbeEngine(new IOException("probe failed")))
                .ExecuteAsync(probeFailure, _ => { }, default));
            Check(probeFailure.VideoSummaryStages!.Capture().Stages[0] is { State: StageState.Failed, Detail: "probe failed" }, "probe failures are observed by the real service entry point");
            var shortFailure = NewJob(); shortFailure.State = JobState.Running;
            await Throws<InvalidDataException>(() => new VideoSummaryService(new ProbeEngine())
                .ExecuteAsync(shortFailure, _ => { }, default));
            var shortStages = shortFailure.VideoSummaryStages!.Capture().Stages;
            Check(shortStages.Single(s => s.Stage.Id == VideoSummaryStageIds.Transcript) is { State: StageState.Done, Current: 0 }
                && shortStages.Single(s => s.Stage.Id == VideoSummaryStageIds.VisualAnalysis).State == StageState.Skipped
                && shortStages.Single(s => s.Stage.Id == VideoSummaryStageIds.Summarize).State == StageState.Failed,
                "short service records real empty transcript, disabled vision and summary validation failure");
            var cancelled = NewJob(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => new VideoSummaryService(new ProbeEngine()).ExecuteAsync(cancelled, _ => { }, cancellation.Token));
            Check(cancelled.VideoSummaryStages!.Capture().Stages[0].State == StageState.Cancelled, "pre-probe cancellation retains actual interruption point");

            await LongPipelineAsync(NewJob(), root);
            Console.WriteLine("PASS video-summary multi-stage unit checks (no media or network)");
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
    private static async Task LongPipelineAsync(Job job, string root)
    {
        job.State = JobState.Running;
        var signals = new SummarySignals(1300,
            Enumerable.Range(0, 12).Select(i => new SummaryCue("cue" + i, i * 110, Math.Min(1300, i * 110 + 50), "本段介绍音频处理要求。", Language: "zh")).ToArray(),
            ShotDetector.FromBoundaries(1300, []).ToArray(), [], [], false, true);
        var folder = AiPartialResults.SummaryFolder(job); var budget = TokenBudget.Local8K;
        int extracted = 0, mapCalls = 0; bool fail = true;
        var observed = new HashSet<string>();
        var model = new LongSummaryMapChecks.FakeModel((prompt, _, _) =>
        {
            var current = job.VideoSummaryStages!.Capture().Stages.Single(s => s.State == StageState.Active).Stage.Id;
            observed.Add(current);
            using var json = JsonDocument.Parse(prompt);
            if (json.RootElement.TryGetProperty("segmentId", out _)) { mapCalls++; return LongSummaryMapChecks.Valid(prompt); }
            if (fail) throw new IOException("chapter failure");
            return json.RootElement.TryGetProperty("items", out _) ? LongSummaryVerifyChecks.Reply(prompt, budget) : LongSummaryReduceChecks.Reply(prompt, budget);
        });
        var orchestrator = new LongVideoSummaryOrchestrator(model, budget, new(model.ModelId, true), new(Path.Combine(root, "response-cache")));
        async Task Execute(VideoSummaryTaskState state)
        {
            job.VideoSummaryStages = state;
            try
            {
                await orchestrator.RunAsync(job, folder, "unit-signals", (_, _) => { extracted++; return Task.FromResult(signals); }, _ => { }, default);
                state.Complete(VideoSummaryStageIds.SaveResults, "单元测试报告保存完成"); state.Finish();
            }
            catch (Exception error) { state.Finish(error); throw; }
        }
        using (var state = new VideoSummaryTaskState(job))
        {
            state.Start(VideoSummaryStageIds.ReadVideo); state.Complete(VideoSummaryStageIds.ReadVideo, "fake probe");
            state.Configure(true, VideoSummaryRetry.SourceKey(job), "fake environment");
            state.Start(VideoSummaryStageIds.Transcript); state.Complete(VideoSummaryStageIds.Transcript, "fake cues"); state.SignalHash("unit-signals");
            await Throws<IOException>(() => Execute(state));
        }
        job.State = JobState.Failed; job.Error = "chapter failure";
        var summary = job.VideoSummaryStages!.Capture(); var callsBefore = mapCalls;
        Check(summary.Stages.Single(s => s.Stage.Id == VideoSummaryStageIds.ReduceChapters).State == StageState.Failed
            && summary.Stages.Single(s => s.Stage.Id == VideoSummaryStageIds.UnderstandSegments) is { State: StageState.Done, Current: > 0 } map
            && map.Current == map.Total, "orchestrator reports completed segment counts and actual reduction failure");
        Check(await VideoSummaryRetry.CanRetryAsync(job, summary), "validated signal and plan checkpoints admit long-video retry");
        job.VideoSummaryStages = null;
        var host = new Host();
        using (var session = new VideoSummaryTaskSession(job, host))
        {
            await Until(() => session.Snapshot.Commands.RetryStageId == VideoSummaryStageIds.UnderstandSegments);
            Check(session.Snapshot.InterruptedStageId == VideoSummaryStageIds.ReduceChapters
                && session.Snapshot.RetainedStageIds.Contains(VideoSummaryStageIds.PlanSegments), "retry boundary and interruption remain distinct after restart");
            await session.RetryFromStageAsync(VideoSummaryStageIds.UnderstandSegments);
            Check(host.Calls.Single() == "rerun:understand-segments", "stage retry delegates the admitted stage id");
            job.Options.VideoSummary!.Focus = "changed options";
            await Throws<InvalidOperationException>(() => session.RetryFromStageAsync(VideoSummaryStageIds.UnderstandSegments));
            job.Options.VideoSummary.Focus = "";
        }
        fail = false; job.State = JobState.Running;
        using (var state = new VideoSummaryTaskState(job))
        {
            state.Start(VideoSummaryStageIds.ReadVideo); state.Complete(VideoSummaryStageIds.ReadVideo, "fake reprobe");
            state.Configure(true, VideoSummaryRetry.SourceKey(job), "fake environment");
            state.Retain(summary, VideoSummaryStageIds.UnderstandSegments);
            await Execute(state);
            Check(state.Capture().Stages.All(s => s.State is StageState.Done or StageState.Skipped), "all observed long-pipeline stages finish");
            Check(state.Capture().Stages.All(s => !s.IsClockRunning), "finished stages have no live clocks");
        }
        Check(extracted == 1 && mapCalls == callsBefore, "checkpoint retry reuses signals and completed segments");
        Check(new[] { VideoSummaryStageIds.UnderstandSegments, VideoSummaryStageIds.ReduceChapters, VideoSummaryStageIds.VerifyChapters, VideoSummaryStageIds.Summarize }.All(observed.Contains),
            "model work is attributed to the actual map, reduce, verify and final-summary stages");
        await File.AppendAllTextAsync(job.Inputs[0], "modified");
        Check(!await VideoSummaryRetry.CanRetryAsync(job, summary), "source change rejects checkpoint retry");
        var other = summary with { SourceKey = VideoSummaryRetry.SourceKey(job) };
        await File.WriteAllTextAsync(Path.Combine(folder, "work", "signals.json"), "corrupted");
        Check(!await VideoSummaryRetry.CanRetryAsync(job, other), "corrupt signals reject checkpoint retry");
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow > timeout) throw new Exception("Timed out awaiting session publication."); await Task.Delay(30); }
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class Host : IMultiStageQueueHost
    {
        internal bool Allowed = true;
        internal List<string> Calls { get; } = [];
        public bool CanStart(Job job) => Allowed;
        public bool CanPause(Job job) => Allowed;
        public bool CanResume(Job job) => Allowed;
        public bool CanStop(Job job) => Allowed;
        public bool CanRerun(Job job) => Allowed;
        public bool CanChangeOutput(Job job) => Allowed;
        public Task StartAsync(Job job) { Calls.Add("start"); return Task.CompletedTask; }
        public void Pause(Job job) => Calls.Add("pause");
        public Task ResumeAsync(Job job) { Calls.Add("resume"); return Task.CompletedTask; }
        public void Stop(Job job) => Calls.Add("stop");
        public Task RerunAsync(Job job, string? fromStageId) { Calls.Add("rerun:" + (fromStageId ?? "all")); return Task.CompletedTask; }
        public Task ChangeOutputFolderAsync(Job job, string folder) { Calls.Add("output:" + folder); return Task.CompletedTask; }
    }
    private sealed class ProbeEngine(Exception? failure = null) : IMediaEngine
    {
        public AppSettings Settings { get; } = new();
        public string FFmpeg => throw new Exception("Unexpected media execution");
        public string FFprobe => throw new Exception("Unexpected media execution");
        public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
            => failure is null ? Task.FromResult(new MediaInfo(60, 640, 360, false, true, "{\"streams\":[]}")) : Task.FromException<MediaInfo>(failure);
        public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new NotSupportedException();
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
    }
}
