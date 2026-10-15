using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Window-scoped observer of AI tagging or folder classification; execution belongs to the queue.</summary>
public sealed class MediaClassificationTaskSession : IMultiStageTask
{
    private readonly Job _job;
    private readonly IMultiStageQueueHost _host;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly MediaClassificationSummary? _restored;
    private MultiStageTaskSnapshot _snapshot = new();
    private volatile bool _disposed;
    private int _publishing;

    public MediaClassificationTaskSession(Job job, IMultiStageQueueHost host)
    {
        ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(host);
        if (job.FeatureId is not ("media-ai" or "folder-classification"))
            throw new ArgumentException("需要 AI 标签或媒体分类任务。", nameof(job));
        _job = job; _host = host;
        Stages = MediaClassificationStageIds.For(job);
        try
        {
            var path = MediaClassificationTaskState.SummaryPath(job);
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var summary = JsonSerializer.Deserialize<MediaClassificationSummary>(stream);
                if (summary?.Stages is { } stages && stages.Select(stage => stage.Stage.Id).SequenceEqual(Stages.Select(stage => stage.Id)))
                    _restored = summary;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Trace.TraceWarning("Classification stages: {0}", error.Message); }
        Publish(false);
        _timer = new(_ => Publish(), null, 250, 250);
    }

    public string FeatureId => _job.FeatureId;
    public IReadOnlyList<TaskStageDescriptor> Stages { get; }
    public MultiStageTaskSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event EventHandler? Changed;

    private void Publish(bool notify = true)
    {
        if (Interlocked.Exchange(ref _publishing, 1) != 0) return;
        try
        {
            if (_disposed) return;
            var previous = Snapshot;
            var state = _job.State;
            var live = _job.MediaClassificationStages;
            var summary = live?.Capture() ?? _restored;
            var configuring = state == JobState.Waiting || state == JobState.Paused && summary?.StartedUtc is null;
            var stages = configuring ? MediaClassificationTaskState.Plans(_job) : summary?.Stages ?? MediaClassificationTaskState.Plans(_job);
            if (live is null)
                stages = stages.Select(stage => stage with { IsClockRunning = false,
                    State = stage.State is StageState.Active or StageState.Paused ? state switch
                    { JobState.Failed => StageState.Failed, JobState.Cancelled => StageState.Cancelled, JobState.Paused => StageState.Paused, _ => StageState.Pending } : stage.State }).ToArray();
            var current = Array.FindIndex(stages, stage => stage.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled);
            var snapshot = new MultiStageTaskSnapshot
            {
                State = state switch { JobState.Waiting => MultiStageTaskState.Configuring, JobState.Running => MultiStageTaskState.Running,
                    JobState.Paused => MultiStageTaskState.Paused, JobState.Stopping => MultiStageTaskState.Stopping,
                    JobState.Completed => MultiStageTaskState.Completed, JobState.Failed => MultiStageTaskState.Failed, _ => MultiStageTaskState.Cancelled },
                Stages = Array.AsReadOnly(stages), CurrentStageIndex = current, Progress = configuring ? 0 : _job.Progress,
                Elapsed = TimeSpan.FromTicks(stages.Sum(stage => (stage.Elapsed ?? TimeSpan.Zero).Ticks)),
                IsClockRunning = stages.Any(stage => stage.IsClockRunning),
                StartedUtc = configuring ? null : summary?.StartedUtc, CompletedUtc = configuring ? null : summary?.CompletedUtc,
                Remaining = state == JobState.Running ? _job.Estimate?.Remaining : null,
                Error = state == JobState.Failed ? _job.Error : "",
                InterruptedStageId = current >= 0 && stages[current].State is StageState.Failed or StageState.Cancelled ? stages[current].Stage.Id : "",
                Runtime = configuring ? new("") : summary?.Runtime ?? new(""),
                Commands = new() { CanStart = _host.CanStart(_job), CanPause = _host.CanPause(_job), CanResume = _host.CanResume(_job),
                    CanStop = _host.CanStop(_job), CanRerun = _host.CanRerun(_job), CanChangeOutput = _host.CanChangeOutput(_job) },
                SnapshotUtc = DateTime.UtcNow, Revision = previous.Revision + 1
            };
            if (stages.SequenceEqual(previous.Stages)
                && snapshot with { Stages = previous.Stages, SnapshotUtc = previous.SnapshotUtc, Revision = previous.Revision } == previous) return;
            // Host capability checks can dispatch to the UI thread, whose readers also need _gate.
            lock (_gate) { if (_disposed) return; _snapshot = snapshot; }
            if (notify) Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { Volatile.Write(ref _publishing, 0); }
    }

    public async Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<TaskLogEntry>();
        if (_job.State == JobState.Waiting) return entries.AsReadOnly();
        try
        {
            await using var stream = new FileStream(MediaClassificationTaskState.LogPath(_job), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                try { if (JsonSerializer.Deserialize<TaskLogEntry>(line) is { } entry) entries.Add(entry); }
                catch (JsonException) { } // An interrupted/concurrent append may leave an incomplete line.
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        return entries.AsReadOnly();
    }

    private void Ensure(bool allowed)
    { ObjectDisposedException.ThrowIf(_disposed, this); if (!allowed) throw new InvalidOperationException("当前任务状态不支持该操作。"); }
    public Task StartAsync() { Ensure(_host.CanStart(_job)); return _host.StartAsync(_job); }
    public Task PauseAsync() { Ensure(_host.CanPause(_job)); _host.Pause(_job); return Task.CompletedTask; }
    public Task ResumeAsync() { Ensure(_host.CanResume(_job)); return _host.ResumeAsync(_job); }
    public Task StopAsync() { Ensure(_host.CanStop(_job)); _host.Stop(_job); return Task.CompletedTask; }
    public Task RerunAsync() { Ensure(_host.CanRerun(_job)); return _host.RerunAsync(_job, null); }
    public Task RetryFromStageAsync(string stageId)
    { Ensure(false); return Task.CompletedTask; } // Per-file checkpoints do not provide a stage retry boundary.
    public Task ChangeOutputFolderAsync(string folder)
    { Ensure(_host.CanChangeOutput(_job)); return _host.ChangeOutputFolderAsync(_job, folder); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Changed = null; }
        _timer.Dispose();
    }
}
