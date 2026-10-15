using System.Text.Json;
using AvaMedia.Core;
using Xunit;

[Collection("PersonClipStages")]
public sealed class JobStageSummaryTests(PersonClipStageFiles files)
{
    private static readonly MediaInfo Info = new(10, 640, 360, true, true, "{}");

    private static Job Bind(Job job, JobStageSummaryScheduler scheduler)
    {
        job.StageSummaryPublisher!.Detach(); job.StageSummaryPublisher = null; job.StageSummary = null;
        JobStageSummaries.Attach(job, scheduler);
        return job;
    }

    private static JobStageSummary Summary(Job job)
    {
        var s = Assert.IsType<JobStageSummary>(job.StageSummary);
        Assert.Equal(job.State, s.State);
        Assert.InRange(s.OverallFraction ?? 0, 0, 1);
        if (s.State == JobState.Waiting)
        {
            Assert.DoesNotContain(s.Stages, x => x.State is StageState.Active or StageState.Failed);
            Assert.Empty(s.Error); Assert.Empty(s.FailedStageId); Assert.Empty(s.RetryStageId);
            Assert.Null(s.Remaining); Assert.Equal(TimeSpan.Zero, s.Elapsed); Assert.False(s.IsClockRunning);
        }
        if (s.State == JobState.Completed)
        {
            Assert.All(s.Stages, x => Assert.True(x.State is StageState.Done or StageState.Skipped));
            Assert.Equal(1, s.OverallFraction); Assert.Equal(-1, s.CurrentIndex); Assert.False(s.IsClockRunning);
        }
        if (s.State is JobState.Running or JobState.Paused or JobState.Failed or JobState.Cancelled)
        {
            var expected = s.State switch { JobState.Running => StageState.Active, JobState.Paused => StageState.Paused,
                JobState.Failed => StageState.Failed, _ => StageState.Cancelled };
            Assert.Equal(expected, s.Current!.State);
            Assert.Single(s.Stages, x => x.State == expected);
            Assert.Equal(s.State == JobState.Running, s.IsClockRunning);
            if (s.State != JobState.Running) Assert.DoesNotContain(s.Stages, x => x.State == StageState.Active);
        }
        return s;
    }

