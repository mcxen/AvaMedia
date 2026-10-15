using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("AvaMedia.ClassificationTests")]

namespace AvaMedia.Core;

internal sealed record MediaClassificationSummary(TaskStageStatus[] Stages, DateTime? StartedUtc = null,
    DateTime? CompletedUtc = null, TaskRuntimeInfo? Runtime = null);

public sealed partial class Job
{
    private MediaClassificationTaskState? _mediaClassificationStages;
    [JsonIgnore] internal MediaClassificationTaskState? MediaClassificationStages
    {
        get => Volatile.Read(ref _mediaClassificationStages);
        set { Volatile.Write(ref _mediaClassificationStages, value); Raise(); }
    }
}

/// <summary>Execution-owned observations. Switching work categories does not complete unfinished work.</summary>
internal sealed class MediaClassificationTaskState
{
    private readonly object _gate = new();
    private readonly Job _job;
    private readonly TimeProvider _time;
    private MediaClassificationSummary _summary;
    private int _active = -1;
    private long _tick;
    private bool _paused, _finished;
    private long _lastSave;
    internal static string SummaryPath(Job job) => AiTaskResults.PathFor(job, "classification-stages");
    internal static string LogPath(Job job) => AiTaskResults.PathFor(job, "classification-log", "jsonl");
    internal static TaskStageStatus[] Plans(Job job) => MediaClassificationStageIds.For(job)
        .Select(stage => new TaskStageStatus(stage, StageState.Pending)).ToArray();

    internal MediaClassificationTaskState(Job job, TimeProvider? time = null)
    {
        _job = job; _time = time ?? TimeProvider.System;
        _summary = new(Plans(job));
        Directory.CreateDirectory(Path.GetDirectoryName(SummaryPath(job))!);
        File.WriteAllText(LogPath(job), "");
        _job.MediaClassificationStages = this;
        _job.PropertyChanged += OnJobChanged;
        Enter(MediaClassificationStageIds.ScanFiles);
    }

    internal MediaClassificationSummary Capture()
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

    internal string CurrentStageId
    { get { lock (_gate) return _active < 0 ? "" : _summary.Stages[_active].Stage.Id; } }

    internal void Enter(string id, string detail = "")
    {
        lock (_gate)
        {
            if (_finished) return;
            var index = Array.FindIndex(_summary.Stages, stage => stage.Stage.Id == id);
            if (index < 0) throw new ArgumentException("未知分类阶段：" + id, nameof(id));
            if (_active == index) return;
            Accumulate();
            if (_active >= 0) _summary.Stages[_active] = _summary.Stages[_active] with { State = StageState.Pending };
            _active = index; _paused = _job.State == JobState.Paused; _tick = _time.GetTimestamp();
            _summary = _summary with { StartedUtc = _summary.StartedUtc ?? _time.GetUtcNow().UtcDateTime };
            var previous = _summary.Stages[index];
            _summary.Stages[index] = previous with { State = _paused ? StageState.Paused : StageState.Active,
                Detail = detail, Current = null, Total = null, Fraction = null, Unit = "",
                Elapsed = previous.Elapsed ?? TimeSpan.Zero, IsClockRunning = !_paused };
            Log(id, "开始 · " + detail); Save();
        }
    }

    internal void Update(string detail, double? current = null, double? total = null, string unit = "")
    {
        lock (_gate)
        {
            if (_active < 0 || _finished) return;
            var stage = _summary.Stages[_active];
            _summary.Stages[_active] = stage with { Detail = detail, Current = current, Total = total, Unit = unit,
                Fraction = total > 0 && current is { } count ? Math.Clamp(count / total.Value, 0, 1) : null };
            if (_time.GetElapsedTime(_lastSave) >= TimeSpan.FromSeconds(2))
            { Log(stage.Stage.Id, detail); Accumulate(); Save(); }
        }
    }

