using System.Text.Json;
using AvaMedia.Core;

/// <summary>Backend units only: fake probe results, synthetic cue records, no native models or media processes.</summary>
internal static class SubtitleMultiStageChecks
{
    private static int _checks;
    public static async Task RunAsync()
    {
        var previous = Environment.GetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY");
        var root = Path.Combine(Path.GetTempPath(), "AvaMedia-subtitle-stages-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", root);
        try
        {
            await PlansAndCommands(root);
            await ExecutionClocksAndRestore(root);
            await InterruptedAndRerun(root);
            await ExecutionFailures(root);
            await ReviewedExecution(root);
            Console.WriteLine($"Passed {_checks} subtitle multi-stage assertions.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", previous);
            Directory.Delete(root, true);
        }
    }

    private static async Task PlansAndCommands(string root)
    {
        var job = NewJob(root); var host = new FakeHost();
        var session = new SubtitleTaskSession(job, host);
        Check(session.FeatureId == "auto-subtitle", "subtitle feature id");
        Check(session.Stages.Select(s => s.Id).Distinct().Count() == 5 && session.Stages.All(s => !s.CanRetryFrom), "five distinct stages without unsupported retry");
        Check(session.Snapshot.State == MultiStageTaskState.Configuring && session.Snapshot.CurrentStageIndex == -1, "queued task has no active stage");
        Check(session.Snapshot.Stages.All(s => s.State == StageState.Pending && s.Current is null && s.Total is null && s.Elapsed is null && s.Fraction is null), "queued plan never invents counts or time");
        Check(session.Snapshot.StartedUtc is null && session.Snapshot.Remaining is null && session.Snapshot.Runtime.Backend == "", "queued environment and estimate remain unknown");
        Check((await session.ReadLogAsync()).Count == 0, "queued task has no execution logs");
        await session.StartAsync(); Check(host.Last == "start" && host.Job == job, "start delegates the exact job");
        await session.PauseAsync(); Check(host.Last == "pause", "pause delegates");
        await session.ResumeAsync(); Check(host.Last == "resume", "resume delegates");
        await session.StopAsync(); Check(host.Last == "stop", "stop delegates");
        await session.RerunAsync(); Check(host.Last == "rerun" && host.FromStage is null, "rerun requests every stage");
        await session.ChangeOutputFolderAsync(root); Check(host.Last == "output" && host.Folder == root, "output command delegates");
        await Throws<InvalidOperationException>(() => session.RetryFromStageAsync(SubtitleStageIds.Recognize));
        Check(host.Last == "output", "unsupported stage retry never reaches host");
        host.Allowed = false;
        await Throws<InvalidOperationException>(() => session.StartAsync());
        await Throws<InvalidOperationException>(() => session.PauseAsync());
        await Throws<InvalidOperationException>(() => session.ResumeAsync());
        await Throws<InvalidOperationException>(() => session.StopAsync());
        await Throws<InvalidOperationException>(() => session.RerunAsync());
        await Throws<InvalidOperationException>(() => session.ChangeOutputFolderAsync(root));
        session.Dispose(); session.Dispose();
        Check(host.Last == "output" && job.State == JobState.Waiting, "disposing observer never stops execution");
        await Throws<ObjectDisposedException>(() => session.StartAsync());
        await Throws<ObjectDisposedException>(() => session.ReadLogAsync());
        await Throws<ArgumentException>(() => Task.FromResult(new SubtitleTaskSession(new Job(), host)));
    }

