using System.ComponentModel;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Window-scoped observer of a direction-detection job. Disposing only stops observation.</summary>
public sealed class OrientationTaskSession : IOrientationTask
{
    private readonly Job _job;
    private readonly IMultiStageQueueHost _host;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private OrientationStageSummary? _restored;
    private OrientationTaskResult? _restoredResult;
    private bool _restoreAttempted;
    private long _generation;
    private OrientationTaskView _view = new();
    private MultiStageTaskSnapshot _snapshot = new();
    private long _revision;
    private int _publishing;
    private bool _disposed;

    public OrientationTaskSession(Job job, IMultiStageQueueHost host)
    {
        if (job.FeatureId != "rotate" || job.Options.Orientation is null)
            throw new ArgumentException("需要方向检测任务。", nameof(job));
        _job = job; _host = host;
        _job.PropertyChanged += OnJobChanged;
        Publish(false, readDisk: false);
        _timer = new(_ => Publish(), null, 250, 250);
    }
    public string FeatureId => "rotate";
    public IReadOnlyList<TaskStageDescriptor> Stages => OrientationStageIds.All;
    public MultiStageTaskSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public OrientationTaskView View { get { lock (_gate) return _view; } }
    public event EventHandler? Changed;
    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Job.State) && _job.State == JobState.Waiting)
            lock (_gate)
            {
                _generation++;
                _restored = null; _restoredResult = null; _restoreAttempted = false;
            }
    }
    private void Publish(bool notify = true, bool readDisk = true)
    {
        if (Interlocked.Exchange(ref _publishing, 1) != 0) return;
        try
        {
            long generation;
            lock (_gate) { if (_disposed) return; generation = _generation; }
            if (readDisk) Restore(generation);
            var now = DateTime.UtcNow;
            var captured = _job.OrientationStages?.Capture();
            OrientationStageSummary? restored;
            OrientationTaskResult? restoredResult;
            lock (_gate) { restored = _restored; restoredResult = _restoredResult; }
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
                    JobState.AwaitingUser => MultiStageTaskState.AwaitingUser, JobState.Completed => MultiStageTaskState.Completed, JobState.Failed => MultiStageTaskState.Failed,
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
            var result = configuring ? null : captured is not null ? captured.Result
                : _job.OrientationResult ?? restoredResult ?? restored?.Result;
            // A job's input/options may have been edited since the saved observations were produced.
            if (result is not null && !Matches(result)) result = null;
            var view = Project(result, configuring ? null : summary, Output(readDisk),
                readDisk && result is not null && SourceUnchanged(result));
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
                _view = view;
                _snapshot = snapshot;
                if (notify) Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Orientation snapshot: {0}", e.Message); }
        finally { Volatile.Write(ref _publishing, 0); }
    }
    // Called only by the worker timer. Constructors and View/Snapshot getters never read the disk.
    private void Restore(long generation)
    {
        lock (_gate) if (_restoreAttempted || _job.State == JobState.Waiting) return;
        OrientationStageSummary? summary = null;
        OrientationTaskResult? result = null;
        try
        {
            var path = AiTaskResults.PathFor(_job, "orientation-stages");
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<OrientationStageSummary>(File.ReadAllText(path));
                if (saved?.Stages is { Length: 5 } && saved.SourcePath == _job.Inputs.FirstOrDefault()
                    && saved.OptionsKey == OrientationTaskState.OptionsKey(_job)
                    && saved.Stages.Select(s => s.Stage.Id).SequenceEqual(Stages.Select(s => s.Id))) summary = saved;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { System.Diagnostics.Trace.TraceWarning("Orientation stages: {0}", e.Message); }
        try
        {
            var path = AiTaskResults.PathFor(_job, "orientation");
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<OrientationTaskResult>(File.ReadAllText(path));
                if (saved is not null && Matches(saved)) result = saved;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { System.Diagnostics.Trace.TraceWarning("Orientation result: {0}", e.Message); }
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _restored = summary; _restoredResult = result; _restoreAttempted = true;
        }
    }
    private bool Matches(OrientationTaskResult result) => result.SourcePath == _job.Inputs.FirstOrDefault()
        && result.OptionsKey == OrientationTaskState.OptionsKey(_job);
    private static bool SourceUnchanged(OrientationTaskResult result)
    {
        try
        {
            var source = new FileInfo(result.SourcePath);
            return source.Exists && source.Length == result.SourceLength && source.LastWriteTimeUtc == result.SourceWriteUtc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    private TaskOutputView Output(bool readDisk)
    {
        var path = _job.Output;
        var output = new TaskOutputView
        {
            Path = path, Folder = Path.GetDirectoryName(path) ?? "",
            IsFixedLocation = path.Length > 0 && BatchRename.PathComparer.Equals(Path.GetFullPath(path), AiTaskResults.PathFor(_job, "orientation"))
        };
        if (!readDisk) return output;
        try
        {
            var file = new FileInfo(path);
            return output with { Exists = file.Exists, Bytes = file.Exists ? file.Length : null };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return output; }
    }
    private OrientationTaskView Project(OrientationTaskResult? result, OrientationStageSummary? summary,
        TaskOutputView output, bool sourceUnchanged)
    {
        var detection = result?.Detection;
        var samples = detection?.Evidence.Take(12).Select(frame =>
        {
            var rotation = VideoOrientationPolicy.FrameRotation(frame);
            return new OrientationSampleView(frame.Seconds, Array.AsReadOnly(frame.Scores))
            {
                Rotation = rotation, Agrees = rotation is not null && rotation == detection.Rotation,
                Image = frame.Image?.ToArray()
            };
        }).ToArray() ?? [];
        return new()
        {
            SourcePath = _job.Inputs.FirstOrDefault() ?? "", Info = result?.Info ?? summary?.Info,
            SampledFrames = detection?.SampledFrames ?? 0, ValidFrames = detection?.ValidFrames ?? 0,
            AgreeingFrames = detection?.AgreeingFrames ?? 0, PlannedFrames = detection?.PlannedFrames ?? summary?.PlannedFrames,
            Rotation = detection?.Rotation,
            Decision = detection is null ? "" : detection.Rotation switch
            {
                0 => "方向正常", 90 => "顺时针旋转 90°", 180 => "旋转 180°", 270 => "逆时针旋转 90°", _ => "无法确定"
            },
            Reliability = detection is null ? "" : detection.Reliability switch
            { OrientationReliability.High => "高", OrientationReliability.Medium => "中等", _ => "无法确定" },
            Reason = detection?.Reason ?? "", Backend = detection?.Backend ?? summary?.Runtime.Backend ?? "",
            Samples = Array.AsReadOnly(samples), HasResult = result is not null, IsPartial = result?.IsPartial ?? true,
            CanConfirm = detection?.Rotation is not null && sourceUnchanged, Output = output
        };
    }
    public Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default) => Task.Run(async () =>
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
        return (IReadOnlyList<TaskLogEntry>)entries.AsReadOnly();
    }, ct);
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