    [Fact]
    public void Single_stage_wait_run_pause_resume_complete_and_attach_are_consistent()
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var job = Bind(new(), scheduler);
        Assert.Equal(Catalog.Find("mp4").Label, Assert.Single(Summary(job).Stages).Title);
        var original = job.StageSummary; JobStageSummaries.Attach(job); Assert.Same(original, job.StageSummary);
        job.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Job.State)) Summary(job); };
        job.State = JobState.Running;
        time.Advance(2); job.Progress = 23; job.ProgressDetail = "23 / 100 帧";
        job.Estimate = new(ProgressEstimateState.Available, TimeSpan.FromSeconds(7));
        scheduler.Pulse(); time.Advance(.25); scheduler.Pulse();
        var s = Summary(job);
        Assert.Equal(.23, s.StageFraction); Assert.Equal(.23, s.OverallFraction);
        Assert.Equal("23 / 100 帧", s.ActivityText); Assert.Equal(TimeSpan.FromSeconds(7), s.Remaining);
        job.State = JobState.Paused;
        var paused = Summary(job); time.Advance(100); scheduler.Pulse();
        Assert.Equal(paused.Elapsed, Summary(job).Elapsed); Assert.Equal(paused.Current!.Elapsed, Summary(job).Current!.Elapsed);
        Assert.Null(Summary(job).Remaining);
        job.State = JobState.Running; time.Advance(3); job.Progress = 89; scheduler.Pulse();
        Assert.Equal(.89, Summary(job).StageFraction); Assert.Equal(TimeSpan.FromSeconds(5.25), Summary(job).Elapsed);
        job.Progress = 100; job.State = JobState.Completed;
        Assert.Equal(TimeSpan.FromSeconds(5.25), Summary(job).Elapsed);
        time.Advance(10); scheduler.Pulse(); Assert.Equal(TimeSpan.FromSeconds(5.25), Summary(job).Elapsed);
    }

    [Theory]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public void Interruption_is_immediate_and_full_rerun_clears_old_failure(JobState state)
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var job = Bind(new(), scheduler); job.State = JobState.Running; job.Progress = 45;
        job.Error = "codec failure"; job.State = state;
        var interrupted = Summary(job);
        Assert.Null(interrupted.Remaining); Assert.Null(interrupted.StageFraction);
        if (state == JobState.Failed) { Assert.Equal("codec failure", interrupted.Error); Assert.Equal("mp4", interrupted.FailedStageId); }
        else { Assert.Empty(interrupted.Error); Assert.Empty(interrupted.FailedStageId); }
        time.Advance(50); scheduler.Pulse(); Assert.Equal(interrupted.Elapsed, Summary(job).Elapsed);
        job.State = JobState.Waiting; Summary(job);
        Assert.All(job.StageSummary!.Stages, x => { Assert.Equal(StageState.Pending, x.State); Assert.DoesNotContain("failure", x.Detail); });
        job.State = JobState.Running; time.Advance(.25); job.Progress = 12; scheduler.Pulse();
        Assert.Equal(.12, Summary(job).StageFraction); Assert.Empty(Summary(job).Error);
        job.State = JobState.Completed; Summary(job);
        job.State = JobState.Waiting; Summary(job);
    }

    [Fact]
    public void Progress_is_bounded_per_job_and_trailing_value_arrives_while_state_is_synchronous()
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var jobs = Enumerable.Range(0, 20).Select(_ => Bind(new(), scheduler)).ToArray();
        var counts = new Dictionary<Job, int>();
        foreach (var job in jobs)
        {
            job.State = JobState.Running; counts[job] = 0;
            job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Job.StageSummary)) counts[job]++; };
        }
        for (int tick = 0; tick < 100; tick++)
        {
            foreach (var job in jobs) for (int burst = 0; burst < 10; burst++) job.Progress = tick + burst / 10d;
            time.Advance(.01); scheduler.Pulse();
        }
        foreach (var job in jobs) { job.Progress = 42.125; Assert.InRange(counts[job], 1, 4); Assert.NotEqual(job.Progress / 100, job.StageSummary!.OverallFraction); }
        time.Advance(.25); scheduler.Pulse();
        foreach (var job in jobs)
        {
            Assert.InRange(counts[job], 1, 5); Assert.Equal(job.Progress / 100, Summary(job).OverallFraction);
            var before = counts[job]; job.State = JobState.Paused; Assert.Equal(before + 1, counts[job]); Summary(job);
            job.State = JobState.Running; Assert.Equal(before + 2, counts[job]); Summary(job);
            job.State = JobState.Completed; Assert.Equal(before + 3, counts[job]); Summary(job);
        }
    }

    [Fact]
    public async Task Person_clip_uses_real_stages_and_validates_export_retry_then_retains_done_stages()
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var job = Bind(files.Job(), scheduler); job.State = JobState.Running;
        var stages = new PersonClipTaskState(job, time); job.PersonClipStages = stages;
        var source = new FileInfo(job.Inputs[0]); stages.Source(Info, source.Length, source.LastWriteTimeUtc);
        foreach (var id in new[] { PersonClipStageIds.ReadVideo, PersonClipStageIds.PrepareModel, PersonClipStageIds.Detect, PersonClipStageIds.Refine })
        {
            stages.Start(id); time.Advance(1); stages.Complete(id, id + " result");
        }
        stages.Start(PersonClipStageIds.Export); stages.Update(2, 10, "片段", "2 / 10 片段");
        time.Advance(.25); scheduler.Pulse();
        var running = Summary(job); Assert.True(running.IsMultiStage); Assert.Equal(4, running.CurrentIndex);
        Assert.Equal("导出片段", running.Current!.Title); Assert.Equal(.2, running.StageFraction);
        Assert.All(running.Stages.Take(4), x => Assert.Equal(StageState.Done, x.State));
        job.PersonDetectionResult = new(new(job.Inputs[0], Info, [new() { Start = 1, End = 3 }], 2, 0, 2, 0, "test"), source.Length, source.LastWriteTimeUtc);
        stages.Finish(JobState.Failed, "disk full"); job.Error = "disk full"; job.State = JobState.Failed;
        var failed = Summary(job); Assert.Equal("export", failed.FailedStageId); Assert.Equal("disk full", failed.Error);
        var withoutQueueSummary = JsonSerializer.SerializeToNode(job)!;
        withoutQueueSummary.AsObject().Remove(nameof(Job.StageSummary));
        var fromStageFile = withoutQueueSummary.Deserialize<Job>()!;
        Assert.Equal("read-video result", Summary(fromStageFile).Stages[0].Detail);
        Assert.Equal("export", Summary(fromStageFile).FailedStageId);
        fromStageFile.StageSummaryPublisher!.Detach();
        await job.StageSummaryPublisher!.RefreshRetryAsync(); Assert.Equal("export", Summary(job).RetryStageId);
        job.PersonClipStagesReset(); job.State = JobState.Waiting; job.PersonClipStartStageId = "export";
        Assert.All(Summary(job).Stages.Take(4), x => Assert.Equal(StageState.Done, x.State));
        Assert.Equal(StageState.Pending, Summary(job).Stages[4].State);
        job.State = JobState.Running;
        Assert.All(Summary(job).Stages.Take(4), x => Assert.Equal(StageState.Done, x.State));
        var previous = stages.Capture().Summary;
        var retry = new PersonClipTaskState(job, time); job.PersonClipStages = retry;
        foreach (var id in PersonClipStageIds.All.Take(4).Select(x => x.Id)) retry.Reuse(id, previous);
        retry.Start("export"); Summary(job); Assert.Empty(job.StageSummary!.Error);
        Assert.All(job.StageSummary.Stages.Take(4), x => Assert.Equal(StageState.Done, x.State));
        retry.Complete("export", "saved"); job.State = JobState.Completed; Summary(job);
        job.PersonClipStagesReset(); job.State = JobState.Waiting;
        Assert.All(Summary(job).Stages, x => Assert.Equal(StageState.Pending, x.State));
    }

    [Theory]
    [InlineData(PersonClipStageIds.Detect)]
    [InlineData(PersonClipStageIds.Refine)]
    public async Task Person_clip_detect_and_refine_retry_use_executor_retention_checks(string id)
    {
        var time = new ManualTime(); var models = new InstalledModels();
        using var scheduler = new JobStageSummaryScheduler(time, resolveRetry: async (job, stage) =>
            (await PersonClipRetry.ResolveAsync(job, stage, models, default)).StageId ?? "");
        var job = Bind(files.Job(), scheduler); job.State = JobState.Running;
        var source = new FileInfo(job.Inputs[0]);
        var records = PersonClipTaskState.Plans(job).Select(s => new PersonClipStageRecord(s with
        { State = Array.FindIndex(PersonClipStageIds.All.ToArray(), x => x.Id == s.Stage.Id) < (id == "detect" ? 2 : 3) ? StageState.Done : StageState.Pending })).ToArray();
        var stamps = PersonClipTaskState.RequiredModels(job).ToDictionary(x => x, _ => "installed");
        var saved = new PersonClipStageSummary(records, null, null, new("test"), Info, source.Length, source.LastWriteTimeUtc,
            job.Inputs[0], PersonClipTaskState.OptionsKey(job), stamps);
        var stages = new PersonClipTaskState(job, time, saved); job.PersonClipStages = stages;
        if (id == "refine")
        {
            var checkpoint = new PersonClipDetectionCheckpoint(new(job.Inputs[0], Info, [new() { Start = 1, End = 3 }], 2, 0, 2, 0, "test"),
                [new(1, true, false, []), new(2, true, false, [])], [new(0, 1)]);
            await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "people-detection"), checkpoint, default);
        }
        stages.Start(id); stages.Finish(JobState.Failed, "inference failure"); job.Error = "inference failure"; job.State = JobState.Failed;
        await job.StageSummaryPublisher!.RefreshRetryAsync(); Assert.Equal(id, Summary(job).RetryStageId);
        models.Installed = false; time.Advance(2); await job.StageSummaryPublisher.RefreshRetryAsync(); Assert.Empty(Summary(job).RetryStageId);
        models.Installed = true; File.AppendAllText(source.FullName, "changed"); time.Advance(2);
        await job.StageSummaryPublisher.RefreshRetryAsync(); Assert.Empty(Summary(job).RetryStageId);
    }

    [Fact]
    public void Subtitle_state_is_observed_without_a_session_and_pause_clocks_freeze()
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var job = Bind(new() { FeatureId = "auto-subtitle" }, scheduler); job.State = JobState.Running;
        using var stages = new SubtitleTaskState(job, persist: false, time: time);
        stages.Start(SubtitleStageIds.ReadAudio); time.Advance(1); stages.Complete(SubtitleStageIds.ReadAudio, "audio ready");
        stages.Start(SubtitleStageIds.PrepareModel); stages.Complete(SubtitleStageIds.PrepareModel, "model ready");
        stages.Start(SubtitleStageIds.Recognize); stages.Update("4 / 10 秒", 4, 10, "秒");
        time.Advance(.25); scheduler.Pulse();
        var s = Summary(job); Assert.Equal(2, s.CurrentIndex); Assert.Equal("语音识别", s.Current!.Title);
        Assert.Equal(.4, s.StageFraction); Assert.Equal("4 / 10 秒", s.ActivityText); Assert.Equal(TimeSpan.FromSeconds(1), s.Stages[0].Elapsed);
        job.State = JobState.Paused; var paused = Summary(job); time.Advance(20); scheduler.Pulse();
        Assert.Equal(paused.Elapsed, Summary(job).Elapsed); Assert.Equal(paused.Current!.Elapsed, Summary(job).Current!.Elapsed);
        job.State = JobState.Running; time.Advance(1); stages.Update("8 / 10 秒", 8, 10, "秒"); scheduler.Pulse();
        Assert.Equal(.8, Summary(job).StageFraction);
        stages.Finish(JobState.Failed, "speech failure"); job.Error = "speech failure"; job.State = JobState.Failed;
        Assert.Equal(SubtitleStageIds.Recognize, Summary(job).FailedStageId); Assert.Empty(Summary(job).RetryStageId);
        job.State = JobState.Waiting; Summary(job); job.State = JobState.Running; time.Advance(.25); scheduler.Pulse();
        Assert.Equal(0, Summary(job).CurrentIndex); Assert.Empty(Summary(job).Error);
    }

    [Fact]
    public async Task Json_roundtrip_storage_import_and_restart_keep_real_results_and_normalize_running()
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var job = Bind(files.Job(), scheduler); job.State = JobState.Running;
        var stages = new PersonClipTaskState(job, time); job.PersonClipStages = stages;
        stages.Start("read-video"); time.Advance(2); stages.Complete("read-video", "real video metadata"); stages.Start("prepare-model");
        var json = JsonSerializer.Serialize(job);
        Assert.Contains("StageSummary", json);
        var copy = JsonSerializer.Deserialize<Job>(json)!; Summary(copy);
        Assert.Equal("real video metadata", copy.StageSummary!.Stages[0].Detail);
        var store = new Storage(Path.Combine(files.Root, Guid.NewGuid().ToString())); store.SaveJobs([job]);
        var loaded = Assert.Single(store.LoadJobs());
        Assert.Equal(JobState.Cancelled, loaded.State); var cancelled = Summary(loaded);
        Assert.Equal("real video metadata", cancelled.Stages[0].Detail); Assert.Equal(1, cancelled.CurrentIndex);
        Assert.DoesNotContain(cancelled.Stages, x => x.State == StageState.Active);
        var cancelledCopy = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(loaded))!;
        Assert.Equal(JsonSerializer.Serialize(cancelled), JsonSerializer.Serialize(cancelledCopy.StageSummary));
        var saved = cancelledCopy.StageSummary; JobStageSummaries.Attach(cancelledCopy); Assert.Same(saved, cancelledCopy.StageSummary);
        stages.Complete("prepare-model", "ready"); job.State = JobState.Completed;
        var completed = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        Assert.Equal(JsonSerializer.Serialize(Summary(job)), JsonSerializer.Serialize(Summary(completed)));
        var export = Path.Combine(files.Root, Guid.NewGuid() + ".json");
        var ordinary = Bind(new() { Inputs = [job.Inputs[0]], Output = Path.Combine(files.Root, "output.mp4") }, scheduler);
        ordinary.State = JobState.Running; ordinary.Progress = 35;
        await store.ExportJobsAsync(export, [ordinary]);
        var imported = Assert.Single(await store.ImportJobsAsync(export));
        Assert.Equal(JobState.Cancelled, imported.State); Summary(imported); Assert.NotEqual(ordinary.Id, imported.Id);
    }

    [Fact]
    public void Fresh_jobs_and_replacements_automatically_have_summaries_and_state_observers_see_them_first()
    {
        var job = new Job { FeatureId = "auto-subtitle" };
        Assert.Equal(5, Summary(job).Stages.Count);
        var events = new List<string>();
        job.PropertyChanged += (_, e) =>
        {
            events.Add(e.PropertyName!);
            if (e.PropertyName == nameof(Job.State)) Assert.Equal(job.State, job.StageSummary!.State);
        };
        job.State = JobState.Running;
        Assert.True(events.IndexOf(nameof(Job.StageSummary)) < events.IndexOf(nameof(Job.State)));
        var replacement = new Job { FeatureId = job.FeatureId, Inputs = job.Inputs.ToArray(), Options = job.Options.Clone() };
        Assert.Equal(JobState.Waiting, Summary(replacement).State);
        Assert.NotSame(job.StageSummaryPublisher, replacement.StageSummaryPublisher);
        job.State = JobState.Cancelled;
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public async Task Actual_queue_lifecycle_rejects_late_callbacks_and_can_run_again(JobState outcome)
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var executor = new ControlledExecution(); var queue = new QueueService(executor, time);
        var job = Bind(new(), scheduler);
        var run = queue.Run([job]); await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobState.Running, Summary(job).State);
        time.Advance(2); executor.Progress!(20); scheduler.Pulse();
        Assert.Equal(.2, Summary(job).StageFraction);
        Assert.True(queue.Pause(job)); Assert.Equal(JobState.Paused, Summary(job).State);
        time.Advance(20); var paused = Summary(job).Elapsed; executor.Progress(60); scheduler.Pulse();
        Assert.Equal(paused, Summary(job).Elapsed); Assert.Equal(20, job.Progress);
        Assert.True(queue.Resume(job)); Assert.Equal(JobState.Running, Summary(job).State);
        time.Advance(1); executor.Progress(30); scheduler.Pulse(); Assert.Equal(.3, Summary(job).StageFraction);
        if (outcome == JobState.Cancelled) queue.Stop(job);
        else if (outcome == JobState.Failed) executor.Release.TrySetException(new IOException("executor failure"));
        else executor.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(outcome, Summary(job).State);
        var final = Summary(job); executor.Progress(1); scheduler.Pulse();
        Assert.Equal(final.OverallFraction, Summary(job).OverallFraction); Assert.Equal(final.Elapsed, Summary(job).Elapsed);
        job.State = JobState.Waiting; Summary(job);
        var second = new ControlledExecution(); run = new QueueService(second, time).Run([job]);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Summary(job).OverallFraction); Assert.Empty(Summary(job).Error);
        second.Release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5)); Summary(job);
    }

    [Fact]
    public async Task Late_retry_resolution_does_not_restore_a_previous_failure()
    {
        var time = new ManualTime(); var resolved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new JobStageSummaryScheduler(time, resolveRetry: (_, _) => resolved.Task);
        var job = Bind(files.Job(), scheduler); job.State = JobState.Running;
        var stages = new PersonClipTaskState(job, time); job.PersonClipStages = stages; stages.Start("detect");
        job.Error = "failed"; job.State = JobState.Failed;
        Interlocked.Increment(ref job.StageSummaryQueueExecutions);
        await job.StageSummaryPublisher!.RefreshRetryAsync(); Assert.Empty(Summary(job).RetryStageId);
        Interlocked.Decrement(ref job.StageSummaryQueueExecutions);
        var resolving = job.StageSummaryPublisher.RefreshRetryAsync();
        job.State = JobState.Waiting; resolved.SetResult("detect"); await resolving;
        Summary(job); Assert.Empty(job.StageSummary!.RetryStageId);
    }

    [Fact]
    public void Loading_time_is_not_added_to_an_interrupted_jobs_saved_elapsed()
    {
        var time = new ManualTime(); using var scheduler = new JobStageSummaryScheduler(time);
        var job = Bind(new(), scheduler); job.State = JobState.Running;
        time.Advance(2); job.Progress = 30; scheduler.Pulse();
        var savedElapsed = Summary(job).Elapsed;
        job.StageSummaryPublisher!.Detach(); job.StageSummaryPublisher = null;
        job.StageSummaryRestoring = true;
        JobStageSummaries.Attach(job, scheduler); job.StageSummaryRestoring = false;
        time.Advance(100); scheduler.Pulse(); job.State = JobState.Cancelled;
        Assert.Equal(savedElapsed, Summary(job).Elapsed);
        Assert.Equal(savedElapsed, Summary(job).Current!.Elapsed);
    }

    private sealed class ControlledExecution : IJobExecutor
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<double>? Progress;
        public async Task Execute(Job job, Action<double> progress, CancellationToken ct)
        { Progress = progress; Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
    }

    private sealed class InstalledModels : IPersonClipModelAvailability
    {
        internal bool Installed = true;
        public string Stamp(string id) => "installed";
        public Task<bool> IsInstalledAsync(string id, CancellationToken ct) => Task.FromResult(Installed);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        internal void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
