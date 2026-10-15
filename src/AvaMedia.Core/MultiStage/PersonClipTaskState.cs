using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

internal sealed record PersonClipStageRecord(TaskStageStatus Status, DateTime? StartedUtc = null, DateTime? CompletedUtc = null);
internal sealed record PersonClipStageSummary(PersonClipStageRecord[] Stages, DateTime? StartedUtc, DateTime? CompletedUtc,
    TaskRuntimeInfo Runtime, MediaInfo? Info, long SourceLength, DateTime SourceWriteUtc, string SourcePath, string OptionsKey,
    Dictionary<string, string> ModelStamps);
internal sealed record PersonClipCandidate(int Start, int End);
internal sealed record PersonClipDetectionCheckpoint(PersonClipResult Result, PersonFrame[] Samples, PersonClipCandidate[] Candidates);

public sealed partial class Job
{
    private PersonClipTaskState? _personClipStages;
    [JsonIgnore] internal PersonClipTaskState? PersonClipStages
    { get => Volatile.Read(ref _personClipStages); set { Volatile.Write(ref _personClipStages, value); NotifyPersonClipStages(); } }
    // Persisted in the queue so an admitted retry keeps its requested entry point across a restart.
    private string? _personClipStartStageId;
    public string? PersonClipStartStageId { get => _personClipStartStageId; set => Set(ref _personClipStartStageId, value); }
    public void PersonClipStagesReset()
    {
        PersonClipStartStageId = null;
        PersonClipStages = null;
    }
    internal void NotifyPersonClipStages() => Raise(nameof(PersonClipStages));
}