    private static async Task ExecutionClocksAndRestore(string root)
    {
        var job = NewJob(root); job.State = JobState.Running;
        var time = new ManualTime();
        using var state = new SubtitleTaskState(job, time: time);
        state.Start(SubtitleStageIds.ReadAudio); time.Advance(2); state.Complete(SubtitleStageIds.ReadAudio, "所选音轨 · 10 秒");
        state.Start(SubtitleStageIds.PrepareModel);
        state.Update("下载模型", 40, 100, "字节");
        Check(state.Capture().Stages[1].Fraction == .4, "model download uses reported bytes");
        state.Update("加载模型");
        Check(state.Capture().Stages[1].Current is null && state.Capture().Stages[1].Fraction is null, "loading clears obsolete download counts");
        state.Backend("Whisper CPU · Silero CPU", "GPU 加载失败，回退 CPU");
        time.Advance(1); state.Complete(SubtitleStageIds.PrepareModel, "模型已加载");
        state.Start(SubtitleStageIds.Recognize); time.Advance(3); state.Update("已识别 6 / 10 秒", 6, 10, "秒");
        var before = state.Capture();
        job.State = JobState.Paused; time.Advance(80);
        var paused = state.Capture().Stages[2];
        Check(paused.State == StageState.Paused && !paused.IsClockRunning && paused.Elapsed == TimeSpan.FromSeconds(3), "paused stage excludes idle time");
        using (var session = new SubtitleTaskSession(job, new FakeHost()))
        {
            Check(session.Snapshot.State == MultiStageTaskState.Paused && session.Snapshot.CurrentStageIndex == 2, "pause is visible to session");
            Check(session.Snapshot.Runtime.Backend.Contains("CPU") && session.Snapshot.Runtime.Note.Contains("回退"), "runtime uses the inference callback");
            Check(session.Snapshot.Remaining is null, "paused task exposes no remaining estimate");
            job.State = JobState.Running;
            job.Estimate = new(ProgressEstimateState.Available, TimeSpan.FromSeconds(9));
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += (_, _) => { if (session.Snapshot.State == MultiStageTaskState.Running) changed.TrySetResult(); };
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(session.Snapshot.Remaining == TimeSpan.FromSeconds(9) && session.Snapshot.Revision > 1, "coalesced changes publish the queue estimate");
        }
        time.Advance(4); state.Finish(JobState.Cancelled); job.State = JobState.Cancelled;
        Check(before.Stages[2].State == StageState.Active && before.Stages[2].Elapsed == TimeSpan.FromSeconds(3), "earlier snapshots remain immutable");
        var summary = state.Capture();
        Check(summary.Stages[2].State == StageState.Cancelled && summary.Stages[2].Current == 6 && summary.Stages[2].Elapsed == TimeSpan.FromSeconds(7), "cancel retains real counts and running time");
        Check(summary.Stages[3].State == StageState.Pending && summary.CompletedUtc is not null, "cancel leaves unexecuted stages pending");
        var liveLogs = state.ReadLog();
        Check(liveLogs.Any(e => e.StageId == SubtitleStageIds.Recognize && e.Level == TaskLogLevel.Marker), "cancel logs carry their stage and marker level");
        Check(liveLogs.Any(e => e.Level == TaskLogLevel.Warning && e.Message.Contains("回退")), "backend fallback produces a warning log");
        job.SubtitleStages = null;
        File.AppendAllText(AiTaskResults.PathFor(job, "subtitle-log", "jsonl"), "{partial");
        using var restored = new SubtitleTaskSession(job, new FakeHost());
        Check(restored.Snapshot.InterruptedStageId == SubtitleStageIds.Recognize && !restored.Snapshot.IsClockRunning, "reopened session restores cancellation without a live clock");
        Check(restored.Snapshot.Elapsed == TimeSpan.FromSeconds(10), "durable elapsed time matches stage totals");
        Check((await restored.ReadLogAsync()).SequenceEqual(liveLogs), "structured log restore tolerates an interrupted last line");
        Check(restored.Snapshot.Commands.RetryStageId == "" && restored.Snapshot.RetainedStageIds.Count == 0, "no unsupported checkpoint retention is advertised");
    }

