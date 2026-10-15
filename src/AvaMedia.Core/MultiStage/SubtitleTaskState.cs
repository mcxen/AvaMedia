using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("AvaMedia.SubtitleTests")]

namespace AvaMedia.Core;

internal sealed record SubtitleStageSummary(TaskStageStatus[] Stages, DateTime? StartedUtc, DateTime? CompletedUtc,
    TaskRuntimeInfo Runtime, string SourcePath)
{
    public SubtitleTaskObservation? Observation { get; init; }
}

internal sealed record SubtitleTaskObservation
{
    public MediaInfo? Info { get; init; }
    public double RangeStart { get; init; }
    public double? RangeEnd { get; init; }
    public double RecognizedSeconds { get; init; }
    public SubtitleTaskResult? Result { get; init; }
}

public sealed partial class Job
{
    private SubtitleTaskState? _subtitleStages;
    [JsonIgnore] internal SubtitleTaskState? SubtitleStages
    {
        get => Volatile.Read(ref _subtitleStages);
        set { Volatile.Write(ref _subtitleStages, value); Raise(); }
    }
}

/// <summary>Execution-owned observations, retained when all window sessions are closed.</summary>
internal sealed class SubtitleTaskState : IDisposable
{
    private readonly object _gate = new();
    private readonly Job _job;
    private readonly TimeProvider _time;
    private readonly bool _persist;
    private readonly List<TaskLogEntry> _logs = [];
    private SubtitleStageSummary _summary;
    private int _active = -1;
    private bool _paused, _finished;
    private long _tick, _lastSave;

    internal SubtitleTaskState(Job job, bool persist = true, TimeProvider? time = null)
    {
        _job = job; _persist = persist; _time = time ?? TimeProvider.System;
        _summary = new(Plans(job), null, null, new("Whisper " + (job.Options.Transcription ?? new()).Model), job.Inputs.FirstOrDefault() ?? "");
        if (_persist)
        {
            AiTaskResults.DeleteResult(job, "subtitle-stages");
            var logPath = AiTaskResults.PathFor(job, "subtitle-log", "jsonl");
            if (File.Exists(logPath)) File.Delete(logPath);
        }
        job.SubtitleStages = this;
        job.PropertyChanged += OnJobChanged;
    }

    internal static TaskStageStatus[] Plans(Job job)
    {
        var speech = job.Options.Transcription ?? new();
        string[] details = [$"音轨 {job.Options.AudioStreamIndex + 1}", "Whisper " + speech.Model,
            speech.ReviewedCues is null ? "语言 · " + speech.Language : $"已有校对字幕 · {speech.ReviewedCues.Length} 条字幕",
            "检查字幕与源文件", job.Options.Format.ToUpperInvariant()];
        return SubtitleStageIds.All.Select((stage, i) => new TaskStageStatus(stage, StageState.Pending) { Detail = details[i] }).ToArray();
    }