/// <summary>One execution's observed stages. Independent of window/session lifetime.</summary>
internal sealed class PersonClipTaskState
{
    private readonly object _gate = new();
    private readonly Job _job;
    private readonly TimeProvider _time;
    private PersonClipStageSummary _summary;
    private long _tick;
    private int _active = -1;
    private bool _paused;
    private PersonClipLiveFrame? _frame;
    private bool _open;
    private long _lastProgressLog;
    private JobState? _finishedState;
    internal PersonClipTaskState(Job job, TimeProvider? time = null, PersonClipStageSummary? summary = null)
    {
        _job = job; _time = time ?? TimeProvider.System;
        _summary = summary ?? new(Plans(job).Select(status => new PersonClipStageRecord(status)).ToArray(), null, null,
            new(Settings(job).ModelName), null, 0, default, job.Inputs.FirstOrDefault() ?? "", OptionsKey(job), []);
    }
    internal static string OptionsKey(Job job) => JsonSerializer.Serialize(job.Options.PersonClip?.Detection);
    internal static PersonClipSettingsSummary Settings(Job job)
    {
        var spec = job.Options.PersonClip ?? new(); var options = spec.Detection;
        return new()
        {
            ModelName = string.Join(" + ", options.RequiredModels.Select(id => PersonDetectorCatalog.Find(id).Name)) + (options.UseEmbedding ? " + Gemma" : ""),
            ModeLabel = options.DetectionMode switch { PersonDetectionMode.Recall => "优先召回", PersonDetectionMode.Consensus => "交叉确认", _ => "平衡检测" },
            FramesPerSecond = options.FramesPerSecond, Threshold = options.Threshold, PaddingSeconds = options.PaddingSeconds,
            MergeGapSeconds = options.MergeGapSeconds, MinimumSeconds = options.MinimumSeconds, SkipDarkFrames = options.SkipDarkFrames,
            SkipBlankFrames = options.SkipBlankFrames, IncludeHands = options.IncludeHands, AnalysisOnly = spec.AnalysisOnly,
            ExportLabel = spec.AnalysisOnly ? "仅检测，不导出" : job.Options.Format.ToUpperInvariant() + " · 拼接为单个文件"
        };
    }
    internal static TaskStageStatus[] Plans(Job job)
    {
        var s = Settings(job);
        string[] details = ["探测时长、编码与音视频流", s.ModelName,
            $"每秒 {MediaEngine.Number(s.FramesPerSecond)} 帧 · {s.ModeLabel}",
            $"前后保留 {MediaEngine.Number(s.PaddingSeconds)} 秒 · 合并间隔 {MediaEngine.Number(s.MergeGapSeconds)} 秒 · 最短 {MediaEngine.Number(s.MinimumSeconds)} 秒", s.ExportLabel];
        return PersonClipStageIds.All.Select((stage, i) => new TaskStageStatus(stage, StageState.Pending) { Detail = details[i] }).ToArray();
    }
    internal static int? PlannedFrames(MediaInfo info, PersonClipOptions options)
    {
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) return null;
        var count = PersonClipExclusions.Subtract([new(0, info.Duration)], options.ExcludedRanges)
            .Sum(range => Math.Ceiling((range.End - range.Start) * options.FramesPerSecond));
        return double.IsFinite(count) && count <= int.MaxValue ? (int)count : null;
    }
    internal (PersonClipStageSummary Summary, PersonClipLiveFrame? Frame, bool Open) Capture()
    {
        lock (_gate)
        {
            var stages = _summary.Stages.ToArray();
            if (_active >= 0 && !_paused)
            {
                var item = stages[_active];
                stages[_active] = item with { Status = item.Status with
                { Elapsed = (item.Status.Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = true } };
            }
            return (_summary with { Stages = stages }, _frame, _open);
        }
    }
    internal void Source(MediaInfo info, long bytes, DateTime modified)
    { lock (_gate) _summary = _summary with { Info = info, SourceLength = bytes, SourceWriteUtc = modified }; }
    internal void Backend(string backend, string note)
    {
        lock (_gate) _summary = _summary with { Runtime = _summary.Runtime with { Backend = backend, Note = note } };
    }
    internal void Model(ModelStore store, string backend)
    {
        lock (_gate) _summary = _summary with { Runtime = new(Settings(_job).ModelName, backend),
            ModelStamps = RequiredModels(_job).ToDictionary(id => id, store.InstalledStamp) };
    }
    internal static IEnumerable<string> RequiredModels(Job job) => job.Options.PersonClip!.Detection.RequiredModels
        .Concat(job.Options.PersonClip.Detection.UseEmbedding ? [ModelCatalog.EmbeddingId] : []);
    internal void Start(string id)
    {
        lock (_gate)
        {
            var index = Index(id);
            if (_active == index) return;
            if (_active >= 0) throw new InvalidOperationException("Previous person-clip stage is still active.");
            _finishedState = null; _lastProgressLog = _time.GetTimestamp();
            _active = index; _tick = _time.GetTimestamp(); _paused = _job.State == JobState.Paused;
            var utc = _time.GetUtcNow().UtcDateTime;
            _summary = _summary with { StartedUtc = _summary.StartedUtc ?? utc, CompletedUtc = null };
            var item = _summary.Stages[index];
            _summary.Stages[index] = item with { StartedUtc = utc, CompletedUtc = null,
                Status = item.Status with { State = _paused ? StageState.Paused : StageState.Active, Elapsed = TimeSpan.Zero, IsClockRunning = !_paused } };
            LogLocked(id, "开始 · " + item.Status.Detail);
            SaveLocked();
        }
        _job.NotifyPersonClipStages();
    }
    internal void Update(double? current, double? total, string unit, string detail)
    {
        lock (_gate)
        {
            if (_active < 0) return;
            var item = _summary.Stages[_active];
            _summary.Stages[_active] = item with { Status = item.Status with { Current = current, Total = total, Unit = unit,
                Fraction = total > 0 && current is { } count ? Math.Clamp(count / total.Value, 0, 1) : null, Detail = detail } };
            if (_time.GetElapsedTime(_lastProgressLog) >= TimeSpan.FromSeconds(2))
            {
                LogLocked(item.Status.Stage.Id, detail); AccumulateLocked(); SaveLocked(); _lastProgressLog = _time.GetTimestamp();
            }
        }
        _job.NotifyPersonClipStages();
    }
    internal void Frame(PersonClipLiveFrame frame) { lock (_gate) _frame = frame; _job.NotifyPersonClipStages(); }
    internal void Open(bool open) { lock (_gate) _open = open; }
    internal void Complete(string id, string detail, StageState state = StageState.Done)
    {
        lock (_gate)
        {
            var index = Index(id); AccumulateLocked();
            var item = _summary.Stages[index];
            _summary.Stages[index] = item with { CompletedUtc = _time.GetUtcNow().UtcDateTime,
                Status = item.Status with { State = state, Detail = detail, IsClockRunning = false, Fraction = null } };
            if (_active == index) _active = -1;
            LogLocked(id, detail); SaveLocked();
        }
        _job.NotifyPersonClipStages();
    }
    internal void Reuse(string id, PersonClipStageSummary previous)
    {
        lock (_gate)
        {
            var index = Index(id); var old = previous.Stages[index];
            _summary.Stages[index] = old with { Status = old.Status with
            { State = StageState.Done, IsClockRunning = false, Detail = old.Status.Detail.StartsWith("复用上次结果 · ", StringComparison.Ordinal) ? old.Status.Detail : "复用上次结果 · " + old.Status.Detail } };
            _summary = _summary with { Info = previous.Info, SourceLength = previous.SourceLength, SourceWriteUtc = previous.SourceWriteUtc,
                Runtime = previous.Runtime, ModelStamps = previous.ModelStamps };
            LogLocked(id, _summary.Stages[index].Status.Detail); SaveLocked();
        }
        _job.NotifyPersonClipStages();
    }
    internal void Pause(bool paused)
    {
        lock (_gate)
        {
            if (_active < 0 || _paused == paused) return;
            AccumulateLocked(); _paused = paused; _tick = _time.GetTimestamp();
            var item = _summary.Stages[_active];
            _summary.Stages[_active] = item with { Status = item.Status with
            { State = paused ? StageState.Paused : StageState.Active, IsClockRunning = !paused } };
            LogLocked(item.Status.Stage.Id, paused ? "已暂停" : "继续执行"); SaveLocked();
        }
        _job.NotifyPersonClipStages();
    }
    internal void Finish(JobState state, string error = "")
    {
        lock (_gate)
        {
            if (_finishedState == state) return;
            _finishedState = state;
            AccumulateLocked();
            if (_active >= 0)
            {
                var item = _summary.Stages[_active]; var id = item.Status.Stage.Id;
                var cancelled = state == JobState.Cancelled;
                var detail = cancelled ? "已取消" : error;
                if (item.Status.Current is { } current)
                    detail += $" · {MediaEngine.Number(current)}" + (item.Status.Total is { } total ? " / " + MediaEngine.Number(total) : "") + " " + item.Status.Unit;
                _summary.Stages[_active] = item with { CompletedUtc = _time.GetUtcNow().UtcDateTime,
                    Status = item.Status with { State = cancelled ? StageState.Cancelled : StageState.Failed, Detail = detail, IsClockRunning = false } };
                LogLocked(id, detail, TaskLogLevel.Marker);
                if (!cancelled) LogLocked(id, error, TaskLogLevel.Error);
                _active = -1;
            }
            else if (state is JobState.Cancelled or JobState.Failed)
                LogLocked("", state == JobState.Cancelled ? "已取消 · 当前没有执行中的阶段" : error, TaskLogLevel.Marker);
            _open = false;
            _summary = _summary with { CompletedUtc = _time.GetUtcNow().UtcDateTime };
            SaveLocked();
        }
        _job.NotifyPersonClipStages();
    }
    internal void Log(string id, string message, TaskLogLevel level = TaskLogLevel.Info)
    { lock (_gate) LogLocked(id, message, level); }
    private void LogLocked(string id, string message, TaskLogLevel level = TaskLogLevel.Info)
    {
        var title = PersonClipStageIds.All.FirstOrDefault(stage => stage.Id == id)?.Title ?? "";
        var entry = new TaskLogEntry(_time.GetUtcNow().UtcDateTime, id, title, message, level);
        var path = AiTaskResults.PathFor(_job, "people-log", "jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
        _job.AppendLog($"[{entry.TimeUtc:O}] [{title}] {message}");
    }
    private void AccumulateLocked()
    {
        if (_active < 0 || _paused) return;
        var item = _summary.Stages[_active];
        _summary.Stages[_active] = item with { Status = item.Status with
        { Elapsed = (item.Status.Elapsed ?? TimeSpan.Zero) + _time.GetElapsedTime(_tick), IsClockRunning = false } };
        _tick = _time.GetTimestamp();
    }
    private void SaveLocked()
    {
        // Stage boundaries and pause transitions are small, atomic durable checkpoints.
        var path = AiTaskResults.PathFor(_job, "people-stages");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_summary)); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private static int Index(string id)
    {
        for (var index = 0; index < PersonClipStageIds.All.Count; index++)
            if (PersonClipStageIds.All[index].Id == id) return index;
        throw new ArgumentException("Unknown person-clip stage: " + id, nameof(id));
    }
}