    private static async Task InterruptedAndRerun(string root)
    {
        var job = NewJob(root); job.State = JobState.Running;
        var time = new ManualTime();
        using (var state = new SubtitleTaskState(job, time: time))
        {
            state.Start(SubtitleStageIds.Recognize); time.Advance(3); state.Update("4 / 10 秒", 4, 10, "秒");
        }
        job.SubtitleStages = null; job.State = JobState.Failed; job.Error = "执行进程中断";
        using (var session = new SubtitleTaskSession(job, new FakeHost()))
        {
            Check(session.Snapshot.Stages[2].State == StageState.Failed && session.Snapshot.InterruptedStageId == SubtitleStageIds.Recognize, "persisted active stage resolves to interruption point");
            Check(session.Snapshot.Stages[2].Detail == job.Error && session.Snapshot.Stages[2].Current == 4, "interruption uses queue error and retained count");
            Check(!session.Snapshot.IsClockRunning && session.Snapshot.CompletedUtc is null, "process interruption invents no completion timestamp");
        }
        job.State = JobState.Waiting;
        using (var queued = new SubtitleTaskSession(job, new FakeHost()))
        {
            Check(queued.Snapshot.Stages.All(s => s.State == StageState.Pending && s.Elapsed is null) && queued.Snapshot.StartedUtc is null, "requeued job shows a fresh plan");
            Check((await queued.ReadLogAsync()).Count == 0, "requeued job hides previous run logs");
        }
        job.State = JobState.Running;
        using (var next = new SubtitleTaskState(job))
        {
            next.Start(SubtitleStageIds.ReadAudio); next.Finish(JobState.Failed, "新的探测错误");
            Check(next.ReadLog().All(e => e.StageId == SubtitleStageIds.ReadAudio), "new execution resets in-memory logs");
        }
        job.SubtitleStages = null; job.State = JobState.Failed;
        using var reset = new SubtitleTaskSession(job, new FakeHost());
        Check((await reset.ReadLogAsync()).All(e => e.StageId == SubtitleStageIds.ReadAudio), "new execution resets durable logs");
        File.WriteAllText(AiTaskResults.PathFor(job, "subtitle-stages"), "{partial");
        using var malformed = new SubtitleTaskSession(job, new FakeHost());
        Check(malformed.Snapshot.Stages.All(s => s.State == StageState.Pending), "incomplete summary never fabricates stage outcomes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => reset.ReadLogAsync(cancelled.Token));
    }

    private static async Task ExecutionFailures(string root)
    {
        var job = NewJob(root); job.State = JobState.Running;
        var engine = new FakeEngine(new(10, 0, 0, false, false, "{}"));
        await Throws<ArgumentException>(() => new SpeechSubtitleService(engine).ExecuteAsync(job, _ => { }, default));
        var failed = job.SubtitleStages!.Capture();
        Check(engine.Probes == 1 && failed.Stages[0].State == StageState.Failed && failed.Stages.Skip(1).All(s => s.State == StageState.Pending), "real probe failure is assigned only to read-audio");
        Check(job.SubtitleStages.ReadLog().Any(e => e.StageId == SubtitleStageIds.ReadAudio && e.Level == TaskLogLevel.Error), "execution failure writes a stage error log");
        job = NewJob(root); job.State = JobState.Running;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => new SpeechSubtitleService(new FakeEngine()).ExecuteAsync(job, _ => { }, cancelled.Token));
        Check(job.SubtitleStages!.Capture().Stages[0].State == StageState.Cancelled, "pre-cancelled execution records its actual cancellation stage");
    }

