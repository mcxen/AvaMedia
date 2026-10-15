using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

/// <summary>Queue-level observations. Attaching twice never replaces a publisher or a restored result.</summary>
public static class JobStageSummaries
{
    public static void Attach(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Attach(job, JobStageSummaryScheduler.Shared);
    }

    internal static void Attach(Job job, JobStageSummaryScheduler scheduler)
    {
        lock (job.StageSummaryGate)
        {
            if (job.StageSummaryHydrating || job.StageSummaryPublisher is not null) return;
            job.StageSummaryPublisher = new(job, scheduler);
        }
        job.StageSummaryPublisher.Initialize();
    }
}

public sealed partial class Job : IJsonOnDeserializing, IJsonOnDeserialized
{
    internal readonly object StageSummaryGate = new();
    internal JobStageSummaryPublisher? StageSummaryPublisher;
    internal bool StageSummaryHydrating;
    internal bool StageSummaryRestoring;
    internal int StageSummaryQueueExecutions;

    void IJsonOnDeserializing.OnDeserializing()
    {
        lock (StageSummaryGate)
        {
            StageSummaryHydrating = true;
            StageSummaryPublisher?.Detach(); StageSummaryPublisher = null;
            StageSummary = null;
        }
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        StageSummaryHydrating = false;
        StageSummaryRestoring = true;
        try { JobStageSummaries.Attach(this); }
        finally { StageSummaryRestoring = false; }
    }
}

/// <summary>One scheduler for all rows; only live, dirty or retry-capable rows are registered weakly.</summary>
internal sealed class JobStageSummaryScheduler : IDisposable
{
    internal static JobStageSummaryScheduler Shared { get; } = new(TimeProvider.System, true);
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private readonly Dictionary<long, WeakReference<JobStageSummaryPublisher>> _publishers = [];
    private readonly ITimer? _timer;
    private int _pulsing;
    internal TimeProvider Time { get; }
    internal Func<Job, string, Task<string>> ResolveRetry { get; }

    internal JobStageSummaryScheduler(TimeProvider time, bool startTimer = false,
        Func<Job, string, Task<string>>? resolveRetry = null)
    {
        Time = time; ResolveRetry = resolveRetry ?? JobStageSummarySources.RetryAsync;
        if (startTimer) _timer = time.CreateTimer(_ => Pulse(), null, Interval, Interval);
    }

    internal void Register(JobStageSummaryPublisher publisher)
    {
        lock (_gate) _publishers[publisher.SchedulerId] = new(publisher);
    }

    internal void Unregister(JobStageSummaryPublisher publisher)
    {
        lock (_gate) _publishers.Remove(publisher.SchedulerId);
    }

    internal void Pulse()
    {
        if (Interlocked.Exchange(ref _pulsing, 1) != 0) return;
        try
        {
            JobStageSummaryPublisher[] live;
            lock (_gate)
            {
                live = _publishers.Values.Select(w => w.TryGetTarget(out var p) ? p : null).OfType<JobStageSummaryPublisher>().ToArray();
                foreach (var key in _publishers.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToArray()) _publishers.Remove(key);
            }
            foreach (var publisher in live) publisher.Tick();
        }
        finally { Volatile.Write(ref _pulsing, 0); }
    }

    public void Dispose() => _timer?.Dispose();
}

internal sealed class JobStageSummaryPublisher
{
    private static long _nextSchedulerId;
    internal long SchedulerId { get; } = Interlocked.Increment(ref _nextSchedulerId);
    private readonly Job _job;
    private readonly JobStageSummaryScheduler _scheduler;
    private TimeProvider Time => _scheduler.Time;
    private long _generation, _lastPublish, _runningSince, _lastRetry, _captureSequence, _appliedCapture;
    private TimeSpan _elapsed;
    private bool _running, _dirty, _registered, _detached, _retryBusy;
    private object? _ignoredOwner;
    private JobStageObservation? _restored;
    private IReadOnlyList<JobStageItem> _previousAttempt = [];
    private string _retryId = "";

    internal JobStageSummaryPublisher(Job job, JobStageSummaryScheduler scheduler)
    { _job = job; _scheduler = scheduler; }

