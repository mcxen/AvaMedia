using AvaMedia.Core;

#if ORIENTATION_STAGE_TESTS
await OrientationStageTests.Run();
#endif

internal static class OrientationStageTests
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { _checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    public static async Task Run()
    {
        await Commands();
        TimingAndSnapshots();
        foreach (int? angle in new int?[] { 0, 90, null }) await Completion(angle);
        await Interruption();
        await SourceChanged();
        await SaveFailure();
        Console.WriteLine($"PASS: {_checks} orientation stage unit checks (no media or UI execution).");
    }
    private static async Task Commands()
    {
        using var files = new Files(); var job = files.Job(); var host = new Host();
        using var session = new OrientationTaskSession(job, host);
        Check(session.FeatureId == "rotate" && session.Stages.Count == 5, "Wrong stage contract");
        Check(session.Snapshot.State == MultiStageTaskState.Configuring
            && session.Snapshot.Stages.All(s => s.State == StageState.Pending && s.Elapsed is null)
            && session.Snapshot.StartedUtc is null && session.Snapshot.Runtime.Backend == "", "Invented queued observations");
        await session.StartAsync(); Check(host.Command == "start", "Start not delegated");
        await session.PauseAsync(); Check(host.Command == "pause", "Pause not delegated");
        await session.ResumeAsync(); Check(host.Command == "resume", "Resume not delegated");
        await session.StopAsync(); Check(host.Command == "stop", "Stop not delegated");
        await session.RerunAsync(); Check(host.Command == "rerun" && host.FromStage is null, "Rerun not delegated");
        await session.ChangeOutputFolderAsync(files.Root); Check(host.Command == files.Root, "Output not delegated");
        Check(session.Snapshot.Commands.RetryStageId == "" && session.Stages.All(s => !s.CanRetryFrom), "Unsupported retry advertised");
        await Reject<InvalidOperationException>(() => session.RetryFromStageAsync(OrientationStageIds.Sample));
        host.Allow = false; await Reject<InvalidOperationException>(() => session.StartAsync());
        host.Allow = true;
        session.Dispose(); await Reject<ObjectDisposedException>(() => session.StartAsync());
        Check(host.Command == files.Root && job.State == JobState.Waiting, "Dispose changed execution");
        await Reject<ArgumentException>(() => Task.FromResult(new OrientationTaskSession(new(), host)));
    }
    private static void TimingAndSnapshots()
    {
        using var files = new Files(); var job = files.Job(); job.State = JobState.Running;
        var time = new Clock(); var state = new OrientationTaskState(job, time); job.OrientationStages = state;
        state.Start(OrientationStageIds.ReadVideo); time.Advance(3);
        var first = state.Capture();
        Check(first.Stages[0].Elapsed == TimeSpan.FromSeconds(3) && first.Stages[0].IsClockRunning, "Running clock is wrong");
        job.State = JobState.Paused; time.Advance(70);
        Check(state.Capture().Stages[0].Elapsed == TimeSpan.FromSeconds(3)
            && state.Capture().Stages[0].State == StageState.Paused, "Pause counted running time");
        job.State = JobState.Running; time.Advance(2);
        state.Complete(OrientationStageIds.ReadVideo, "read");
        state.Start(OrientationStageIds.Sample);
        state.Update(new(2, 8, 1));
        var copy = state.Capture(); state.Update(new(3, 8, 2));
        Check(copy.Stages[1].Current == 2 && copy.Stages[1].Fraction == .25, "Snapshot was mutated");
        Check(first.Stages[0].State == StageState.Active, "Previous stage array was mutated");
        time.Advance(1); job.Error = "detector failed"; job.State = JobState.Failed;
        var final = state.Capture();
        Check(final.Stages[0].Elapsed == TimeSpan.FromSeconds(5) && final.Stages[1].Elapsed == TimeSpan.FromSeconds(1)
            && final.Stages[1].State == StageState.Failed && !final.Stages[1].IsClockRunning, "Failure clock/state is wrong");
        using var session = new OrientationTaskSession(job, new Host());
        Check(session.Snapshot.InterruptedStageId == OrientationStageIds.Sample
            && session.Snapshot.Error == "detector failed", "Failure attribution is wrong");
        job.OrientationStages = null;
        using var restored = new OrientationTaskSession(job, new Host());
        Check(restored.Snapshot.Stages[1].Current == 3 && !restored.Snapshot.IsClockRunning, "Persisted observations lost");
        job.State = JobState.Waiting;
        using var rerun = new OrientationTaskSession(job, new Host());
        Check(rerun.Snapshot.Stages.All(s => s.State == StageState.Pending && s.Current is null)
            && rerun.Snapshot.StartedUtc is null, "Rerun exposes stale stages");
    }
    private static async Task Completion(int? angle)
    {
        using var files = new Files(); var job = files.Job(); job.State = JobState.Running;
        var expected = new VideoOrientationResult(angle, angle is null ? OrientationReliability.Unknown : OrientationReliability.High,
            4, angle is null ? 0 : 4, angle is null ? 0 : 4, "真实的模拟检测结果") { Backend = "fixture backend", FallbackReason = "fixture fallback" };
        var detector = new Detector((_, progress, _) =>
        {
            progress!.Report(new(4, 8, 2) { PreviewResult = expected });
            Check(job.Progress == 45 && job.OrientationStages!.Capture().Stages[1].Total == 8, "Sampling plan progress is wrong");
            Check(job.OrientationResult?.IsPartial == true, "Partial result unavailable");
            progress.Report(new(4, 4, 2) { IsFinalDecision = true });
            Check(job.OrientationStages!.Capture().Stages[2].State == StageState.Active, "Final decision stage not entered");
            return Task.FromResult(expected);
        });
        using var session = new OrientationTaskSession(job, new Host());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, _) => { if (session.Snapshot.State == MultiStageTaskState.Completed) changed.TrySetResult(); };
        await new OrientationTaskService(new Engine(), detector).Execute(job, p => job.Progress = p, default);
        job.State = JobState.Completed;
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = session.Snapshot;
        Check(snapshot.Stages.Take(4).All(s => s.State == StageState.Done), "Completed stages incorrect");
        Check(snapshot.Stages[1].Current == 4 && snapshot.Stages[1].Total == 4 && snapshot.Stages[1].Elapsed is not null, "Actual sample counters lost");
        var export = snapshot.Stages[4];
        Check(export.State == StageState.Skipped && export.Elapsed is null
            && export.Detail.Contains(angle == 0 ? "无需旋转" : angle is null ? "手动确认" : "仅检测"), "Incorrect export skip reason");
        Check(snapshot.Runtime.Backend == expected.Backend && snapshot.Runtime.Note == expected.FallbackReason, "Runtime was not observed");
        Check(snapshot.CompletedUtc is not null && !snapshot.IsClockRunning && job.OrientationResult?.IsPartial == false, "Completion incorrect");
        var saved = await AiTaskResults.LoadAsync<OrientationTaskResult>(job.Output);
        Check(saved?.Detection.Rotation == angle && saved?.Detection.Reason == expected.Reason, "Saved result changed");
        var log = await session.ReadLogAsync();
        Check(log.Any(e => e.StageId == OrientationStageIds.Export && e.Message == export.Detail)
            && log.All(e => e.TimeUtc.Kind == DateTimeKind.Utc), "Structured logs missing");
        await File.AppendAllTextAsync(AiTaskResults.PathFor(job, "orientation-log", "jsonl"), "{partial");
        Check((await session.ReadLogAsync()).Count == log.Count, "Partial log broke reading");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Reject<OperationCanceledException>(() => session.ReadLogAsync(cancel.Token));
        job.OrientationStages = null;
        using var restored = new OrientationTaskSession(job, new Host());
        Check(restored.Snapshot.Stages[4].State == StageState.Skipped && restored.Snapshot.Runtime.Backend == expected.Backend, "Completed restore failed");
    }
    private static async Task Interruption()
    {
        foreach (var cancelled in new[] { true, false })
        {
            using var files = new Files(); var job = files.Job(); job.State = JobState.Running;
            var detector = new Detector((_, progress, ct) =>
            {
                progress!.Report(new(1, 8, .5));
                return Task.FromException<VideoOrientationResult>(cancelled ? new OperationCanceledException(ct) : new InvalidDataException("inference failed"));
            });
            await Reject<Exception>(() => new OrientationTaskService(new Engine(), detector).Execute(job, _ => { }, default));
            job.Error = cancelled ? "cancelled" : "inference failed";
            job.State = cancelled ? JobState.Cancelled : JobState.Failed;
            using var session = new OrientationTaskSession(job, new Host());
            Check(session.Snapshot.InterruptedStageId == OrientationStageIds.Sample
                && session.Snapshot.Stages[1].Current == 1 && !session.Snapshot.IsClockRunning, "Interrupted sample attribution/count incorrect");
            Check(session.Snapshot.Stages.Skip(2).All(s => s.State == StageState.Pending), "Unexecuted stages fabricated");
            job.OrientationStages = null;
            using var restored = new OrientationTaskSession(job, new Host());
            Check(restored.Snapshot.InterruptedStageId == OrientationStageIds.Sample, "Interrupted stage restore failed");
        }
        using var invalid = new Files(); var missing = invalid.Job(); File.Delete(missing.Inputs[0]);
        await Reject<FileNotFoundException>(() => new OrientationTaskService(new Engine()).Execute(missing, _ => { }, default));
        Check(missing.OrientationStages!.Capture().Stages[0].State == StageState.Failed, "Validation failure not attributed to reading");
    }
    private static async Task SourceChanged()
    {
        using var files = new Files(); var job = files.Job(); job.State = JobState.Running;
        var detector = new Detector((path, _, _) =>
        {
            File.AppendAllText(path, "changed");
            return Task.FromResult(new VideoOrientationResult(90, OrientationReliability.High, 4, 4, 4, "certain"));
        });
        await Reject<IOException>(() => new OrientationTaskService(new Engine(), detector).Execute(job, _ => { }, default));
        Check(job.OrientationStages!.Capture().Stages[2].State == StageState.Failed && !File.Exists(job.Output), "Changed source accepted");
    }
    private static async Task SaveFailure()
    {
        using var files = new Files(); var job = files.Job(); job.State = JobState.Running;
        var detector = new Detector((_, _, _) =>
        {
            Directory.CreateDirectory(job.Output);
            return Task.FromResult(new VideoOrientationResult(0, OrientationReliability.High, 4, 4, 4, "upright"));
        });
        await Reject<IOException>(() => new OrientationTaskService(new Engine(), detector).Execute(job, _ => { }, default));
        Check(job.OrientationStages!.Capture().Stages[3].State == StageState.Failed, "Save failure attributed to wrong stage");
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "avamedia-orientation-" + Guid.NewGuid().ToString("N"));
        private Job? _job;
        public Job Job()
        {
            Directory.CreateDirectory(Root); var path = Path.Combine(Root, "source.mp4"); File.WriteAllText(path, "fake input; never decoded");
            _job = new() { FeatureId = "rotate", Inputs = [path], Options = new()
            { Format = "json", Orientation = new("mp4", Root, false, false) } };
            _job.Output = AiTaskResults.PathFor(_job, "orientation"); return _job;
        }
        public void Dispose()
        {
            if (_job is not null)
            {
                _job.OrientationStages = null;
                var results = Path.GetDirectoryName(_job.Output)!; if (Directory.Exists(results)) Directory.Delete(results, true);
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class Host : IMultiStageQueueHost
    {
        public string Command = "";
        public string? FromStage;
        public bool Allow = true;
        public bool CanStart(Job job) => Allow;
        public Task StartAsync(Job job) { Command = "start"; return Task.CompletedTask; }
        public bool CanPause(Job job) => Allow;
        public void Pause(Job job) => Command = "pause";
        public bool CanResume(Job job) => Allow;
        public Task ResumeAsync(Job job) { Command = "resume"; return Task.CompletedTask; }
        public bool CanStop(Job job) => Allow;
        public void Stop(Job job) => Command = "stop";
        public bool CanRerun(Job job) => Allow;
        public Task RerunAsync(Job job, string? fromStageId) { Command = "rerun"; FromStage = fromStageId; return Task.CompletedTask; }
        public bool CanChangeOutput(Job job) => Allow;
        public Task ChangeOutputFolderAsync(Job job, string folder) { Command = folder; return Task.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
        public void Advance(int seconds) => _seconds += seconds;
    }
    private sealed class Detector(Func<string, IProgress<OrientationDetectionProgress>?, CancellationToken, Task<VideoOrientationResult>> detect) : IVideoOrientationDetector
    {
        public Task<VideoOrientationResult> DetectAsync(string path, MediaInfo info,
            IProgress<OrientationDetectionProgress>? progress = null, CancellationToken ct = default) => detect(path, progress, ct);
    }
    private sealed class Engine : IMediaEngine
    {
        public AppSettings Settings { get; } = new();
        public string FFmpeg => throw new Exception("Unit checks must not launch FFmpeg");
        public string FFprobe => throw new Exception("Unit checks must not launch FFprobe");
        public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaInfo(8, 640, 360, true, true, "{}", VideoStreamIndex: videoStreamIndex, AudioStreamIndex: audioStreamIndex)); }
        public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new Exception("No media conversion allowed");
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default,
            bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new Exception("No media preview allowed");
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new Exception("No media preview allowed");
    }
}
