using System.ComponentModel;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Window-scoped observer. Commands and queue admission remain the host's responsibility.</summary>
public sealed class VideoSummaryTaskSession : IVideoSummaryTask
{
    private readonly Job _job;
    private readonly IMultiStageQueueHost _host;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private MultiStageTaskSnapshot _snapshot = new();
    private VideoSummaryTaskView _view = new();
    private VideoSummaryViewData _viewData = new();
    private VideoSummaryTaskState? _loadedExecution;
    private string _loadedOutput = "", _loadedSource = "";
    private VideoSummaryStageSummary? _restored;
    private string _retryId = "";
    private long _revision, _lastLoad;
    private int _publishing, _loading;
    private volatile bool _disposed;
    public VideoSummaryTaskSession(Job job, IMultiStageQueueHost host)
    {
        ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(host);
        if (job.FeatureId != "video-summary" || job.Options.VideoSummary is null) throw new ArgumentException("需要视频总结任务。", nameof(job));
        _job = job; _host = host;
        _job.PropertyChanged += OnJobChanged;
        _timer = new(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
        Publish(false); _timer.Change(250, 250);
    }
    public string FeatureId => "video-summary";
    public IReadOnlyList<TaskStageDescriptor> Stages => VideoSummaryStageIds.All;
    public MultiStageTaskSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public VideoSummaryTaskView View { get { lock (_gate) return _view; } }
    public event EventHandler? Changed;
    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Job.State) or nameof(Job.Inputs) or nameof(Job.Output)
            or nameof(Job.SummaryReport) or nameof(Job.LongSummaryReport) or nameof(Job.SummarySegments))
            Interlocked.Exchange(ref _lastLoad, 0);
    }
    private async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) != 0) return;
        try
        {
            var execution = _job.VideoSummaryStages;
            var output = _job.Output; var source = _job.Inputs.FirstOrDefault() ?? "";
            var summary = _job.VideoSummaryStages?.Capture() ?? await VideoSummaryRetry.LoadAsync(_job).ConfigureAwait(false);
            var retry = (_job.State is JobState.Failed or JobState.Cancelled) && await VideoSummaryRetry.CanRetryAsync(_job, summary).ConfigureAwait(false);
            var viewData = _job.State == JobState.Waiting ? new VideoSummaryViewData()
                : await VideoSummaryTaskViewProjection.LoadAsync(_job).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(execution, _job.VideoSummaryStages) && output == _job.Output && source == (_job.Inputs.FirstOrDefault() ?? ""))
                {
                    _restored = summary; _retryId = retry ? VideoSummaryStageIds.UnderstandSegments : "";
                    _viewData = viewData; _loadedExecution = execution; _loadedOutput = output; _loadedSource = source;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            lock (_gate) _retryId = "";
            System.Diagnostics.Trace.TraceWarning("Video summary session: {0}", error.Message);
        }
        finally { Volatile.Write(ref _loading, 0); }
    }
    private void Publish(bool notify = true)
    {
        if (Interlocked.Exchange(ref _publishing, 1) != 0) return;
        try
        {
            if (_disposed) return;
            if (Environment.TickCount64 - Interlocked.Read(ref _lastLoad) >= 2000)
            { Interlocked.Exchange(ref _lastLoad, Environment.TickCount64); _ = Task.Run(LoadAsync); }
            VideoSummaryStageSummary? restored; string retry; VideoSummaryViewData viewData;
            lock (_gate)
            {
                var matches = ReferenceEquals(_loadedExecution, _job.VideoSummaryStages) && _loadedOutput == _job.Output
                    && _loadedSource == (_job.Inputs.FirstOrDefault() ?? "");
                restored = matches ? _restored : null; retry = matches ? _retryId : "";
                viewData = matches ? _viewData : new();
            }
            var live = _job.VideoSummaryStages?.Capture();
            var configuring = _job.State == JobState.Waiting || _job.State == JobState.Paused && live?.StartedUtc is null && restored?.StartedUtc is null;
            var summary = configuring ? null : live ?? restored;
            var statuses = summary?.Stages.ToArray() ?? VideoSummaryTaskState.Plans();
            for (int i = 0; i < statuses.Length; i++)
            {
                var stage = statuses[i];
                if (stage.State is not (StageState.Active or StageState.Paused)) continue;
                var state = _job.State switch { JobState.Paused => StageState.Paused, JobState.Failed => StageState.Failed,
                    JobState.Cancelled => StageState.Cancelled, _ => stage.State };
                statuses[i] = stage with { State = state, IsClockRunning = live is not null && (_job.State is JobState.Running or JobState.Stopping) && stage.IsClockRunning };
            }
            int current = Array.FindIndex(statuses, s => s.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled);
            var canRerun = _host.CanRerun(_job);
            var retryId = canRerun && (_job.State is JobState.Failed or JobState.Cancelled) ? retry : "";
            var next = new MultiStageTaskSnapshot
            {
                State = _job.State switch { JobState.Waiting => MultiStageTaskState.Configuring, JobState.Running => MultiStageTaskState.Running,
                    JobState.Paused => MultiStageTaskState.Paused, JobState.Stopping => MultiStageTaskState.Stopping,
                    JobState.Failed => MultiStageTaskState.Failed, JobState.Completed => MultiStageTaskState.Completed, _ => MultiStageTaskState.Cancelled },
                Stages = Array.AsReadOnly(statuses), CurrentStageIndex = current, Progress = _job.Progress,
                Elapsed = TimeSpan.FromTicks(statuses.Sum(s => (s.Elapsed ?? TimeSpan.Zero).Ticks)), IsClockRunning = statuses.Any(s => s.IsClockRunning),
                StartedUtc = summary?.StartedUtc, CompletedUtc = summary?.CompletedUtc, SnapshotUtc = DateTime.UtcNow,
                Remaining = _job.State == JobState.Running ? _job.Estimate?.Remaining : null,
                Error = _job.State == JobState.Failed ? _job.Error : "",
                InterruptedStageId = current >= 0 && (statuses[current].State is StageState.Failed or StageState.Cancelled) ? statuses[current].Stage.Id : "",
                Runtime = summary?.Runtime ?? new(""), Revision = Interlocked.Increment(ref _revision),
                RetainedStageIds = retryId.Length == 0 ? [] : Array.AsReadOnly(statuses.TakeWhile(s => s.Stage.Id != retryId).Where(s => s.State == StageState.Done).Select(s => s.Stage.Id).ToArray()),
                Commands = new() { CanStart = _host.CanStart(_job), CanPause = _host.CanPause(_job), CanResume = _host.CanResume(_job),
                    CanStop = _host.CanStop(_job), CanRerun = canRerun, CanChangeOutput = _host.CanChangeOutput(_job), RetryStageId = retryId }
            };
            var allowResults = !configuring && (live is null || live.Stages.Any(s => s.Stage.Id == VideoSummaryStageIds.Transcript && s.State != StageState.Pending));
            var view = VideoSummaryTaskViewProjection.Project(_job, summary, viewData, allowResults);
            lock (_gate) { if (_disposed) return; _snapshot = next; _view = view; }
            if (notify) Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { Volatile.Write(ref _publishing, 0); }
    }
    public async Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entries = new List<TaskLogEntry>();
        try
        {
            await using var stream = new FileStream(VideoSummaryTaskState.LogPath(_job), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                try { if (JsonSerializer.Deserialize<TaskLogEntry>(line) is { } entry) entries.Add(entry); }
                catch (JsonException) { } // An interrupted or concurrent append can leave a partial line.
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        return entries.AsReadOnly();
    }
    private void Ensure(bool allowed) { ObjectDisposedException.ThrowIf(_disposed, this); if (!allowed) throw new InvalidOperationException("当前任务状态不支持该操作。"); }
    public Task StartAsync() { Ensure(_host.CanStart(_job)); return _host.StartAsync(_job); }
    public Task PauseAsync() { Ensure(_host.CanPause(_job)); _host.Pause(_job); return Task.CompletedTask; }
    public Task ResumeAsync() { Ensure(_host.CanResume(_job)); return _host.ResumeAsync(_job); }
    public Task StopAsync() { Ensure(_host.CanStop(_job)); _host.Stop(_job); return Task.CompletedTask; }
    public Task RerunAsync() { Ensure(_host.CanRerun(_job)); return _host.RerunAsync(_job, null); }
    public async Task RetryFromStageAsync(string stageId)
    {
        Ensure(_host.CanRerun(_job) && stageId == VideoSummaryStageIds.UnderstandSegments && Snapshot.Commands.RetryStageId == stageId);
        var summary = _job.VideoSummaryStages?.Capture() ?? await VideoSummaryRetry.LoadAsync(_job).ConfigureAwait(false);
        Ensure(await VideoSummaryRetry.CanRetryAsync(_job, summary).ConfigureAwait(false));
        await _host.RerunAsync(_job, stageId).ConfigureAwait(false);
    }
    public Task ChangeOutputFolderAsync(string folder) { Ensure(_host.CanChangeOutput(_job)); return _host.ChangeOutputFolderAsync(_job, folder); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Changed = null; }
        _job.PropertyChanged -= OnJobChanged; _timer.Dispose();
    }
}
