using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Window-scoped observer for automatic subtitles; execution remains owned by the queue.</summary>
public sealed class SubtitleTaskSession : ISubtitleTask
{
    private readonly Job _job;
    private readonly IMultiStageQueueHost _host;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private SubtitleStageSummary? _restored;
    private SubtitleTaskResult? _savedResult, _projectedResult;
    private IReadOnlyList<SubtitleLineView> _lines = [];
    private bool _restoreLoaded;
    private int _resetRestore;
    private MultiStageTaskSnapshot _snapshot = new();
    private SubtitleTaskView _view = new();
    private long _revision;
    private int _refreshing;
    private volatile bool _disposed;

    public SubtitleTaskSession(Job job, IMultiStageQueueHost host)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(host);
        if (job.FeatureId != "auto-subtitle") throw new ArgumentException("需要自动字幕任务。", nameof(job));
        _job = job; _host = host;
        _timer = new(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
        _job.PropertyChanged += OnJobChanged;
        Publish(false, readDisk: false);
        _timer.Change(0, 250);
    }

    public string FeatureId => "auto-subtitle";
    public IReadOnlyList<TaskStageDescriptor> Stages => SubtitleStageIds.All;
    public MultiStageTaskSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public SubtitleTaskView View { get { lock (_gate) return _view; } }
    public event EventHandler? Changed;