    internal SubtitleStageSummary Capture()
    {
        lock (_gate)
        {
            var stages = _summary.Stages.ToArray();
            if (_active >= 0 && !_paused)
                stages[_active] = stages[_active] with
                { Elapsed = (stages[_active].Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = true };
            return _summary with { Stages = stages };
        }
    }

    internal IReadOnlyList<TaskLogEntry> ReadLog() { lock (_gate) return Array.AsReadOnly(_logs.ToArray()); }

    internal void Source(MediaInfo info, double start, double end)
    {
        lock (_gate)
        {
            _summary = _summary with { Observation = new() { Info = info, RangeStart = start, RangeEnd = end } };
            SaveLocked();
        }
    }

    internal void Recognized(double sourceSeconds)
    {
        lock (_gate)
        {
            if (_summary.Observation is not { } observation || !double.IsFinite(sourceSeconds)) return;
            _summary = _summary with { Observation = observation with
            { RecognizedSeconds = Math.Max(observation.RecognizedSeconds, sourceSeconds) } };
        }
    }

    internal void Transcript(SubtitleTaskResult result)
    {
        lock (_gate)
        {
            if (_summary.Observation is not { } observation) return;
            _summary = _summary with { Observation = observation with { Result = result } };
            // Partial cues are checkpointed with stage updates; complete results must survive a write failure.
            if (!result.IsPartial || _time.GetElapsedTime(_lastSave) >= TimeSpan.FromSeconds(2)) SaveLocked();
        }
    }

    internal void Start(string id)
    {
        lock (_gate)
        {
            if (_finished || _active >= 0) throw new InvalidOperationException("Previous subtitle stage is still active or execution has ended.");
            _active = Index(id); _paused = _job.State == JobState.Paused;
            _tick = _time.GetTimestamp();
            _summary = _summary with { StartedUtc = _summary.StartedUtc ?? _time.GetUtcNow().UtcDateTime };
            _summary.Stages[_active] = _summary.Stages[_active] with
            { State = _paused ? StageState.Paused : StageState.Active, Elapsed = TimeSpan.Zero, IsClockRunning = !_paused };
            LogLocked(id, "开始 · " + _summary.Stages[_active].Detail);
            SaveLocked();
        }
    }

    internal void Update(string detail, double? current = null, double? total = null, string unit = "")
    {
        lock (_gate)
        {
            if (_active < 0 || _finished) return;
            _summary.Stages[_active] = _summary.Stages[_active] with
            {
                Detail = detail, Current = current, Total = total, Unit = unit,
                Fraction = total > 0 && current is { } count ? Math.Clamp(count / total.Value, 0, 1) : null
            };
            if (_time.GetElapsedTime(_lastSave) >= TimeSpan.FromSeconds(2))
            {
                AccumulateLocked(); LogLocked(_summary.Stages[_active].Stage.Id, detail); SaveLocked();
            }
        }
    }

    internal void Backend(string backend, string? note)
    {
        lock (_gate)
        {
            _summary = _summary with { Runtime = _summary.Runtime with { Backend = backend, Note = note ?? _summary.Runtime.Note } };
            if (note is not null) LogLocked(_active >= 0 ? _summary.Stages[_active].Stage.Id : "", note, TaskLogLevel.Warning);
            SaveLocked();
        }
    }

    internal void Complete(string id, string detail, bool skipped = false)
    {
        lock (_gate)
        {
            var index = Index(id);
            if (_active != index && !skipped) throw new InvalidOperationException("Subtitle stage is not active.");
            AccumulateLocked();
            _summary.Stages[index] = _summary.Stages[index] with
            { State = skipped ? StageState.Skipped : StageState.Done, Detail = detail, Fraction = null, IsClockRunning = false };
            if (_active == index) _active = -1;
            LogLocked(id, detail); SaveLocked();
        }
    }

    internal void Finish(JobState state, string error = "")
    {
        lock (_gate)
        {
            if (_finished) return;
            AccumulateLocked(); _finished = true;
            if (_active >= 0)
            {
                var item = _summary.Stages[_active];
                var cancelled = state == JobState.Cancelled;
                var detail = cancelled ? "已取消" : error;
                _summary.Stages[_active] = item with
                { State = cancelled ? StageState.Cancelled : StageState.Failed, Detail = detail, IsClockRunning = false };
                LogLocked(item.Stage.Id, detail, cancelled ? TaskLogLevel.Marker : TaskLogLevel.Error);
                _active = -1;
            }
            _summary = _summary with { CompletedUtc = _time.GetUtcNow().UtcDateTime };
            SaveLocked();
        }
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Job.State)) return;
        lock (_gate)
        {
            if (_active < 0 || _finished) return;
            var paused = _job.State == JobState.Paused;
            if (_paused == paused) return;
            AccumulateLocked(); _paused = paused; _tick = _time.GetTimestamp();
            _summary.Stages[_active] = _summary.Stages[_active] with
            { State = paused ? StageState.Paused : StageState.Active, IsClockRunning = !paused };
            LogLocked(_summary.Stages[_active].Stage.Id, paused ? "已暂停" : "继续执行"); SaveLocked();
        }
    }

    private void AccumulateLocked()
    {
        if (_active < 0 || _paused) return;
        var item = _summary.Stages[_active];
        _summary.Stages[_active] = item with { Elapsed = (item.Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick) };
        _tick = _time.GetTimestamp();
    }

    private void LogLocked(string id, string message, TaskLogLevel level = TaskLogLevel.Info)
    {
        var title = SubtitleStageIds.All.FirstOrDefault(s => s.Id == id)?.Title ?? "";
        var entry = new TaskLogEntry(_time.GetUtcNow().UtcDateTime, id, title, message, level);
        _logs.Add(entry);
        if (!_persist) return;
        var path = AiTaskResults.PathFor(_job, "subtitle-log", "jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        _job.AppendLog($"[{entry.TimeUtc:O}] [{title}] {message}");
    }

    private void SaveLocked()
    {
        _lastSave = _time.GetTimestamp();
        if (!_persist) return;
        var path = AiTaskResults.PathFor(_job, "subtitle-stages");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(Capture())); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }

    private static int Index(string id)
    {
        for (var i = 0; i < SubtitleStageIds.All.Count; i++) if (SubtitleStageIds.All[i].Id == id) return i;
        throw new ArgumentException("Unknown subtitle stage: " + id, nameof(id));
    }

    public void Dispose() => _job.PropertyChanged -= OnJobChanged;
}