    private static async Task ReviewedExecution(string root)
    {
        var job = ReviewedJob(root);
        await new SpeechSubtitleService(new FakeEngine()).ExecuteAsync(job, _ => { }, default);
        var stages = job.SubtitleStages!.Capture();
        Check(stages.Stages.Select(s => s.State).SequenceEqual(new[] { StageState.Done, StageState.Skipped, StageState.Skipped, StageState.Done, StageState.Done }), "reviewed cue execution records actual bypasses and completed stages");
        Check(stages.Stages[1].Elapsed is null && stages.Stages[2].Elapsed is null && stages.Runtime.Backend == "", "bypassed inference has no fake model clock or backend");
        Check(stages.Stages[3].Current == 1 && stages.CompletedUtc is not null, "timeline records actual reviewed cue count");
        Check(stages.Stages.All(s => !s.IsClockRunning) && job.SubtitleResult?.Cues.Length == 1, "completed service retains results and stops stage clocks");

        job = ReviewedJob(root); job.Options.Transcription!.ReviewedSourceLength++;
        await Throws<IOException>(() => new SpeechSubtitleService(new FakeEngine()).ExecuteAsync(job, _ => { }, default));
        Check(job.SubtitleStages!.Capture().Stages[3].State == StageState.Failed && job.SubtitleStages.Capture().Stages[4].State == StageState.Pending, "changed reviewed source fails timeline before writing");
        Check(job.SubtitleResult is null, "invalid reviewed source never publishes a transcript");

        job = ReviewedJob(root); job.Options.Transcription!.ReviewedCues = [new(TimeSpan.Zero, TimeSpan.FromSeconds(20), "超过范围")];
        await Throws<ArgumentException>(() => new SpeechSubtitleService(new FakeEngine()).ExecuteAsync(job, _ => { }, default));
        Check(job.SubtitleStages!.Capture().Stages[3].State == StageState.Failed, "out-of-range reviewed cue fails timeline");
        Check(job.SubtitleResult is null, "invalid reviewed timeline never publishes a transcript");

        job = ReviewedJob(root); job.Output = Path.Combine(root, "absent", "output.srt");
        await Throws<DirectoryNotFoundException>(() => new SpeechSubtitleService(new FakeEngine()).ExecuteAsync(job, _ => { }, default));
        Check(job.SubtitleStages!.Capture().Stages[4].State == StageState.Failed && job.SubtitleResult?.Cues.Length == 1, "write failure retains transcript and reports the write stage");
    }

    private static Job NewJob(string root)
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".txt"); File.WriteAllText(path, "not media: fingerprint fixture");
        return new() { FeatureId = "auto-subtitle", Inputs = [path], Output = Path.Combine(root, Guid.NewGuid() + ".srt"),
            Options = new() { Format = "srt", Transcription = new() { Model = SpeechModel.Tiny, Language = "zh" } } };
    }
    private static Job ReviewedJob(string root)
    {
        var job = NewJob(root); job.State = JobState.Running;
        var file = new FileInfo(job.Inputs[0]);
        job.Options.Transcription!.ReviewedCues = [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "校对文字")];
        job.Options.Transcription.ReviewedSourceLength = file.Length;
        job.Options.Transcription.ReviewedSourceWriteUtc = file.LastWriteTimeUtc;
        return job;
    }
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception(label);
        _checks++; Console.WriteLine("PASS " + label);
    }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { _checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class FakeHost : IMultiStageQueueHost
    {
        public bool Allowed = true;
        public string Last = "", Folder = "";
        public Job? Job;
        public string? FromStage;
        private Task Record(Job job, string command) { Job = job; Last = command; return Task.CompletedTask; }
        public bool CanStart(Job job) => Allowed;
        public Task StartAsync(Job job) => Record(job, "start");
        public bool CanPause(Job job) => Allowed;
        public void Pause(Job job) => Record(job, "pause");
        public bool CanResume(Job job) => Allowed;
        public Task ResumeAsync(Job job) => Record(job, "resume");
        public bool CanStop(Job job) => Allowed;
        public void Stop(Job job) => Record(job, "stop");
        public bool CanRerun(Job job) => Allowed;
        public Task RerunAsync(Job job, string? fromStageId) { FromStage = fromStageId; return Record(job, "rerun"); }
        public bool CanChangeOutput(Job job) => Allowed;
        public Task ChangeOutputFolderAsync(Job job, string folder) { Folder = folder; return Record(job, "output"); }
    }
    private sealed class FakeEngine(MediaInfo? info = null) : IMediaEngine
    {
        public int Probes;
        public AppSettings Settings { get; } = new();
        public string FFmpeg => throw new Exception("Unit test must not invoke FFmpeg.");
        public string FFprobe => throw new Exception("Unit test must not invoke FFprobe.");
        public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
        { Probes++; return Task.FromResult(info ?? new(10, 0, 0, HasAudio: true, HasVideo: false, "{}")); }
        public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new Exception("Unit test must not convert media.");
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new NotSupportedException();
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
    }
}
