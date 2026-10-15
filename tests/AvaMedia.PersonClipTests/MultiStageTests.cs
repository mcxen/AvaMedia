using AvaMedia.Core;
using Xunit;

[CollectionDefinition("PersonClipStages", DisableParallelization = true)]
public sealed class PersonClipStageCollection : ICollectionFixture<PersonClipStageFiles> { }

public sealed class PersonClipStageFiles : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "AvaMedia-person-stages-" + Guid.NewGuid().ToString("N"));
    public PersonClipStageFiles() { Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", Root); }
    public void Dispose() { Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", _previous); Directory.Delete(Root, true); }
    public Job Job()
    {
        var path = Path.Combine(Root, Guid.NewGuid() + ".mp4"); File.WriteAllText(path, "unit-test source fingerprint");
        return new() { FeatureId = "person-clip", Inputs = [path], Output = Path.Combine(Root, Guid.NewGuid() + ".json"),
            Options = new() { Format = "json", PersonClip = new() { AnalysisOnly = true } } };
    }
}

[Collection("PersonClipStages")]
public sealed class MultiStageTests(PersonClipStageFiles files)
{
    private static readonly MediaInfo Info = new(10, 1920, 1080, true, true, "{}", "h264");

    [Fact]
    public void Stages_accumulate_only_running_time_and_persist_cancellation_counts()
    {
        var job = files.Job(); var time = new ManualTime(); var stages = new PersonClipTaskState(job, time); job.PersonClipStages = stages;
        stages.Start(PersonClipStageIds.ReadVideo); time.Advance(2); stages.Complete(PersonClipStageIds.ReadVideo, "1920×1080 · h264 · 10 秒");
        stages.Start(PersonClipStageIds.PrepareModel); time.Advance(1); stages.Complete(PersonClipStageIds.PrepareModel, "NanoDet: CPU");
        stages.Start(PersonClipStageIds.Detect); time.Advance(3); stages.Update(6, 20, "帧", "6 / 20 帧");
        job.State = JobState.Paused; time.Advance(80);
        var paused = stages.Capture().Summary.Stages[2].Status;
        Assert.Equal(StageState.Paused, paused.State); Assert.False(paused.IsClockRunning); Assert.Equal(TimeSpan.FromSeconds(3), paused.Elapsed);
        job.State = JobState.Running; time.Advance(4); stages.Finish(JobState.Cancelled);
        var summary = stages.Capture().Summary;
        Assert.Equal(StageState.Cancelled, summary.Stages[2].Status.State);
        Assert.Equal("已取消 · 6 / 20 帧", summary.Stages[2].Status.Detail);
        Assert.Equal(TimeSpan.FromSeconds(7), summary.Stages[2].Status.Elapsed);
        Assert.Equal(StageState.Pending, summary.Stages[3].Status.State);
        Assert.Equal(TimeSpan.FromSeconds(10), TimeSpan.FromTicks(summary.Stages.Sum(s => s.Status.Elapsed?.Ticks ?? 0)));
        Assert.NotNull(summary.Stages[2].StartedUtc); Assert.NotNull(summary.Stages[2].CompletedUtc);
    }

    [Fact]
    public async Task All_excluded_video_reports_real_probe_and_skip_reasons_without_models()
    {
        var job = files.Job(); job.State = JobState.Running;
        job.Options.PersonClip = new() { AnalysisOnly = true, Detection = new(ExcludedRanges: [new(0, Info.Duration)]) };
        var stages = new PersonClipTaskState(job); job.PersonClipStages = stages;
        var engine = new FakeEngine();
        var result = await new PersonClipAnalysis(engine).AnalyzeStagesAsync(job.Inputs[0], job.Options.PersonClip.Detection, null, default, stages, job);
        var summary = stages.Capture().Summary;
        Assert.Equal(1, engine.Probes); Assert.Equal(Info, summary.Info); Assert.Empty(result.Segments);
        Assert.Equal(StageState.Done, summary.Stages[0].Status.State);
        Assert.All(summary.Stages.Skip(1).Take(3), s => Assert.Equal(StageState.Skipped, s.Status.State));
        Assert.Equal(0, PersonClipTaskState.PlannedFrames(Info, job.Options.PersonClip.Detection));
        Assert.Null(summary.Stages[1].Status.Elapsed);
    }

    [Fact]
    public async Task Retry_resolves_saved_detection_for_refine_and_final_ranges_for_export()
    {
        var job = files.Job(); var summary = await SavePredecessors(job);
        var rough = Result(job, partial: false);
        var checkpoint = new PersonClipDetectionCheckpoint(rough,
            [new(0, false, false, []), new(1, true, false, []), new(2, false, false, [])], [new(1, 2)]);
        await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "people-detection"), checkpoint, default);
        var models = new ReadyModels();
        var detect = await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Detect, models, default);
        Assert.Equal(PersonClipStageIds.Detect, detect.StageId); Assert.Null(detect.Detection);
        var refine = await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Refine, models, default);
        Assert.Equal(PersonClipStageIds.Refine, refine.StageId); Assert.NotNull(refine.Detection);
        Assert.Equal(3, refine.Detection.Samples.Length); Assert.Single(refine.Detection.Candidates);
        Assert.Null(refine.Result);
        var export = await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Export, models, default);
        Assert.Equal(PersonClipStageIds.Export, export.StageId); Assert.NotNull(export.Result); Assert.Null(export.Detection);
        var stages = new PersonClipTaskState(job);
        foreach (var stage in PersonClipStageIds.All.Take(4)) stages.Reuse(stage.Id, summary);
        Assert.All(stages.Capture().Summary.Stages.Take(4), stage =>
        { Assert.Equal(StageState.Done, stage.Status.State); Assert.StartsWith("复用上次结果", stage.Status.Detail); });
        File.Delete(AiTaskResults.PathFor(job, "people-detection"));
        Assert.Null((await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Refine, models, default)).StageId);
    }

    [Fact]
    public async Task Export_retry_executes_saved_analysis_without_probing_or_loading_models()
    {
        var job = files.Job(); await SavePredecessors(job); job.PersonClipStartStageId = PersonClipStageIds.Export;
        // An intentionally missing ffprobe ensures this unit never touches the media pipeline.
        var engine = new MediaEngine(new() { FFprobePath = Path.Combine(files.Root, "no-ffprobe"), AutoDetectGpu = false });
        await new PersonClipService(engine).ExecuteAsync(job, _ => { }, default);
        Assert.Null(job.PersonClipStartStageId);
        Assert.NotNull(job.PersonDetectionResult); Assert.Single(job.PersonDetectionResult.Result.Segments);
        var stages = job.PersonClipStages!.Capture().Summary.Stages;
        Assert.All(stages.Take(4), stage => Assert.StartsWith("复用上次结果", stage.Status.Detail));
        Assert.Equal(StageState.Skipped, stages[4].Status.State); Assert.Contains("仅检测", stages[4].Status.Detail);
        Assert.NotNull(await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(job.Output));
    }

    [Fact]
    public async Task Changed_source_missing_output_and_model_change_fall_back_to_full_execution()
    {
        var job = files.Job(); await SavePredecessors(job);
        var modelChanged = await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Detect, new ReadyModels("changed"), default);
        Assert.Null(modelChanged.StageId); Assert.Contains("模型", modelChanged.FallbackReason);
        File.Delete(AiTaskResults.PathFor(job, "people"));
        var missing = await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Export, new ReadyModels(), default);
        Assert.Null(missing.StageId); Assert.Contains("产出缺失", missing.FallbackReason);
        File.AppendAllText(job.Inputs[0], "changed");
        var changed = await PersonClipRetry.ResolveAsync(job, PersonClipStageIds.Detect, new ReadyModels(), default);
        Assert.Null(changed.StageId); Assert.Contains("源文件变化", changed.FallbackReason);
    }

    [Fact]
    public async Task Queue_stop_retains_partial_results_marker_and_durable_stage_time()
    {
        var job = files.Job(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new QueueService(new CancelExecutor(started));
        var running = queue.Run([job]); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.Stop(job)); await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobState.Cancelled, job.State);
        var restored = await AiTaskResults.LoadAsync<PersonClipStageSummary>(AiTaskResults.PathFor(job, "people-stages"));
        Assert.NotNull(restored); Assert.Equal(StageState.Cancelled, restored.Stages[2].Status.State);
        Assert.Contains("6 / 20 帧", restored.Stages[2].Status.Detail);
        var partial = await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(job.Output);
        Assert.NotNull(partial); Assert.True(partial.Result.IsPartial); Assert.Single(partial.Result.Segments);
        using var session = new PersonClipTaskSession(job, new FakeHost(), new FakeEngine(), new ModelStore(Path.Combine(files.Root, "models")));
        var logs = await session.ReadLogAsync();
        Assert.Contains(logs, e => e.StageId == PersonClipStageIds.Detect && e.Level == TaskLogLevel.Marker);
        Assert.Contains("已取消", await job.ReadLogAsync());
    }

    [Fact]
    public async Task Session_probes_queued_tasks_routes_commands_and_dispose_never_stops_job()
    {
        var job = files.Job(); var engine = new FakeEngine(); var host = new FakeHost();
        var session = new PersonClipTaskSession(job, host, engine, new ModelStore(Path.Combine(files.Root, "models")));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, _) => { if (session.View.Info is not null) changed.TrySetResult(); };
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MultiStageTaskState.Configuring, session.Snapshot.State);
        Assert.Equal(20, session.View.PlannedFrames); Assert.False(session.View.Settings.ModelInstalled);
        Assert.All(session.Snapshot.Stages, s => Assert.Equal(StageState.Pending, s.State));
        Assert.Null(session.Snapshot.StartedUtc); Assert.Null(session.Snapshot.Remaining);
        Assert.Contains("每秒 2 帧", session.Snapshot.Stages[2].Detail);
        await session.StartAsync(); Assert.Equal("start", host.LastCommand);
        session.Dispose(); Assert.Equal("start", host.LastCommand); Assert.Equal(JobState.Waiting, job.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StopAsync());
    }

    [Fact]
    public async Task Session_restores_terminal_stages_retry_command_and_legacy_logs()
    {
        var job = files.Job(); await SavePredecessors(job);
        var summary = (await AiTaskResults.LoadAsync<PersonClipStageSummary>(AiTaskResults.PathFor(job, "people-stages")))!;
        var state = new PersonClipTaskState(job); foreach (var stage in PersonClipStageIds.All.Take(4)) state.Reuse(stage.Id, summary);
        state.Start(PersonClipStageIds.Export); state.Update(17, 100, "%", "17%"); state.Finish(JobState.Failed, "export error");
        job.State = JobState.Failed; job.Error = "export error";
        using var session = new PersonClipTaskSession(job, new FakeHost(), new FakeEngine(), new ModelStore(Path.Combine(files.Root, "models")));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += (_, _) => { if (session.Snapshot.Commands.RetryStageId.Length > 0) ready.TrySetResult(); };
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PersonClipStageIds.Export, session.Snapshot.InterruptedStageId);
        Assert.Equal(PersonClipStageIds.Export, session.Snapshot.Commands.RetryStageId);
        Assert.False(session.Snapshot.IsClockRunning); Assert.True(session.View.CanAdjust);
        await session.RetryFromStageAsync(PersonClipStageIds.Export);
        File.Delete(AiTaskResults.PathFor(job, "people-log", "jsonl")); job.Log = "人物检测已中断";
        var logs = await session.ReadLogAsync(); var entry = Assert.Single(logs);
        Assert.Equal(PersonClipStageIds.Detect, entry.StageId); Assert.Equal(TaskLogLevel.Marker, entry.Level);
        Assert.Equal(DateTime.MinValue, entry.TimeUtc);
    }

    private async Task<PersonClipStageSummary> SavePredecessors(Job job)
    {
        var time = new ManualTime(); var state = new PersonClipTaskState(job, time); var source = new FileInfo(job.Inputs[0]);
        state.Start(PersonClipStageIds.ReadVideo); state.Source(Info, source.Length, source.LastWriteTimeUtc);
        time.Advance(1); state.Complete(PersonClipStageIds.ReadVideo, "1920×1080 · h264 · 10 秒");
        foreach (var stage in PersonClipStageIds.All.Skip(1).Take(3))
        { state.Start(stage.Id); time.Advance(1); state.Complete(stage.Id, stage.Title + "完成"); }
        var summary = state.Capture().Summary with { ModelStamps = new() { [ModelCatalog.NanoDetId] = "ready" } };
        await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "people-stages"), summary, default);
        await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "people"), new PersonDetectionTaskResult(Result(job), source.Length, source.LastWriteTimeUtc), default);
        return summary;
    }
    private static PersonClipResult Result(Job job, bool partial = false) => new(job.Inputs[0], Info,
        [new() { Start = 1, End = 3 }], 6, 0, 6, 0, "CPU") { IsPartial = partial, AnalyzedSeconds = partial ? 3 : 10 };
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class ReadyModels(string stamp = "ready") : IPersonClipModelAvailability
    {
        public string Stamp(string id) => stamp;
        public Task<bool> IsInstalledAsync(string id, CancellationToken ct) => Task.FromResult(true);
    }
    private sealed class FakeEngine : IMediaEngine
    {
        public int Probes;
        public AppSettings Settings { get; } = new();
        public string FFmpeg => "never-run-ffmpeg";
        public string FFprobe => "never-run-ffprobe";
        public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
        { Interlocked.Increment(ref Probes); return Task.FromResult(Info); }
        public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new NotSupportedException();
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
    }
    private sealed class CancelExecutor(TaskCompletionSource started) : IJobExecutor
    {
        public async Task Execute(Job job, Action<double> progress, CancellationToken ct)
        {
            var state = new PersonClipTaskState(job); job.PersonClipStages = state;
            state.Start(PersonClipStageIds.ReadVideo); state.Complete(PersonClipStageIds.ReadVideo, "read");
            state.Start(PersonClipStageIds.PrepareModel); state.Complete(PersonClipStageIds.PrepareModel, "CPU");
            state.Start(PersonClipStageIds.Detect); state.Update(6, 20, "帧", "6 / 20 帧");
            var source = new FileInfo(job.Inputs[0]); job.PersonDetectionResult = new(Result(job, true), source.Length, source.LastWriteTimeUtc);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { state.Finish(JobState.Cancelled); throw; }
        }
    }
    private sealed class FakeHost : IMultiStageQueueHost
    {
        public string LastCommand = "";
        public bool CanStart(Job job) => job.State == JobState.Waiting;
        public Task StartAsync(Job job) { LastCommand = "start"; return Task.CompletedTask; }
        public bool CanPause(Job job) => job.State == JobState.Running;
        public void Pause(Job job) { LastCommand = "pause"; }
        public bool CanResume(Job job) => job.State == JobState.Paused;
        public Task ResumeAsync(Job job) { LastCommand = "resume"; return Task.CompletedTask; }
        public bool CanStop(Job job) => true;
        public void Stop(Job job) { LastCommand = "stop"; }
        public bool CanRerun(Job job) => job.State is JobState.Completed or JobState.Failed or JobState.Cancelled;
        public Task RerunAsync(Job job, string? fromStageId) { LastCommand = fromStageId ?? "rerun"; return Task.CompletedTask; }
        public bool CanChangeOutput(Job job) => CanStart(job);
        public Task ChangeOutputFolderAsync(Job job, string folder) { LastCommand = "output"; return Task.CompletedTask; }
    }
}