    private void OnJobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Job.State) && _job.State == JobState.Waiting)
            Interlocked.Exchange(ref _resetRestore, 1);
    }

    private static SubtitleStageSummary? LoadSummary(Job job)
    {
        try
        {
            var path = AiTaskResults.PathFor(job, "subtitle-stages");
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var summary = JsonSerializer.Deserialize<SubtitleStageSummary>(stream);
            return summary is not null && summary.SourcePath == job.Inputs.FirstOrDefault()
                && summary.Stages is { Length: 5 }
                && summary.Stages.All(s => s?.Stage is not null)
                && (summary.Observation?.Result is null || ValidResult(summary.Observation.Result))
                && summary.Stages.Select(s => s.Stage.Id).SequenceEqual(SubtitleStageIds.All.Select(s => s.Id)) ? summary : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { System.Diagnostics.Trace.TraceWarning("Subtitle stages: {0}", error.Message); return null; }
    }

    private void Publish(bool notify = true, bool readDisk = true)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            if (_disposed) return;
            // Do not hold the session lock while entering execution state or queue code.
            var live = _job.SubtitleStages;
            if (Interlocked.Exchange(ref _resetRestore, 0) != 0 || _job.State == JobState.Waiting || live is not null)
            {
                _restoreLoaded = false; _restored = null; _savedResult = null;
            }
            if (readDisk && live is null && !_restoreLoaded && _job.State != JobState.Waiting)
            {
                _restored = LoadSummary(_job);
                _savedResult = LoadResult(_job);
                _restoreLoaded = true;
            }
            var summary = live?.Capture() ?? _restored;
            var state = _job.State;
            var configuring = state == JobState.Waiting || state == JobState.Paused && (summary is null || summary.CompletedUtc is not null);
            if (configuring) summary = null;
            var result = configuring ? null : live is not null
                ? summary?.Observation is null ? null : _job.SubtitleResult ?? summary.Observation.Result
                : _job.SubtitleResult ?? _savedResult ?? summary?.Observation?.Result;
            // The review entry point consumes Job.SubtitleResult or subtitles.json. A complete
            // observation checkpoint must also be usable when the separate result file is missing.
            if (readDisk && live is null && _job.SubtitleResult is null && result is { IsPartial: false }
                && state is JobState.Completed or JobState.Failed or JobState.Cancelled)
                _job.SubtitleResult = result;
            var view = CreateView(summary, result, state, readDisk);
            var commands = new TaskCommands
            {
                CanStart = _host.CanStart(_job), CanPause = _host.CanPause(_job), CanResume = _host.CanResume(_job),
                CanStop = _host.CanStop(_job), CanRerun = _host.CanRerun(_job), CanChangeOutput = _host.CanChangeOutput(_job)
            };
            lock (_gate)
            {
                if (_disposed) return;
                var stages = summary?.Stages ?? SubtitleTaskState.Plans(_job);
                if (live is null && summary is not null)
                {
                    // Persisted clocks never advance after a process interruption.
                    stages = stages.Select(s => s with
                    {
                        IsClockRunning = false,
                        Detail = s.State is StageState.Active or StageState.Paused && state is JobState.Failed or JobState.Cancelled
                            ? (state == JobState.Failed ? _job.Error : "已取消") : s.Detail,
                        State = s.State is StageState.Active or StageState.Paused ? state switch
                        {
                            JobState.Failed => StageState.Failed,
                            JobState.Cancelled => StageState.Cancelled,
                            _ => s.State
                        } : s.State
                    }).ToArray();
                }
                var current = Array.FindIndex(stages, s => s.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled);
                _snapshot = new()
                {
                    State = state switch
                    {
                        JobState.Waiting => MultiStageTaskState.Configuring, JobState.Running => MultiStageTaskState.Running,
                        JobState.Paused => MultiStageTaskState.Paused, JobState.Stopping => MultiStageTaskState.Stopping,
                        JobState.AwaitingUser => MultiStageTaskState.AwaitingUser, JobState.Failed => MultiStageTaskState.Failed, JobState.Completed => MultiStageTaskState.Completed,
                        _ => MultiStageTaskState.Cancelled
                    },
                    Stages = Array.AsReadOnly(stages), CurrentStageIndex = current,
                    Progress = _job.Progress, SnapshotUtc = DateTime.UtcNow,
                    Elapsed = TimeSpan.FromTicks(stages.Sum(s => s.Elapsed?.Ticks ?? 0)),
                    IsClockRunning = stages.Any(s => s.IsClockRunning),
                    StartedUtc = summary?.StartedUtc, CompletedUtc = summary?.CompletedUtc,
                    Remaining = state == JobState.Running ? _job.Estimate?.Remaining : null,
                    Error = state == JobState.Failed ? _job.Error : "",
                    InterruptedStageId = current >= 0 && stages[current].State is StageState.Failed or StageState.Cancelled ? stages[current].Stage.Id : "",
                    Runtime = summary?.Runtime ?? new("Whisper " + (_job.Options.Transcription ?? new()).Model),
                    Commands = commands,
                    Revision = ++_revision
                };
                _view = view;
            }
            if (notify) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Subtitle snapshot: {0}", error.Message); }
        finally { Volatile.Write(ref _refreshing, 0); }
    }

    private static SubtitleTaskResult? LoadResult(Job job)
    {
        try
        {
            using var stream = new FileStream(AiTaskResults.PathFor(job, "subtitles"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var result = JsonSerializer.Deserialize<SubtitleTaskResult>(stream);
            return ValidResult(result) ? result : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { System.Diagnostics.Trace.TraceWarning("Subtitle result: {0}", error.Message); return null; }
    }

    private static bool ValidResult(SubtitleTaskResult? result) => result?.Cues is not null && result.Cues.All(c => c is not null);

    private SubtitleTaskView CreateView(SubtitleStageSummary? summary, SubtitleTaskResult? result, JobState state, bool readDisk)
    {
        if (!ReferenceEquals(_projectedResult, result))
        {
            _projectedResult = result;
            _lines = Array.AsReadOnly(result?.Cues.Select((cue, index) => new SubtitleLineView(index + 1,
                cue.Start.TotalSeconds, cue.End.TotalSeconds, cue.Text) { Language = KnownLanguage(cue.Language) }).ToArray() ?? []);
        }
        var options = _job.Options;
        var observation = summary?.Observation;
        var source = _job.Inputs.FirstOrDefault() ?? "";
        var output = new TaskOutputView
        {
            Path = _job.Output,
            Folder = string.IsNullOrEmpty(_job.Output) ? "" : Path.GetDirectoryName(Path.GetFullPath(_job.Output)) ?? "",
            IsFixedLocation = options.Transcription?.RecognitionOnly == true
        };
        var sourceMatches = false;
        if (readDisk)
        {
            try
            {
                var file = new FileInfo(output.Path);
                output = output with { Exists = file.Exists, Bytes = file.Exists ? file.Length : null };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { System.Diagnostics.Trace.TraceWarning("Subtitle output: {0}", error.Message); }
            if (result is { IsPartial: false, Cues.Length: > 0 })
            {
                try
                {
                    var file = new FileInfo(source);
                    sourceMatches = file.Exists && file.Length == result.SourceLength && file.LastWriteTimeUtc == result.SourceWriteUtc;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { System.Diagnostics.Trace.TraceWarning("Subtitle source: {0}", error.Message); }
            }
        }
        var languages = _lines.Select(line => line.Language).Where(value => value.Length > 0).Distinct().ToArray();
        return new()
        {
            SourcePath = source, Info = observation?.Info,
            RangeStart = observation?.RangeStart ?? options.Start,
            RangeEnd = observation?.RangeEnd ?? (options.End > 0 ? options.End : null),
            RecognizedSeconds = observation?.RecognizedSeconds ?? 0,
            Lines = _lines,
            Language = languages.Length > 0 ? string.Join("、", languages) : KnownLanguage(options.Transcription?.Language),
            ModelName = summary?.Runtime.Model ?? "Whisper " + (options.Transcription ?? new()).Model,
            OutputFormat = options.Format.ToLowerInvariant() switch
            {
                "srt" => "SRT", "ass" => "ASS", "vtt" => "VTT",
                var format when VideoFormats.OriginalOutputExtensions.Contains(format) => "烧录到视频",
                _ => ""
            },
            HasResult = _lines.Count > 0, IsPartial = result?.IsPartial ?? true,
            CanReview = sourceMatches && state is JobState.Completed or JobState.Failed or JobState.Cancelled,
            Output = output
        };
    }

    private static string KnownLanguage(string? language) => string.IsNullOrWhiteSpace(language) || language == "auto" ? "" : language;

    public async Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (_job.State == JobState.Waiting || _job.State == JobState.Paused && Snapshot.StartedUtc is null) return [];
        if (_job.SubtitleStages is { } live) return live.ReadLog();
        var entries = new List<TaskLogEntry>();
        try
        {
            await using var stream = new FileStream(AiTaskResults.PathFor(_job, "subtitle-log", "jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                try { if (JsonSerializer.Deserialize<TaskLogEntry>(line) is { } entry) entries.Add(entry); }
                catch (JsonException) { } // Ignore an interrupted or concurrently appended final line.
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        return entries.AsReadOnly();
    }

    private void Ensure(bool allowed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!allowed) throw new InvalidOperationException("当前任务状态不支持该操作。");
    }
    public Task StartAsync() { Ensure(_host.CanStart(_job)); return _host.StartAsync(_job); }
    public Task PauseAsync() { Ensure(_host.CanPause(_job)); _host.Pause(_job); return Task.CompletedTask; }
    public Task ResumeAsync() { Ensure(_host.CanResume(_job)); return _host.ResumeAsync(_job); }
    public Task StopAsync() { Ensure(_host.CanStop(_job)); _host.Stop(_job); return Task.CompletedTask; }
    public Task RerunAsync() { Ensure(_host.CanRerun(_job)); return _host.RerunAsync(_job, null); }
    public Task RetryFromStageAsync(string stageId)
    {
        Ensure(false); // Per-stage retry needs durable model/audio/inference checkpoints; this pipeline offers a full rerun only.
        return Task.CompletedTask;
    }
    public Task ChangeOutputFolderAsync(string folder) { Ensure(_host.CanChangeOutput(_job)); return _host.ChangeOutputFolderAsync(_job, folder); }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Changed = null; }
        _job.PropertyChanged -= OnJobChanged;
        _timer.Dispose();
    }
}
