using System.Text.Json;
using AvaMedia.Core;

internal static class MediaClassificationTests
{
    private static int _checks;
    private static readonly List<Job> Jobs = [];
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "classification-stages-" + Guid.NewGuid().ToString("N"));

    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await CommandsAsync();
            await ObservationsAsync();
            await RestoreAsync();
            await ExecutionAsync();
            Console.WriteLine($"Passed {_checks} media classification unit checks.");
        }
        finally
        {
            foreach (var job in Jobs)
            {
                job.MediaClassificationStages?.Finish(JobState.Cancelled, "测试清理");
                var folder = Path.GetDirectoryName(MediaClassificationTaskState.SummaryPath(job))!;
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                FolderClassificationTaskStore.Delete(job);
            }
            Directory.Delete(Root, true);
        }
    }

    private static Job NewJob(string feature = "folder-classification")
    {
        var job = new Job { FeatureId = feature };
        Jobs.Add(job); return job;
    }
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); _checks++; Console.WriteLine("PASS " + name); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }
    private static TaskStageStatus Stage(MediaClassificationTaskState state, string id) => state.Capture().Stages.Single(stage => stage.Stage.Id == id);
    private static async Task RefreshAsync(MediaClassificationTaskSession session, Action change)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args) => signal.TrySetResult();
        session.Changed += Changed;
        try { change(); await signal.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { session.Changed -= Changed; }
    }

    private static async Task CommandsAsync()
    {
        foreach (var feature in new[] { "media-ai", "folder-classification" })
        {
            var job = NewJob(feature); var host = new Host();
            using var session = new MediaClassificationTaskSession(job, host);
            // Model a host dispatching to an owner thread that also reads Snapshot.
            host.OnCapabilityCheck = () => { _ = Task.Run(() => session.Snapshot).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); };
            Check(session.FeatureId == feature && session.Snapshot.State == MultiStageTaskState.Configuring, feature + " queued session");
            Check(session.Stages.Count == (feature == "media-ai" ? 4 : 5)
                && session.Snapshot.Stages.All(stage => stage.State == StageState.Pending && stage.Elapsed is null && stage.Current is null)
                && session.Snapshot.StartedUtc is null && session.Snapshot.Runtime.Backend.Length == 0, "queued data contains no invented observations");
            await session.StartAsync(); await session.PauseAsync(); await session.ResumeAsync(); await session.StopAsync();
            await session.RerunAsync(); await session.ChangeOutputFolderAsync("new-output");
            Check(host.Calls.SequenceEqual(new[] { "start", "pause", "resume", "stop", "rerun", "output:new-output" })
                && host.LastJob == job && host.RetryStage is null, "all lifecycle commands delegate to host");
            Check(session.Stages.All(stage => !stage.CanRetryFrom) && session.Snapshot.Commands.RetryStageId.Length == 0, "stage retry is not advertised");
            await ThrowsAsync<InvalidOperationException>(() => session.RetryFromStageAsync(MediaClassificationStageIds.WriteResults), "unsupported retry rejected");
            await RefreshAsync(session, () => host.Allowed = false);
            Check(!session.Snapshot.Commands.CanStart && !session.Snapshot.Commands.CanRerun, "host capability changes publish without job notifications");
            await ThrowsAsync<InvalidOperationException>(() => session.StartAsync(), "host command gate enforced");
            var count = host.Calls.Count; session.Dispose(); session.Dispose();
            Check(host.Calls.Count == count, "dispose never stops execution");
            await ThrowsAsync<ObjectDisposedException>(() => session.StopAsync(), "disposed commands rejected");
        }
        Check(!MediaClassificationStageIds.For(NewJob("media-ai")).Any(stage => stage.Id == MediaClassificationStageIds.PlanClassification), "tagging has no classification planning stage");
        try { using var invalid = new MediaClassificationTaskSession(NewJob("zip"), new Host()); throw new Exception("wrong feature accepted"); }
        catch (ArgumentException) { Check(true, "unrelated job rejected"); }
    }

    private static async Task ObservationsAsync()
    {
        var job = NewJob(); job.State = JobState.Running;
        var clock = new Clock(); var state = new MediaClassificationTaskState(job, clock);
        clock.Advance(2); state.Update("已检查 2 个", 2, 3, "个文件");
        state.Complete(MediaClassificationStageIds.ScanFiles, "检查完成");
        state.Enter(MediaClassificationStageIds.AnalyzeItems);
        clock.Advance(3); job.State = JobState.Paused; clock.Advance(20);
        Check(Stage(state, MediaClassificationStageIds.AnalyzeItems) is { State: StageState.Paused, IsClockRunning: false, Elapsed.TotalSeconds: 3 }, "paused clock excludes waiting time");
        job.State = JobState.Running; clock.Advance(4);
        state.Enter(MediaClassificationStageIds.PlanClassification); clock.Advance(1);
        Check(Stage(state, MediaClassificationStageIds.AnalyzeItems) is { State: StageState.Pending, Elapsed.TotalSeconds: 7, IsClockRunning: false }, "interleaved work stays unfinished with measured elapsed");
        state.Enter(MediaClassificationStageIds.AnalyzeItems); clock.Advance(2);
        state.Observe(new("a.mp4", null, null, 0, 1) { Activity = new("识别视频标签", "JoyTag", clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime)
        { Current = 2, Total = 8, Unit = "帧", Backend = "CPU" } });
        Check(Stage(state, MediaClassificationStageIds.AnalyzeItems) is { Current: 2, Total: 8, Fraction: .25, Elapsed.TotalSeconds: 9 }, "real frame counts and resumed stage elapsed");
        using var session = new MediaClassificationTaskSession(job, new Host());
        Check(session.Snapshot.Runtime == new TaskRuntimeInfo("JoyTag", "CPU") && session.Snapshot.Elapsed.TotalSeconds == 12, "runtime and elapsed derive from execution");
        var old = session.Snapshot;
        await RefreshAsync(session, () => { state.Finish(JobState.Cancelled, "已取消"); job.State = JobState.Cancelled; });
        Check(session.Snapshot is { State: MultiStageTaskState.Cancelled, IsClockRunning: false, InterruptedStageId: MediaClassificationStageIds.AnalyzeItems }
            && old.Stages.Any(stage => stage.State == StageState.Active), "cancellation published and earlier snapshots immutable");
        var logs = await session.ReadLogAsync();
        Check(logs.Any(entry => entry.StageId == MediaClassificationStageIds.AnalyzeItems && entry.Level == TaskLogLevel.Marker)
            && logs.All(entry => entry.TimeUtc.Kind == DateTimeKind.Utc), "structured cancellation log preserves real stage and time");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => session.ReadLogAsync(cancellation.Token), "log cancellation honored");
        var between = NewJob(); between.State = JobState.Running;
        var gap = new MediaClassificationTaskState(between, clock);
        gap.Complete(MediaClassificationStageIds.ScanFiles, "检查完成"); gap.Finish(JobState.Cancelled, "阶段之间取消");
        between.State = JobState.Cancelled;
        using var gapSession = new MediaClassificationTaskSession(between, new Host());
        Check((await gapSession.ReadLogAsync()).Any(entry => entry.StageId == "" && entry.Level == TaskLogLevel.Marker)
            && gapSession.Snapshot.InterruptedStageId.Length == 0, "cancellation between stages logs task marker without invented stage");
    }

    private static async Task RestoreAsync()
    {
        var job = NewJob("media-ai"); job.State = JobState.Running;
        var state = new MediaClassificationTaskState(job, new Clock());
        state.Complete(MediaClassificationStageIds.ScanFiles, "检查完成");
        state.Enter(MediaClassificationStageIds.PrepareModel);
        state.Finish(JobState.Failed, "模型加载失败"); job.State = JobState.Failed; job.Error = "模型加载失败";
        job.MediaClassificationStages = null;
        using (var restored = new MediaClassificationTaskSession(job, new Host()))
        {
            Check(restored.Snapshot.InterruptedStageId == MediaClassificationStageIds.PrepareModel
                && !restored.Snapshot.IsClockRunning && restored.Snapshot.Error == job.Error, "failed stage restores from durable summary");
            File.AppendAllText(MediaClassificationTaskState.LogPath(job), "{partial");
            Check((await restored.ReadLogAsync()).Any(entry => entry.Level == TaskLogLevel.Error), "partial log tail does not discard previous entries");
        }
        // A persisted active stage after process interruption must not restart its clock.
        var path = MediaClassificationTaskState.SummaryPath(job);
        var summary = JsonSerializer.Deserialize<MediaClassificationSummary>(File.ReadAllText(path))!;
        summary.Stages[1] = summary.Stages[1] with { State = StageState.Active, IsClockRunning = true };
        File.WriteAllText(path, JsonSerializer.Serialize(summary));
        job.State = JobState.Cancelled;
        using (var interrupted = new MediaClassificationTaskSession(job, new Host()))
            Check(interrupted.Snapshot.Stages[1].State == StageState.Cancelled && !interrupted.Snapshot.IsClockRunning, "process interruption restores as cancelled without running clock");
        job.State = JobState.Waiting;
        using var queued = new MediaClassificationTaskSession(job, new Host());
        Check(queued.Snapshot.Stages.All(stage => stage.State == StageState.Pending && stage.Elapsed is null)
            && (await queued.ReadLogAsync()).Count == 0, "requeued task does not expose previous run observations");
        File.WriteAllText(path, "{invalid");
        using var corrupt = new MediaClassificationTaskSession(job, new Host());
        Check(corrupt.Snapshot.Stages.All(stage => stage.Elapsed is null), "corrupt summary leaves observations unknown");
    }

    private static async Task ExecutionAsync()
    {
        var source = Path.Combine(Root, "synthetic.mp4"); File.WriteAllText(source, "synthetic metadata fixture");
        Job MetadataJob()
        {
            var job = NewJob(); job.State = JobState.Running; job.Inputs = [source];
            job.Options.FolderClassification = new() { Rules = [FolderClassificationRule.Presets.Single(rule => rule.ByDuration)] };
            job.Output = FolderClassificationTaskStore.Folder(job); return job;
        }
        var job = MetadataJob(); var engine = new Engine();
        await new FolderClassificationJobService(engine).ExecuteAsync(job, value => job.Progress = value, default);
        job.State = JobState.Completed;
        using (var session = new MediaClassificationTaskSession(job, new Host()))
        {
            Check(engine.Probes == 1 && session.Snapshot.Stages[1] is { State: StageState.Skipped, Elapsed: null }, "metadata-only execution skips models");
            Check(session.Snapshot.Stages.Where(stage => stage.Stage.Id != MediaClassificationStageIds.PrepareModel).All(stage => stage.State == StageState.Done)
                && session.Snapshot.CompletedUtc is not null && !session.Snapshot.IsClockRunning, "metadata execution completes actual stages");
            Check(session.Snapshot.Stages.Single(stage => stage.Stage.Id == MediaClassificationStageIds.WriteResults).Current == 1, "checkpoint writes counted from successful saves");
        }
        job.State = JobState.Running;
        await new FolderClassificationJobService(engine).ExecuteAsync(job, _ => { }, default);
        Check(engine.Probes == 1 && job.MediaClassificationStages!.Capture().Stages.Skip(1).All(stage => stage.State == StageState.Skipped), "cached source reuse does not fabricate model, analysis or write work");
        var cancelled = MetadataJob();
        await ThrowsAsync<OperationCanceledException>(() => new FolderClassificationJobService(new Engine { Cancel = true }).ExecuteAsync(cancelled, _ => { }, default), "analysis cancellation propagates");
        Check(Stage(cancelled.MediaClassificationStages!, MediaClassificationStageIds.AnalyzeItems).State == StageState.Cancelled, "executor records cancellation point");
        var failed = MetadataJob();
        await ThrowsAsync<InvalidOperationException>(() => new FolderClassificationJobService(new Engine { Fail = true }).ExecuteAsync(failed, _ => { }, default), "per-file failure propagates after checkpoint");
        Check(Stage(failed.MediaClassificationStages!, MediaClassificationStageIds.AnalyzeItems).State == StageState.Failed
            && failed.ClassificationSnapshot?.Failed == 1, "failed media retains classification checkpoint and failure stage");
        Check(Stage(failed.MediaClassificationStages!, MediaClassificationStageIds.PlanClassification).Elapsed is null,
            "read failures do not fabricate classification work");
        var tag = NewJob("media-ai"); tag.State = JobState.Running;
        await ThrowsAsync<ArgumentException>(() => new MediaTagJobService(new Engine()).ExecuteAsync(tag, _ => { }, default), "invalid tagging input propagates");
        Check(Stage(tag.MediaClassificationStages!, MediaClassificationStageIds.ScanFiles).State == StageState.Failed
            && Stage(tag.MediaClassificationStages!, MediaClassificationStageIds.PrepareModel).Elapsed is null, "tagging validation failure precedes model preparation");
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        internal void Advance(int seconds) => _ticks += seconds * TimeSpan.TicksPerSecond;
    }
    private sealed class Host : IMultiStageQueueHost
    {
        internal bool Allowed = true;
        internal readonly List<string> Calls = [];
        internal Job? LastJob;
        internal string? RetryStage;
        internal Action? OnCapabilityCheck;
        private Task Call(Job job, string command) { LastJob = job; Calls.Add(command); return Task.CompletedTask; }
        public bool CanStart(Job job) { OnCapabilityCheck?.Invoke(); return Allowed; }
        public bool CanPause(Job job) => Allowed;
        public bool CanResume(Job job) => Allowed;
        public bool CanStop(Job job) => Allowed;
        public bool CanRerun(Job job) => Allowed;
        public bool CanChangeOutput(Job job) => Allowed;
        public Task StartAsync(Job job) => Call(job, "start");
        public void Pause(Job job) => Call(job, "pause");
        public Task ResumeAsync(Job job) => Call(job, "resume");
        public void Stop(Job job) => Call(job, "stop");
        public Task RerunAsync(Job job, string? fromStageId) { RetryStage = fromStageId; return Call(job, "rerun"); }
        public Task ChangeOutputFolderAsync(Job job, string folder) => Call(job, "output:" + folder);
    }
    private sealed class Engine : IMediaEngine
    {
        internal int Probes;
        internal bool Cancel, Fail;
        public AppSettings Settings { get; } = new();
        public string FFmpeg => throw new NotSupportedException();
        public string FFprobe => throw new NotSupportedException();
        public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
        {
            Probes++; if (Cancel) throw new OperationCanceledException(); if (Fail) throw new IOException("模拟读取失败");
            return Task.FromResult(new MediaInfo(120, 640, 360, false, true, "", "synthetic"));
        }
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new NotSupportedException();
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
        public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