    internal void Observe(MediaTagProgress update)
    {
        if (update.StageId.Length > 0) Enter(update.StageId);
        if (update.Activity is { } activity)
        {
            Update(activity.Stage + (activity.Detail.Length > 0 ? " · " + activity.Detail : ""), activity.Current, activity.Total, activity.Unit);
            Runtime(activity.Model, activity.Backend);
        }
        if (update.Error is { } error) Warning(error);
        if (update.Result is { } result)
        {
            Runtime("JoyTag", result.Backend, result.FallbackReason ?? "");
            foreach (var warning in new[] { result.SceneError, result.CaptionError }.OfType<string>()) Warning(warning);
        }
    }

    internal void Warning(string message)
    { lock (_gate) Log(_active < 0 ? "" : _summary.Stages[_active].Stage.Id, message, TaskLogLevel.Warning); }

    internal void Runtime(string model, string backend, string note = "")
    {
        lock (_gate)
        {
            var previous = _summary.Runtime ?? new("");
            _summary = _summary with { Runtime = new(model, backend, note) };
            if (previous != _summary.Runtime) Save();
        }
    }

    internal void Complete(string id, string detail, bool skipped = false)
    {
        lock (_gate)
        {
            var index = Array.FindIndex(_summary.Stages, stage => stage.Stage.Id == id);
            if (index < 0 || _finished) return;
            Accumulate();
            var stage = _summary.Stages[index];
            _summary.Stages[index] = stage with { State = skipped ? StageState.Skipped : StageState.Done,
                Detail = detail, Fraction = null, IsClockRunning = false };
            if (_active == index) _active = -1;
            Log(id, detail); Save();
        }
    }

    internal void Finish(JobState state, string error = "")
    {
        lock (_gate)
        {
            if (_finished) return;
            Accumulate();
            if (_active >= 0 && state != JobState.Completed)
            {
                var stage = _summary.Stages[_active];
                _summary.Stages[_active] = stage with { State = state == JobState.Cancelled ? StageState.Cancelled : StageState.Failed,
                    Detail = error, IsClockRunning = false };
                Log(stage.Stage.Id, error, state == JobState.Cancelled ? TaskLogLevel.Marker : TaskLogLevel.Error);
            }
            else if (state is JobState.Cancelled or JobState.Failed)
                Log("", error, state == JobState.Cancelled ? TaskLogLevel.Marker : TaskLogLevel.Error);
            _active = -1; _finished = true;
            _summary = _summary with { CompletedUtc = _time.GetUtcNow().UtcDateTime };
            Save(); _job.PropertyChanged -= OnJobChanged;
        }
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Job.State)) return;
        lock (_gate)
        {
            var paused = _job.State == JobState.Paused;
            if (_active < 0 || _finished || paused == _paused) return;
            Accumulate(); _paused = paused; _tick = _time.GetTimestamp();
            var stage = _summary.Stages[_active];
            _summary.Stages[_active] = stage with { State = paused ? StageState.Paused : StageState.Active, IsClockRunning = !paused };
            Log(stage.Stage.Id, paused ? "已暂停" : "继续执行"); Save();
        }
    }

    private void Accumulate()
    {
        if (_active < 0 || _paused) return;
        var stage = _summary.Stages[_active];
        _summary.Stages[_active] = stage with
        { Elapsed = (stage.Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = false };
        _tick = _time.GetTimestamp();
    }
    private void Save()
    {
        var path = SummaryPath(_job); var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(Capture())); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
        _lastSave = _time.GetTimestamp();
    }
    private void Log(string id, string message, TaskLogLevel level = TaskLogLevel.Info)
    {
        var title = _summary.Stages.FirstOrDefault(stage => stage.Stage.Id == id)?.Stage.Title ?? "";
        var entry = new TaskLogEntry(_time.GetUtcNow().UtcDateTime, id, title, message, level);
        File.AppendAllText(LogPath(_job), JsonSerializer.Serialize(entry) + Environment.NewLine);
    }
}
