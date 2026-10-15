using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("AvaMedia.BatchRotateTests")]

namespace AvaMedia.Core;

public sealed partial class Job
{
    private OrientationTaskState? _orientationStages;
    [JsonIgnore] internal OrientationTaskState? OrientationStages
    {
        get => Volatile.Read(ref _orientationStages);
        set
        {
            _orientationStages?.Detach();
            Volatile.Write(ref _orientationStages, value);
        }
    }
}

internal sealed record OrientationStageSummary(TaskStageStatus[] Stages, DateTime? StartedUtc,
    DateTime? CompletedUtc, TaskRuntimeInfo Runtime, string SourcePath, string OptionsKey);

/// <summary>Execution-owned observations, independent of any observer window.</summary>
internal sealed class OrientationTaskState
{
    private readonly object _gate = new();
    private readonly Job _job;
    private readonly TimeProvider _time;
    private OrientationStageSummary _summary;
    private int _active = -1;
    private bool _paused;
    private long _tick;
    private long _lastLog;
    private JobState? _finished;

    internal OrientationTaskState(Job job, TimeProvider? time = null)
    {
        _job = job; _time = time ?? TimeProvider.System;
        _summary = new(Plans(), null, null, new("YuNet"), job.Inputs.FirstOrDefault() ?? "", OptionsKey(job));
        job.PropertyChanged += OnJobChanged;
    }
    internal static string OptionsKey(Job job) => JsonSerializer.Serialize(new
    { job.Options.Orientation, job.Options.VideoStreamIndex, job.Options.AudioStreamIndex });
    internal static TaskStageStatus[] Plans() => OrientationStageIds.All.Select(s => new TaskStageStatus(s, StageState.Pending)
    { Detail = s.Description }).ToArray();
    internal void Detach() => _job.PropertyChanged -= OnJobChanged;
    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Job.State)) return;
        if (_job.State == JobState.Waiting)
        {
            lock (_gate)
            {
                _summary = new(Plans(), null, null, new("YuNet"), _job.Inputs.FirstOrDefault() ?? "", OptionsKey(_job));
                _active = -1; _finished = null;
                SaveLocked();
            }
        }
        else if (_job.State is JobState.Completed or JobState.Failed or JobState.Cancelled) Finish(_job.State, _job.Error);
        else Pause(_job.State == JobState.Paused);
    }
    internal OrientationStageSummary Capture()
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
    internal void Start(string id)
    {
        lock (_gate)
        {
            var index = Index(id);
            if (_active == index) return;
            if (_active >= 0) throw new InvalidOperationException("上一个方向检测阶段尚未结束。");
            _active = index; _paused = _job.State == JobState.Paused;
            _tick = _lastLog = _time.GetTimestamp();
            _summary = _summary with { StartedUtc = _summary.StartedUtc ?? _time.GetUtcNow().UtcDateTime };
            _summary.Stages[index] = _summary.Stages[index] with
            { State = _paused ? StageState.Paused : StageState.Active, Elapsed = TimeSpan.Zero, IsClockRunning = !_paused };
            LogLocked(id, "开始 · " + _summary.Stages[index].Detail); SaveLocked();
        }
    }
    internal void Update(OrientationDetectionProgress progress)
    {
        lock (_gate)
        {
            if (_active < 0) return;
            var stage = _summary.Stages[_active];
            _summary.Stages[_active] = stage with
            {
                Current = progress.CompletedFrames, Total = progress.TotalFrames, Unit = "帧次",
                Fraction = progress.TotalFrames > 0 ? Math.Clamp((double)progress.CompletedFrames / progress.TotalFrames, 0, 1) : null,
                Detail = $"已处理 {progress.CompletedFrames}/{progress.TotalFrames} 帧次 · {MediaEngine.Number(progress.Seconds)} 秒"
            };
            if (progress.PreviewResult is { } preview) RuntimeLocked(preview);
            if (_time.GetElapsedTime(_lastLog) >= TimeSpan.FromSeconds(2))
            { LogLocked(stage.Stage.Id, _summary.Stages[_active].Detail); AccumulateLocked(); SaveLocked(); _lastLog = _time.GetTimestamp(); }
        }
    }
    internal void Runtime(VideoOrientationResult result) { lock (_gate) RuntimeLocked(result); }
    private void RuntimeLocked(VideoOrientationResult result) => _summary = _summary with
    { Runtime = new("YuNet", result.Backend, result.FallbackReason ?? "") };
    internal void Complete(string id, string detail, bool skipped = false)
    {
        lock (_gate)
        {
            var index = Index(id); AccumulateLocked();
            _summary.Stages[index] = _summary.Stages[index] with
            { State = skipped ? StageState.Skipped : StageState.Done, Detail = detail, Fraction = null, IsClockRunning = false };
            if (_active == index) _active = -1;
            LogLocked(id, detail); SaveLocked();
        }
    }
    private void Pause(bool paused)
    {
        lock (_gate)
        {
            if (_active < 0 || _paused == paused) return;
            AccumulateLocked(); _paused = paused; _tick = _time.GetTimestamp();
            _summary.Stages[_active] = _summary.Stages[_active] with
            { State = paused ? StageState.Paused : StageState.Active, IsClockRunning = !paused };
            LogLocked(_summary.Stages[_active].Stage.Id, paused ? "已暂停" : "继续执行"); SaveLocked();
        }
    }
    internal void Finish(JobState state, string error = "")
    {
        lock (_gate)
        {
            if (_finished == state) return;
            _finished = state; AccumulateLocked();
            if (_active >= 0 && state is JobState.Failed or JobState.Cancelled)
            {
                var stage = _summary.Stages[_active];
                var detail = state == JobState.Cancelled ? "已取消" : error;
                _summary.Stages[_active] = stage with
                { State = state == JobState.Cancelled ? StageState.Cancelled : StageState.Failed, Detail = detail, IsClockRunning = false };
                LogLocked(stage.Stage.Id, detail, state == JobState.Cancelled ? TaskLogLevel.Marker : TaskLogLevel.Error);
                _active = -1;
            }
            _summary = _summary with { CompletedUtc = _time.GetUtcNow().UtcDateTime }; SaveLocked();
        }
    }
    private void AccumulateLocked()
    {
        if (_active < 0 || _paused) return;
        var stage = _summary.Stages[_active];
        _summary.Stages[_active] = stage with
        { Elapsed = (stage.Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = false };
        _tick = _time.GetTimestamp();
    }
    private void LogLocked(string id, string message, TaskLogLevel level = TaskLogLevel.Info)
    {
        var title = OrientationStageIds.All.First(s => s.Id == id).Title;
        var entry = new TaskLogEntry(_time.GetUtcNow().UtcDateTime, id, title, message, level);
        var path = AiTaskResults.PathFor(_job, "orientation-log", "jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        _job.AppendLog($"[{entry.TimeUtc:O}] [{title}] {message}");
    }
    private void SaveLocked()
    {
        var path = AiTaskResults.PathFor(_job, "orientation-stages");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_summary)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private static int Index(string id)
    {
        for (var i = 0; i < OrientationStageIds.All.Count; i++) if (OrientationStageIds.All[i].Id == id) return i;
        throw new ArgumentException("未知方向检测阶段：" + id, nameof(id));
    }
}