    internal void Initialize()
    {
        var saved = _job.StageSummary;
        var observation = JobStageSummarySources.Capture(_job, null, loadSaved: saved is null && _job.State != JobState.Waiting);
        lock (_job.StageSummaryGate)
        {
            if (_detached) return;
            _job.PropertyChanged += Changed;
            if (saved is not null)
            {
                _elapsed = saved.Elapsed;
                _restored = FromSummary(saved);
                if (_job.State == JobState.Waiting) _previousAttempt = saved.Stages;
            }
            if (saved is null && observation.Owner is null && _job.State != JobState.Waiting)
            {
                _restored = observation;
                _elapsed = TimeSpan.FromTicks(observation.Stages.Sum(s => s.Elapsed?.Ticks ?? 0));
            }
            if (_job.State == JobState.Waiting) _elapsed = TimeSpan.Zero;
            // Deserialization is observation, not execution. Storage will cancel interrupted jobs;
            // loading a large queue must not add parsing time to their saved running time.
            _running = _job.State == JobState.Running && !_job.StageSummaryRestoring; _runningSince = Time.GetTimestamp();
            if (saved is not null && saved.State == _job.State && saved.State != JobState.Running)
            {
                // Exact restored snapshot, including revision and real details, is part of Attach's contract.
                _lastPublish = Time.GetTimestamp();
                if (RetryCandidate(saved).Length > 0) Register();
                return;
            }
            if (observation.Owner is null && _restored is not null && _job.State != JobState.Waiting) observation = _restored;
            Publish(Build(observation));
        }
    }

    internal void Detach() { _detached = true; _job.PropertyChanged -= Changed; _scheduler.Unregister(this); }

    internal JobStageObservation Capture()
    {
        var observation = JobStageSummarySources.Capture(_job, _ignoredOwner);
        var previous = _job.StageSummary;
        if (observation.Owner is null && previous is { IsMultiStage: true } && _job.State != JobState.Waiting
            && previous.Stages.Select(s => s.Id).SequenceEqual(observation.Stages.Select(s => s.Stage.Id)))
            return FromSummary(previous);
        return observation;
    }

