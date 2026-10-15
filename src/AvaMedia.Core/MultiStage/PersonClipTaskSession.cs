using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>A window-scoped observer; all execution and commands remain owned by the queue.</summary>
public sealed class PersonClipTaskSession : IPersonClipTask
{
    private readonly Job _job;
    private readonly IMultiStageQueueHost _host;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private readonly IMediaEngine _engine;
    private readonly ModelStore _store;
    private MultiStageTaskSnapshot _snapshot = new();
    private PersonClipTaskView _view = new();
    private PersonClipStageSummary? _restored;
    private PersonDetectionTaskResult? _restoredResult;
    private string? _replayPath;
    private IReadOnlyList<PersonClipReplayFrame> _replayFrames = [];
    private MediaInfo? _info;
    private bool? _installed;
    private string _retryId = "";
    private long _revision;
    private int _refreshing, _loading, _loadRequested;
    private long _lastLoadTick = Environment.TickCount64;
    private volatile bool _disposed;
    private long _loadVersion;
    public PersonClipTaskSession(Job job, IMultiStageQueueHost host)
        : this(job, host, new MediaEngine(new Storage().LoadSettings()), new ModelStore()) { }
    internal PersonClipTaskSession(Job job, IMultiStageQueueHost host, IMediaEngine engine, ModelStore store)
    {
        if (job.FeatureId != "person-clip" || job.Options.PersonClip is null) throw new ArgumentException("需要人物片段任务。", nameof(job));
        _job = job; _host = host; _engine = engine; _store = store;
        _job.PropertyChanged += OnJobChanged;
        _timer = new(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
        Publish(false);
        _timer.Change(250, 250);
        RequestLoad();
    }
    public string FeatureId => "person-clip";
    public IReadOnlyList<TaskStageDescriptor> Stages => PersonClipStageIds.All;
    public MultiStageTaskSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public PersonClipTaskView View { get { lock (_gate) return _view; } }
    public event EventHandler? Changed;
    private void OnJobChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Job.State) or nameof(Job.Inputs) or nameof(Job.Output)) RequestLoad();
    }
    private void RequestLoad()
    {
        Interlocked.Increment(ref _loadVersion);
        Volatile.Write(ref _loadRequested, 1);
        if (Interlocked.Exchange(ref _loading, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                do { Volatile.Write(ref _loadRequested, 0); await LoadAsync().ConfigureAwait(false); }
                while (Volatile.Read(ref _loadRequested) != 0 && !_disposed);
            }
            finally
            {
                Volatile.Write(ref _loading, 0);
                if (Volatile.Read(ref _loadRequested) != 0 && !_disposed) RequestLoad();
            }
        });
    }
    private async Task LoadAsync()
    {
        var version = Volatile.Read(ref _loadVersion);
        try
        {
            var sourceKey = _job.Inputs.FirstOrDefault() ?? "";
            var optionsKey = PersonClipTaskState.OptionsKey(_job);
            var summary = _job.PersonClipStages?.Capture().Summary ?? await PersonClipRetry.LoadAsync<PersonClipStageSummary>(AiTaskResults.PathFor(_job, "people-stages")).ConfigureAwait(false);
            if (summary?.Stages is not { Length: 5 } || !summary.Stages.Select(s => s.Status.Stage.Id).SequenceEqual(Stages.Select(s => s.Id))) summary = null;
            var result = _job.PersonDetectionResult ?? await PersonClipRetry.LoadAsync<PersonDetectionTaskResult>(AiTaskResults.PathFor(_job, "people")).ConfigureAwait(false);
            if (result is null && _job.Options.PersonClip?.AnalysisOnly == true)
                result = await PersonClipRetry.LoadAsync<PersonDetectionTaskResult>(_job.Output).ConfigureAwait(false);
            var replayPath = result?.Result.ReplayPath;
            IReadOnlyList<PersonClipReplayFrame> replayFrames;
            lock (_gate) replayFrames = _replayFrames;
            if (replayPath != _replayPath)
                replayFrames = await PersonClipReplayArchive.ReadAsync(replayPath).ConfigureAwait(false);
            MediaInfo? info = summary?.Info;
            if (info is null || !PersonClipRetry.SourceMatches(_job, summary!))
            {
                try { info = await _engine.Probe(sourceKey).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { info = null; }
            }
            var installed = true;
            foreach (var model in PersonClipTaskState.RequiredModels(_job))
                installed &= await _store.IsInstalledAsync(model).ConfigureAwait(false);
            var retryId = "";
            if (_job.State is JobState.Failed or JobState.Cancelled)
            {
                var interrupted = summary?.Stages.FirstOrDefault(s => s.Status.State is StageState.Failed or StageState.Cancelled or StageState.Active or StageState.Paused)?.Status.Stage.Id;
                if (interrupted is PersonClipStageIds.Detect or PersonClipStageIds.Refine or PersonClipStageIds.Export)
                {
                    var retry = await PersonClipRetry.ResolveAsync(_job, interrupted, _store, CancellationToken.None).ConfigureAwait(false);
                    if (retry.StageId == interrupted) retryId = interrupted;
                }
            }
            lock (_gate)
            {
                if (_disposed || version != Volatile.Read(ref _loadVersion) || _job.Inputs.FirstOrDefault() != sourceKey || PersonClipTaskState.OptionsKey(_job) != optionsKey) return;
                _restored = summary; _restoredResult = result; _info = info; _installed = installed; _retryId = retryId;
                _replayPath = replayPath; _replayFrames = replayFrames;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Person-clip session: {0}", error.Message); }
    }
    private void Publish(bool notify = true)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            // Queue admission/removal and filesystem/model changes need no Job property notification.
            // Refresh commands and file metadata at the same bounded cadence as execution observations.
            if (_disposed) return;
            if (Environment.TickCount64 - _lastLoadTick >= 2000 && _job.State is not (JobState.Running or JobState.Stopping))
            { _lastLoadTick = Environment.TickCount64; RequestLoad(); }
            PersonClipStageSummary? restored; PersonDetectionTaskResult? restoredResult; MediaInfo? probed; bool? installed; string retryId;
            IReadOnlyList<PersonClipReplayFrame> replayFrames;
            lock (_gate)
            {
                if (_disposed) return;
                restored = _restored; restoredResult = _restoredResult; probed = _info; installed = _installed; retryId = _retryId;
                replayFrames = _replayFrames;
            }
            var snapshotUtc = DateTime.UtcNow;
            var captured = _job.PersonClipStages?.Capture();
            var summary = captured?.Summary ?? restored;
            var state = _job.State;
            var configuring = state == JobState.Waiting || state == JobState.Paused && summary?.StartedUtc is null;
            var statuses = configuring ? PersonClipTaskState.Plans(_job) : summary?.Stages.Select(item => item.Status).ToArray() ?? PersonClipTaskState.Plans(_job);
            // A process interruption has no live clock; the persisted active stage becomes the cancellation point.
            if (captured is null && state is (JobState.Cancelled or JobState.Failed))
                statuses = statuses.Select(s => s.State is StageState.Active or StageState.Paused
                    ? s with { State = state == JobState.Cancelled ? StageState.Cancelled : StageState.Failed, IsClockRunning = false, Detail = _job.Error } : s).ToArray();
            var current = Array.FindIndex(statuses, s => s.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled);
            if (installed is not null && statuses[1].State == StageState.Pending)
                statuses[1] = statuses[1] with { Detail = statuses[1].Detail + (installed == true ? " · 已安装" : " · 需要下载") };
            var people = _job.PersonDetectionResult ?? (configuring || captured is not null ? null : restoredResult);
            var info = configuring ? probed : summary?.Info ?? people?.Result.Info ?? probed;
            var partial = people?.Result.IsPartial ?? true;
            var segments = people?.Result.Segments.Select((range, i) => new PersonClipKeptSegment(i + 1, range.Start, range.End)
            { IsOpen = captured?.Open == true && i == people.Result.Segments.Count - 1 }).ToArray() ?? [];
            var output = _job.Output; bool exists = false; long? bytes = null;
            try { var file = new FileInfo(output); exists = file.Exists; if (exists) bytes = file.Length; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            var commands = new TaskCommands
            {
                CanStart = _host.CanStart(_job), CanPause = _host.CanPause(_job), CanResume = _host.CanResume(_job),
                CanStop = _host.CanStop(_job), CanRerun = _host.CanRerun(_job), CanChangeOutput = _host.CanChangeOutput(_job),
                RetryStageId = (state is JobState.Failed or JobState.Cancelled) && _host.CanRerun(_job) && summary is not null
                    && PersonClipRetry.SourceMatches(_job, summary)
                    && (retryId != PersonClipStageIds.Refine || File.Exists(AiTaskResults.PathFor(_job, "people-detection")))
                    && (retryId != PersonClipStageIds.Export || people is not null && !people.Result.IsPartial) ? retryId : ""
            };
            var snapshot = new MultiStageTaskSnapshot
            {
                State = state switch { JobState.Waiting => MultiStageTaskState.Configuring, JobState.Running => MultiStageTaskState.Running,
                    JobState.Paused => MultiStageTaskState.Paused, JobState.Stopping => MultiStageTaskState.Stopping,
                    JobState.AwaitingUser => MultiStageTaskState.AwaitingUser, JobState.Failed => MultiStageTaskState.Failed, JobState.Completed => MultiStageTaskState.Completed, _ => MultiStageTaskState.Cancelled },
                Stages = Array.AsReadOnly(statuses), CurrentStageIndex = current, Progress = _job.Progress,
                Elapsed = TimeSpan.FromTicks(statuses.Sum(s => (s.Elapsed ?? TimeSpan.Zero).Ticks)), IsClockRunning = statuses.Any(s => s.IsClockRunning),
                StartedUtc = configuring ? null : summary?.StartedUtc, CompletedUtc = configuring ? null : summary?.CompletedUtc, SnapshotUtc = snapshotUtc,
                Remaining = state == JobState.Running ? _job.Estimate?.Remaining : null, Error = state == JobState.Failed ? _job.Error : "",
                InterruptedStageId = current >= 0 && statuses[current].State is (StageState.Failed or StageState.Cancelled) ? statuses[current].Stage.Id : "",
                RetainedStageIds = commands.RetryStageId.Length == 0 ? [] : statuses.TakeWhile(s => s.Stage.Id != commands.RetryStageId).Where(s => s.State == StageState.Done).Select(s => s.Stage.Id).ToArray(),
                Commands = commands, Runtime = summary?.Runtime ?? new(PersonClipTaskState.Settings(_job).ModelName), Revision = Interlocked.Increment(ref _revision)
            };
            var view = new PersonClipTaskView
            {
                SourcePath = _job.Inputs.FirstOrDefault() ?? "", Info = info, AnalyzedSeconds = people?.Result.AnalyzedSeconds ?? 0,
                SampledFrames = people?.Result.SampledFrames ?? 0, PlannedFrames = info is null ? null : PersonClipTaskState.PlannedFrames(info, _job.Options.PersonClip!.Detection),
                Segments = Array.AsReadOnly(segments), IsPartial = partial, HasResult = people is not null, KeptSeconds = segments.Sum(s => s.Duration),
                LiveFrame = captured?.Frame, Settings = PersonClipTaskState.Settings(_job) with { ModelInstalled = statuses[1].State == StageState.Done ? true : installed },
                ReplayFrames = replayFrames,
                OutputPath = output, OutputFolder = Path.GetDirectoryName(output) ?? "", OutputExists = exists, OutputBytes = bytes,
                CanAdjust = (state is JobState.Completed or JobState.Failed or JobState.Cancelled) && people is not null && !partial
                    && PersonClipRetry.SourceMatches(_job, people) && (summary is null || statuses[3].State is StageState.Done or StageState.Skipped)
            };
            lock (_gate)
            {
                if (_disposed) return;
                _snapshot = snapshot; _view = view;
            }
            if (notify) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Person-clip snapshot: {0}", error.Message); }
        finally { Volatile.Write(ref _refreshing, 0); }
    }
    public async Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        var path = AiTaskResults.PathFor(_job, "people-log", "jsonl");
        var entries = new List<TaskLogEntry>();
        if (File.Exists(path))
        {
            string text;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { text = ""; }
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                try { if (JsonSerializer.Deserialize<TaskLogEntry>(line) is { } entry) entries.Add(entry); }
                catch (JsonException) { } // A concurrent append/process interruption may leave a partial last line.
            }
            if (entries.Count > 0) return entries.AsReadOnly();
        }
        foreach (var line in (await _job.ReadLogAsync(ct).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var message = line.Trim(); var utc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
            if (message.StartsWith('[') && message.IndexOf(']') is var end && end > 1
                && DateTime.TryParse(message[1..end], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
            { utc = time.ToUniversalTime(); message = message[(end + 1)..].Trim(); }
            var stage = Stages.FirstOrDefault(s => message.Contains(s.Title, StringComparison.Ordinal));
            entries.Add(new(utc, stage?.Id ?? "", stage?.Title ?? "", message,
                message.Contains("取消", StringComparison.Ordinal) || message.Contains("中断", StringComparison.Ordinal) || message.Contains("停止", StringComparison.Ordinal)
                    ? TaskLogLevel.Marker : message.Contains("失败", StringComparison.Ordinal) ? TaskLogLevel.Error
                    : message.Contains("回退", StringComparison.Ordinal) ? TaskLogLevel.Warning : TaskLogLevel.Info));
        }
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
        Ensure(_host.CanRerun(_job) && stageId.Length > 0 && Snapshot.Commands.RetryStageId == stageId);
        // Recheck after the displayed command: sources/checkpoints can disappear while a window stays open.
        var retry = await PersonClipRetry.ResolveAsync(_job, stageId, _store, CancellationToken.None).ConfigureAwait(false);
        if (retry.StageId is null) _job.AppendLog(retry.FallbackReason);
        await _host.RerunAsync(_job, stageId).ConfigureAwait(false);
    }
    public Task ChangeOutputFolderAsync(string folder) { Ensure(_host.CanChangeOutput(_job)); return _host.ChangeOutputFolderAsync(_job, folder); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Changed = null; }
        _job.PropertyChanged -= OnJobChanged;
        _timer.Dispose();
    }
}
