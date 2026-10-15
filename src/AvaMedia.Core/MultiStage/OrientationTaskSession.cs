using System.ComponentModel;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Window-scoped observer of a direction-detection job. Disposing only stops observation.</summary>
public sealed class OrientationTaskSession : IMultiStageTask
{
    private readonly Job _job;
    private readonly IMultiStageQueueHost _host;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private OrientationStageSummary? _restored;
    private MultiStageTaskSnapshot _snapshot = new();
    private long _revision;
    private int _publishing;
    private bool _disposed;

    public OrientationTaskSession(Job job, IMultiStageQueueHost host)
    {
        if (job.FeatureId != "rotate" || job.Options.Orientation is null)
            throw new ArgumentException("需要方向检测任务。", nameof(job));
        _job = job; _host = host;
        try
        {
            var path = AiTaskResults.PathFor(job, "orientation-stages");
            if (File.Exists(path))
            {
                var summary = JsonSerializer.Deserialize<OrientationStageSummary>(File.ReadAllText(path));
                if (job.State != JobState.Waiting && summary?.Stages is { Length: 5 } && summary.SourcePath == job.Inputs.FirstOrDefault()
                    && summary.OptionsKey == OrientationTaskState.OptionsKey(job)
                    && summary.Stages.Select(s => s.Stage.Id).SequenceEqual(Stages.Select(s => s.Id))) _restored = summary;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { System.Diagnostics.Trace.TraceWarning("Orientation session: {0}", e.Message); }
        _job.PropertyChanged += OnJobChanged;
        Publish(false);
        _timer = new(_ => Publish(), null, 250, 250);
    }
    public string FeatureId => "rotate";
    public IReadOnlyList<TaskStageDescriptor> Stages => OrientationStageIds.All;
    public MultiStageTaskSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event EventHandler? Changed;
    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Job.State) && _job.State == JobState.Waiting)
            lock (_gate) _restored = null;
    }
    private void Publish(bool notify = true)
    {
        if (Interlocked.Exchange(ref _publishing, 1) != 0) return;
        try
        {
            lock (_gate) if (_disposed) return;
            var now = DateTime.UtcNow;
            var captured = _job.OrientationStages?.Capture();
            OrientationStageSummary? restored;
            lock (_gate) restored = _restored;
            var summary = captured ?? restored;
            var state = _job.State;
            var configuring = state == JobState.Waiting || state == JobState.Paused && summary?.StartedUtc is null;
            var statuses = configuring ? OrientationTaskState.Plans() : summary?.Stages.ToArray() ?? OrientationTaskState.Plans();
            // A persisted clock cannot keep running after a process interruption.
            if (captured is null)
                statuses = statuses.Select(s => s with
                {
                    IsClockRunning = false,
                    State = s.State is StageState.Active or StageState.Paused && state is JobState.Cancelled or JobState.Failed
                        ? state == JobState.Failed ? StageState.Failed : StageState.Cancelled : s.State,
                    Detail = s.State is StageState.Active or StageState.Paused && state is JobState.Cancelled or JobState.Failed
                        ? state == JobState.Failed ? _job.Error : "任务已中断" : s.Detail
                }).ToArray();
            var current = Array.FindIndex(statuses, s => s.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled);
            var snapshot = new MultiStageTaskSnapshot
            {
                State = state switch
                {
                    JobState.Waiting => MultiStageTaskState.Configuring, JobState.Running => MultiStageTaskState.Running,
                    JobState.Paused => MultiStageTaskState.Paused, JobState.Stopping => MultiStageTaskState.Stopping,
                    JobState.Completed => MultiStageTaskState.Completed, JobState.Failed => MultiStageTaskState.Failed,
                    _ => MultiStageTaskState.Cancelled
                },
                Stages = Array.AsReadOnly(statuses), CurrentStageIndex = current, Progress = configuring ? 0 : _job.Progress,
                Elapsed = TimeSpan.FromTicks(statuses.Sum(s => (s.Elapsed ?? TimeSpan.Zero).Ticks)),
                IsClockRunning = statuses.Any(s => s.IsClockRunning), SnapshotUtc = now,
                StartedUtc = configuring ? null : summary?.StartedUtc, CompletedUtc = configuring ? null : summary?.CompletedUtc,
                Remaining = state == JobState.Running ? _job.Estimate?.Remaining : null,
                Error = state == JobState.Failed ? _job.Error : "",
                InterruptedStageId = current >= 0 && statuses[current].State is StageState.Failed or StageState.Cancelled ? statuses[current].Stage.Id : "",
                Runtime = configuring ? new("YuNet") : summary?.Runtime ?? new("YuNet"),
                Commands = new()
                {
                    CanStart = _host.CanStart(_job), CanPause = _host.CanPause(_job), CanResume = _host.CanResume(_job),
                    CanStop = _host.CanStop(_job), CanRerun = _host.CanRerun(_job), CanChangeOutput = _host.CanChangeOutput(_job)
                },
                Revision = Interlocked.Increment(ref _revision)
            };
            lock (_gate)
            {
                if (_disposed) return;
                _snapshot = snapshot;
                if (notify) Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Orientation snapshot: {0}", e.Message); }
        finally { Volatile.Write(ref _publishing, 0); }
    }
    public async Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<TaskLogEntry>();
        var path = AiTaskResults.PathFor(_job, "orientation-log", "jsonl");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                try { if (JsonSerializer.Deserialize<TaskLogEntry>(line) is { } entry) entries.Add(entry); }
                catch (JsonException) { } // Concurrent appends may leave a partial final line.
            }
        }
        catch (IOException e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        return entries.AsReadOnly();
    }
    private void Ensure(bool allowed)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (!allowed) throw new InvalidOperationException("当前任务状态不支持该操作。");
    }
    public Task StartAsync() { Ensure(_host.CanStart(_job)); return _host.StartAsync(_job); }
    public Task PauseAsync() { Ensure(_host.CanPause(_job)); _host.Pause(_job); return Task.CompletedTask; }
    public Task ResumeAsync() { Ensure(_host.CanResume(_job)); return _host.ResumeAsync(_job); }
    public Task StopAsync() { Ensure(_host.CanStop(_job)); _host.Stop(_job); return Task.CompletedTask; }
    public Task RerunAsync() { Ensure(_host.CanRerun(_job)); return _host.RerunAsync(_job, null); }
    public Task RetryFromStageAsync(string stageId)
    { Ensure(false); return Task.CompletedTask; } // No reusable checkpoint: only a full rerun is supported.
    public Task ChangeOutputFolderAsync(string folder)
    { Ensure(_host.CanChangeOutput(_job)); return _host.ChangeOutputFolderAsync(_job, folder); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Changed = null; }
        _job.PropertyChanged -= OnJobChanged;
        _timer.Dispose();
    }
}