    // Called under the owning job's gate, after its field changes and BEFORE PropertyChanged(State).
    // Capture happened outside that gate: execution-owned locks must never nest inside the publisher lock.
    internal void StateChanged(JobStageObservation observation)
    {
        _generation++; _lastRetry = 0;
        Accumulate();
        if (_job.State == JobState.Waiting)
        {
            _previousAttempt = _job.StageSummary?.Stages ?? [];
            _ignoredOwner = observation.Owner;
            _restored = null; _elapsed = TimeSpan.Zero; _retryId = "";
            observation = new(null, JobStageSummarySources.Plans(_job));
        }
        _running = _job.State == JobState.Running;
        _runningSince = Time.GetTimestamp();
        if (_job.State is not (JobState.Failed or JobState.Cancelled or JobState.Completed)) _retryId = "";
        if (observation.Owner is null && _job.State != JobState.Waiting)
        {
            if (_restored is not null) observation = _restored;
            else if (_job.StageSummary is { IsMultiStage: true } previous
                && previous.Stages.Select(s => s.Id).SequenceEqual(observation.Stages.Select(s => s.Stage.Id))) observation = FromSummary(previous);
        }
        Publish(Build(observation));
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Job.StageSummary) or nameof(Job.State)) return;
        if (args.PropertyName is nameof(Job.PersonClipStages))
        { Refresh(); return; }
        if (args.PropertyName is nameof(Job.Lifecycle) or nameof(Job.FeatureId) or nameof(Job.Options) or nameof(Job.PersonClipStartStageId) or nameof(Job.Inputs) or nameof(Job.Output))
        {
            lock (_job.StageSummaryGate) { _generation++; _lastRetry = 0; _retryId = ""; }
            Refresh(force: true); return;
        }
        if (args.PropertyName is not (nameof(Job.Progress) or nameof(Job.ProgressDetail) or nameof(Job.Activity)
            or nameof(Job.Estimate) or nameof(Job.Error) or nameof(Job.Inputs) or nameof(Job.Output)
            or nameof(Job.SubtitleStages) or nameof(Job.MediaClassificationStages))) return;
        Refresh();
    }

    private void Register()
    {
        if (_registered) return;
        _registered = true; _scheduler.Register(this);
    }

    internal void Tick()
    {
        bool refresh;
        lock (_job.StageSummaryGate)
        {
            if (_detached) return;
            refresh = _dirty || _job.State is JobState.Running or JobState.Paused or JobState.Stopping;
        }
        if (refresh) Refresh();
        // Retry is deliberately pessimistic until the executor's full retention check has completed.
        _ = RefreshRetryAsync();
        lock (_job.StageSummaryGate)
        {
            if (!_dirty && _job.State is not (JobState.Running or JobState.Paused or JobState.Stopping)
                && (_job.StageSummary is not { } summary || RetryCandidate(summary).Length == 0))
            { _registered = false; _scheduler.Unregister(this); }
        }
    }

    private void Refresh(bool force = false)
    {
        long generation;
        lock (_job.StageSummaryGate) { if (_detached) return; generation = _generation; }
        var captureSequence = Interlocked.Increment(ref _captureSequence);
        var observation = Capture();
        lock (_job.StageSummaryGate)
        {
            if (_detached || generation != _generation) return;
            // A slow earlier capture must not overwrite a newer stage observation.
            if (captureSequence < _appliedCapture) { _dirty = true; Register(); return; }
            _appliedCapture = captureSequence;
            if (observation.Owner is null && _restored is not null && _job.State != JobState.Waiting) observation = _restored;
            var next = Build(observation);
            var previous = _job.StageSummary;
            if (!force && previous is not null && Equivalent(previous, next)) { _dirty = false; return; }
            bool structural = previous is null || previous.State != next.State || previous.CurrentIndex != next.CurrentIndex
                || previous.Error != next.Error || !previous.Stages.Select(s => (s.Id, s.State)).SequenceEqual(next.Stages.Select(s => (s.Id, s.State)));
            if (force || structural || Time.GetElapsedTime(_lastPublish) >= JobStageSummaryScheduler.Interval)
                Publish(next);
            else { _dirty = true; Register(); }
        }
    }

    private void Accumulate()
    {
        if (_running) _elapsed += Time.GetElapsedTime(_runningSince);
        _runningSince = Time.GetTimestamp();
    }

    private JobStageSummary Build(JobStageObservation observation)
    {
        var state = _job.State;
        var previous = _job.StageSummary;
        var items = observation.Stages.Select(s => new JobStageItem(s.Stage.Id, s.Stage.Title, s.State)
        { Detail = s.Detail, Fraction = Fraction(s.Fraction), Elapsed = s.Elapsed }).ToArray();
        int current = Array.FindIndex(items, s => s.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled);
        if (current < 0) current = Array.FindIndex(items, s => s.State == StageState.Pending);
        if (current < 0 && previous?.State != JobState.Waiting && previous?.Current is { } old)
            current = Array.FindIndex(items, s => s.Id == old.Id);
        if (current < 0) current = items.Length - 1;
        var single = items.Length == 1;
        var activity = state == JobState.Waiting ? "" : Detail();
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (state == JobState.Waiting)
            {
                var boundary = Array.FindIndex(items, s => s.Id == _job.PersonClipStartStageId);
                var retained = boundary > i ? _previousAttempt.FirstOrDefault(s => s.Id == item.Id && s.State is StageState.Done or StageState.Skipped) : null;
                items[i] = retained ?? item with { State = StageState.Pending, Detail = item.State == StageState.Pending ? item.Detail : observation.Stages[i].Stage.Description, Fraction = null, Elapsed = null };
                continue;
            }
            if (state == JobState.Completed)
            {
                items[i] = item with { State = item.State == StageState.Skipped ? StageState.Skipped : StageState.Done, Fraction = null,
                    Elapsed = single ? Elapsed() : previous?.State == JobState.Completed ? previous.Stages.FirstOrDefault(s => s.Id == item.Id)?.Elapsed : item.Elapsed };
                continue;
            }
            var stageState = i == current ? state switch
            {
                JobState.Running => StageState.Active,
                JobState.Paused => StageState.Paused,
                JobState.Failed => StageState.Failed,
                JobState.Cancelled or JobState.Stopping => StageState.Cancelled,
                _ => StageState.Pending
            } : item.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled ? StageState.Pending : item.State;
            var elapsed = item.Elapsed;
            // Freeze snapshots while paused/terminal even if an execution-owned state has not handled State yet.
            if (state != JobState.Running && previous?.State == state)
                elapsed = previous.Stages.FirstOrDefault(s => s.Id == item.Id)?.Elapsed ?? elapsed;
            items[i] = item with
            {
                State = stageState,
                Fraction = stageState is StageState.Active or StageState.Paused ? (single ? Fraction(_job.Progress / 100) : item.Fraction) : null,
                Elapsed = single && i == current ? Elapsed() : elapsed,
                Detail = i == current && state == JobState.Failed ? Failure(item.Detail) : single ? activity : item.Detail
            };
        }
        if (state is JobState.Completed or JobState.Waiting) current = -1;
        var error = state == JobState.Failed ? Failure(current >= 0 ? items[current].Detail : "") : "";
        var fraction = state switch { JobState.Completed => 1d, JobState.Waiting => 0d, _ => Fraction(_job.Progress / 100) };
        return new()
        {
            State = state, IsMultiStage = !single, Stages = Array.AsReadOnly(items), CurrentIndex = current,
            Pages = _job.Lifecycle?.Pages.Select(page => page with { Result = null }).ToArray() ?? [], CurrentPageId = _job.Lifecycle?.Current.Page.Id ?? "",
            OverallFraction = fraction, StageFraction = current >= 0 ? items[current].Fraction : null,
            Elapsed = state == JobState.Waiting ? TimeSpan.Zero : Elapsed(), IsClockRunning = state == JobState.Running,
            SnapshotUtc = Time.GetUtcNow().UtcDateTime, Remaining = state == JobState.Running ? _job.Estimate?.Remaining : null,
            FailedStageId = state == JobState.Failed && current >= 0 ? items[current].Id : "", Error = error,
            RetryStageId = Volatile.Read(ref _job.StageSummaryQueueExecutions) == 0 && current >= 0 && items[current].Id == _retryId
                && state is JobState.Failed or JobState.Cancelled ? _retryId : "",
            ActivityText = single ? activity : current >= 0 ? items[current].Detail : ""
        };
    }

    private static bool Equivalent(JobStageSummary a, JobStageSummary b) =>
        a with { Stages = b.Stages, Pages = b.Pages, Revision = b.Revision, SnapshotUtc = b.SnapshotUtc } == b
        && a.Stages.SequenceEqual(b.Stages) && a.Pages.SequenceEqual(b.Pages);

    private TimeSpan Elapsed() => _elapsed + (_running ? Time.GetElapsedTime(_runningSince) : TimeSpan.Zero);
    private static double? Fraction(double? value) => value is { } n && double.IsFinite(n) ? Math.Clamp(n, 0, 1) : null;
    private string Failure(string detail) => _job.Error.Length > 0 ? _job.Error : detail.Length > 0 ? detail : "任务失败";
    private string Detail()
    {
        if (_job.ProgressDetail.Length > 0) return _job.ProgressDetail;
        if (_job.Activity is { } activity) return activity.Detail.Length > 0 ? activity.Stage + " · " + activity.Detail : activity.Stage;
        var status = _job.Status; var separator = status.IndexOf(" · ", StringComparison.Ordinal);
        return separator < 0 ? "" : status[(separator + 3)..];
    }

    private void Publish(JobStageSummary next)
    {
        _lastPublish = Time.GetTimestamp(); _dirty = false;
        _job.StageSummary = next with { Revision = (_job.StageSummary?.Revision ?? 0) + 1 };
        if (next.State is JobState.Running or JobState.Paused or JobState.Stopping || RetryCandidate(next).Length > 0) Register();
    }

    private static JobStageObservation FromSummary(JobStageSummary saved) => new(null, saved.Stages.Select(s =>
        new TaskStageStatus(new(s.Id, s.Title, ""), s.State) { Detail = s.Detail, Fraction = s.Fraction, Elapsed = s.Elapsed }).ToArray());

    private string RetryCandidate(JobStageSummary summary) => _job.FeatureId == "person-clip"
        && summary.State is JobState.Failed or JobState.Cancelled or JobState.Completed
        && summary.Current?.Id is PersonClipStageIds.Detect or PersonClipStageIds.Refine or PersonClipStageIds.Export ? summary.Current.Id : "";

    internal async Task RefreshRetryAsync()
    {
        string candidate; long generation;
        lock (_job.StageSummaryGate)
        {
            if (_detached || _retryBusy || Volatile.Read(ref _job.StageSummaryQueueExecutions) != 0 || _job.StageSummary is not { } summary) return;
            candidate = RetryCandidate(summary);
            if (candidate.Length == 0 || (_lastRetry != 0 && Time.GetElapsedTime(_lastRetry) < TimeSpan.FromSeconds(2))) return;
            _retryBusy = true; generation = _generation; _lastRetry = Time.GetTimestamp();
        }
        try
        {
            var id = await _scheduler.ResolveRetry(_job, candidate).ConfigureAwait(false);
            lock (_job.StageSummaryGate)
            {
                if (_detached || generation != _generation || Volatile.Read(ref _job.StageSummaryQueueExecutions) != 0
                    || _job.StageSummary is not { } summary || RetryCandidate(summary) != candidate) return;
                if (_retryId != id || summary.RetryStageId != id)
                {
                    _retryId = id;
                    Publish(summary with { RetryStageId = id, SnapshotUtc = Time.GetUtcNow().UtcDateTime });
                }
            }
        }
        finally { lock (_job.StageSummaryGate) _retryBusy = false; }
    }
}
