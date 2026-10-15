using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AvaMedia.VideoSummaryTests")]

namespace AvaMedia.Core;

public sealed partial class Job
{
    private VideoSummaryTaskState? _videoSummaryStages;
    [JsonIgnore] internal VideoSummaryTaskState? VideoSummaryStages
    { get => Volatile.Read(ref _videoSummaryStages); set => Volatile.Write(ref _videoSummaryStages, value); }
    /// <summary>Host sets this before requeuing a stage retry. Null requests a complete execution.</summary>
    public string? VideoSummaryStartStageId { get; set; }
    public void VideoSummaryStagesReset() { VideoSummaryStartStageId = null; VideoSummaryStages = null; }
}

internal sealed record VideoSummaryStageSummary(TaskStageStatus[] Stages, DateTime? StartedUtc = null,
    DateTime? CompletedUtc = null, TaskRuntimeInfo? Runtime = null, string SourceKey = "", string EnvironmentKey = "",
    string SignalHash = "", bool IsLong = false);

/// <summary>Execution-owned observations, persisted independently of any session.</summary>
internal sealed class VideoSummaryTaskState : IDisposable
{
    private readonly object _gate = new();
    private readonly Job _job;
    private readonly TimeProvider _time;
    private VideoSummaryStageSummary _summary = new(Plans());
    private int _active = -1, _retryIndex;
    private long _tick;
    private bool _paused;
    private bool _finished;
    internal VideoSummaryTaskState(Job job, TimeProvider? time = null)
    { _job = job; _time = time ?? TimeProvider.System; job.PropertyChanged += OnJobChanged; }
    internal static string StatePath(Job job) => AiTaskResults.PathFor(job, "video-summary-stages");
    internal static string LogPath(Job job) => AiTaskResults.PathFor(job, "video-summary-log", "jsonl");
    internal static TaskStageStatus[] Plans() => VideoSummaryStageIds.All.Select(s => new TaskStageStatus(s, StageState.Pending)).ToArray();
    internal static int Index(string id)
    {
        for (int i = 0; i < VideoSummaryStageIds.All.Count; i++) if (VideoSummaryStageIds.All[i].Id == id) return i;
        throw new ArgumentException("未知视频总结阶段。", nameof(id));
    }
    internal VideoSummaryStageSummary Capture()
    {
        lock (_gate)
        {
            var stages = _summary.Stages.ToArray();
            if (_active >= 0 && !_paused) stages[_active] = stages[_active] with
            { Elapsed = (stages[_active].Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = true };
            return _summary with { Stages = stages };
        }
    }
    internal void Configure(bool isLong, string sourceKey, string environmentKey)
    {
        lock (_gate) _summary = _summary with { IsLong = isLong, SourceKey = sourceKey, EnvironmentKey = environmentKey };
        if (!isLong)
            foreach (var id in new[] { VideoSummaryStageIds.PlanSegments, VideoSummaryStageIds.UnderstandSegments, VideoSummaryStageIds.ReduceChapters, VideoSummaryStageIds.VerifyChapters })
                Complete(id, "短视频流程无需此阶段", StageState.Skipped);
        if (!_job.Options.VideoSummary!.NeedsAi || !_job.Options.VideoSummary.AnalyzeFrames)
            Complete(VideoSummaryStageIds.VisualAnalysis, "未启用画面分析", StageState.Skipped);
        if (!_job.Options.VideoSummary.NeedsAi) Complete(VideoSummaryStageIds.Summarize, "仅提取字幕", StageState.Skipped);
    }
    internal void Retain(VideoSummaryStageSummary previous, string stageId)
    {
        lock (_gate)
        {
            _retryIndex = Index(stageId);
            // Read-video actually ran again to verify the source and parameters.
            for (int i = 1; i < _retryIndex; i++) _summary.Stages[i] = previous.Stages[i] with
            { IsClockRunning = false, Detail = "复用检查点 · " + previous.Stages[i].Detail };
            _summary = _summary with { SignalHash = previous.SignalHash };
            SaveLocked();
        }
    }
    internal bool IsRetry { get { lock (_gate) return _retryIndex > 0; } }
    internal void DropRetention()
    {
        lock (_gate)
        {
            if (_retryIndex == 0) return;
            _retryIndex = 0;
            for (int i = 1; i < _summary.Stages.Length; i++)
                if (_summary.Stages[i].State != StageState.Skipped) _summary.Stages[i] = new(VideoSummaryStageIds.All[i], StageState.Pending);
            _job.AppendLog("信号检查点已失效，重新提取字幕与画面。");
            SaveLocked();
        }
    }
    internal void SignalHash(string hash) { lock (_gate) { _summary = _summary with { SignalHash = hash }; SaveLocked(); } }
    internal void Runtime(string model, string backend) { lock (_gate) _summary = _summary with { Runtime = new(model, backend) }; }
    internal void Start(string id)
    {
        lock (_gate)
        {
            var index = Index(id);
            if (index < _retryIndex || _active == index) return;
            if (_active >= 0) throw new InvalidOperationException("上一个视频总结阶段尚未结束。");
            _active = index; _tick = _time.GetTimestamp(); _paused = _job.State == JobState.Paused;
            _summary = _summary with { StartedUtc = _summary.StartedUtc ?? _time.GetUtcNow().UtcDateTime };
            _summary.Stages[index] = new(VideoSummaryStageIds.All[index], _paused ? StageState.Paused : StageState.Active)
            { Elapsed = TimeSpan.Zero, IsClockRunning = !_paused };
            LogLocked(id, "开始"); SaveLocked();
        }
    }
    internal void Update(string id, double? current, double? total, string unit, string detail)
    {
        lock (_gate)
        {
            var index = Index(id); if (_active != index) return;
            _summary.Stages[index] = _summary.Stages[index] with { Current = current, Total = total, Unit = unit, Detail = detail,
                Fraction = total > 0 && current is { } count ? Math.Clamp(count / total.Value, 0, 1) : null };
        }
    }
    internal void Complete(string id, string detail, StageState state = StageState.Done)
    {
        lock (_gate)
        {
            int index = Index(id); if (index < _retryIndex) return;
            AccumulateLocked();
            _summary.Stages[index] = _summary.Stages[index] with { State = state, Detail = detail, IsClockRunning = false, Fraction = null };
            if (_active == index) _active = -1;
            LogLocked(id, detail); SaveLocked();
        }
    }
    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Job.State)) return;
        lock (_gate)
        {
            if (_active < 0 || _paused == (_job.State == JobState.Paused)) return;
            AccumulateLocked(); _paused = _job.State == JobState.Paused; _tick = _time.GetTimestamp();
            _summary.Stages[_active] = _summary.Stages[_active] with
            { State = _paused ? StageState.Paused : StageState.Active, IsClockRunning = !_paused };
            LogLocked(_summary.Stages[_active].Stage.Id, _paused ? "已暂停" : "继续执行"); SaveLocked();
        }
    }
    internal void Finish(Exception? error = null)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true; AccumulateLocked();
            if (_active >= 0 && error is not null)
            {
                bool cancelled = error is OperationCanceledException;
                var stage = _summary.Stages[_active];
                _summary.Stages[_active] = stage with { State = cancelled ? StageState.Cancelled : StageState.Failed,
                    Detail = cancelled ? "已取消" : error.Message, IsClockRunning = false };
                LogLocked(stage.Stage.Id, cancelled ? "已取消" : error.Message, cancelled ? TaskLogLevel.Marker : TaskLogLevel.Error);
            }
            else if (error is not null)
                LogLocked("", error is OperationCanceledException ? "已取消" : error.Message,
                    error is OperationCanceledException ? TaskLogLevel.Marker : TaskLogLevel.Error);
            _active = -1; _summary = _summary with { CompletedUtc = _time.GetUtcNow().UtcDateTime }; SaveLocked();
        }
    }
    private void AccumulateLocked()
    {
        if (_active < 0 || _paused) return;
        _summary.Stages[_active] = _summary.Stages[_active] with
        { Elapsed = (_summary.Stages[_active].Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = false };
        _tick = _time.GetTimestamp();
    }
    private void SaveLocked()
    {
        var path = StatePath(_job); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(Capture())); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private void LogLocked(string id, string message, TaskLogLevel level = TaskLogLevel.Info)
    {
        var entry = new TaskLogEntry(_time.GetUtcNow().UtcDateTime, id, id.Length == 0 ? "" : VideoSummaryStageIds.All[Index(id)].Title, message, level);
        var path = LogPath(_job); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }
    public void Dispose() => _job.PropertyChanged -= OnJobChanged;
}
