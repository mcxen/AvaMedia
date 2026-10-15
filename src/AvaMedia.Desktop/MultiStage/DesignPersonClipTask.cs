using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>
/// Design-time/capture-only stand-in for <see cref="IPersonClipTask"/>. It renders a fixed state of the
/// 保留有人片段 window from illustrative data so the shell can be reviewed without running detection.
/// Never wired to real jobs.
/// </summary>
internal sealed class DesignPersonClipTask : IPersonClipTask
{
    public enum Scenario { Configuring, Running, Paused, Failed, Completed, Cancelled }

    // Illustrative kept ranges as fractions of the video, in the rhythm of the review demo.
    private static readonly (double Start, double End)[] Pattern =
    [
        (.000, .158), (.163, .175), (.181, .198), (.214, .226), (.239, .255), (.273, .282), (.302, .318), (.337, .349),
        (.372, .404), (.416, .432), (.446, .463), (.488, .502), (.522, .542), (.560, .582), (.600, .611), (.629, .645),
        (.662, .680), (.701, .722), (.742, .758), (.778, .797), (.818, .861)
    ];

    private readonly Scenario _scenario;
    private readonly DateTime _now = DateTime.UtcNow;

    public DesignPersonClipTask(Scenario scenario, string source, MediaInfo info, byte[]? frame, string outputFolder)
    {
        _scenario = scenario;
        var duration = info.Duration;
        var fraction = scenario switch { Scenario.Running or Scenario.Paused => .62, Scenario.Failed => .406, Scenario.Cancelled => .455, Scenario.Completed => 1, _ => 0 };
        var analyzed = duration * fraction;
        var segments = Pattern.Select((range, index) => new PersonClipKeptSegment(index + 1, Round(range.Start * duration), Round(range.End * duration)))
            .Where(segment => segment.Start < analyzed || scenario == Scenario.Completed)
            .Select(segment => segment.End > analyzed && scenario != Scenario.Completed ? segment with { End = Round(analyzed), IsOpen = scenario is Scenario.Running or Scenario.Paused } : segment)
            .ToArray();
        var planned = (int)Math.Round(duration * 1.5);
        var output = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(source) + ".mp4");
        View = new PersonClipTaskView
        {
            SourcePath = source, Info = info, AnalyzedSeconds = analyzed, SampledFrames = (int)Math.Round(planned * fraction), PlannedFrames = planned,
            Segments = scenario == Scenario.Configuring ? [] : segments, IsPartial = scenario != Scenario.Completed, HasResult = scenario != Scenario.Configuring,
            KeptSeconds = scenario == Scenario.Configuring ? 0 : segments.Sum(segment => segment.Duration),
            LiveFrame = frame is null || scenario is Scenario.Configuring or Scenario.Completed ? null : new PersonClipLiveFrame(analyzed, (int)Math.Round(planned * fraction), frame)
            {
                PersonCount = scenario is Scenario.Running or Scenario.Paused ? 2 : 0,
                Boxes = scenario is Scenario.Running or Scenario.Paused ? new PersonClipDetectionBox[] { new(.21, .18, .25, .62, .91), new(.52, .2, .24, .6, .84) } : Array.Empty<PersonClipDetectionBox>()
            },
            Settings = new PersonClipSettingsSummary
            {
                ModelName = "NanoDet", ModelInstalled = true, ModeLabel = "平衡检测", FramesPerSecond = 1.5, Threshold = .35, PaddingSeconds = .5,
                MergeGapSeconds = 1, MinimumSeconds = .5, SkipDarkFrames = true, ExportLabel = "MP4 · 拼接为单个文件"
            },
            OutputFolder = outputFolder, OutputPath = output, OutputExists = scenario == Scenario.Completed, OutputBytes = scenario == Scenario.Completed ? 168L << 20 : null,
            CanAdjust = scenario == Scenario.Completed
        };
        Snapshot = BuildSnapshot(info, analyzed, planned);
    }

    public string FeatureId => "person-clip";
    public IReadOnlyList<TaskStageDescriptor> Stages => PersonClipStageIds.All;
    public MultiStageTaskSnapshot Snapshot { get; }
    public PersonClipTaskView View { get; }
    public event EventHandler? Changed { add { } remove { } }

    private MultiStageTaskSnapshot BuildSnapshot(MediaInfo info, double analyzed, int planned)
    {
        var duration = PersonTaskView.Time(info.Duration, 1);
        var probe = $"{info.Width}×{info.Height} · H.264 · {duration}";
        var stages = PersonClipStageIds.All;
        var sampled = View.SampledFrames;
        TaskStageStatus S(int index, StageState state, string detail, double? seconds = null, double? fraction = null, bool clock = false) =>
            new(stages[index], state) { Detail = detail, Elapsed = seconds is { } s ? TimeSpan.FromSeconds(s) : null, Fraction = fraction, IsClockRunning = clock };
        var runtime = new TaskRuntimeInfo("NanoDet", "CPU", "未检测到可用 GPU 执行器");
        var log = new TaskCommands();
        return _scenario switch
        {
            Scenario.Configuring => new MultiStageTaskSnapshot
            {
                State = MultiStageTaskState.Configuring, CurrentStageIndex = -1,
                Stages = [S(0, StageState.Pending, "探测时长、编码与音视频流"), S(1, StageState.Pending, "NanoDet · 已下载"), S(2, StageState.Pending, "每秒 1.5 帧 · 平衡检测"),
                    S(3, StageState.Pending, "前后保留 0.5 秒 · 合并间隔 1 秒"), S(4, StageState.Pending, "MP4 · 拼接为单个文件")],
                Commands = new TaskCommands { CanStart = true, CanChangeOutput = true }, Runtime = new TaskRuntimeInfo("NanoDet")
            },
            Scenario.Running or Scenario.Paused => new MultiStageTaskSnapshot
            {
                State = _scenario == Scenario.Running ? MultiStageTaskState.Running : MultiStageTaskState.Paused, CurrentStageIndex = 2, Progress = 62,
                Elapsed = TimeSpan.FromSeconds(12), IsClockRunning = _scenario == Scenario.Running, SnapshotUtc = _now, Remaining = TimeSpan.FromSeconds(8), StartedUtc = _now.AddSeconds(-12),
                Stages = [S(0, StageState.Done, probe, .4), S(1, StageState.Skipped, "已跳过 · 模型已在内存中"),
                    S(2, _scenario == Scenario.Running ? StageState.Active : StageState.Paused, $"{sampled} / {planned} 帧 · 62%", 11, .62, _scenario == Scenario.Running),
                    S(3, StageState.Pending, "等待"), S(4, StageState.Pending, "等待")],
                Commands = _scenario == Scenario.Running ? new TaskCommands { CanPause = true, CanStop = true } : new TaskCommands { CanResume = true, CanStop = true }, Runtime = runtime
            },
            Scenario.Failed => new MultiStageTaskSnapshot
            {
                State = MultiStageTaskState.Failed, CurrentStageIndex = 2, Progress = 41, Elapsed = TimeSpan.FromSeconds(9), SnapshotUtc = _now, StartedUtc = _now.AddSeconds(-9),
                Error = "无法解码视频帧（数据损坏或编码不受支持）。", InterruptedStageId = PersonClipStageIds.Detect,
                RetainedStageIds = [PersonClipStageIds.ReadVideo, PersonClipStageIds.PrepareModel],
                Stages = [S(0, StageState.Done, probe, .4), S(1, StageState.Done, "NanoDet · CPU", 1.8),
                    S(2, StageState.Failed, $"在 {PersonTaskView.Time(analyzed, 1)} 处中断 · {sampled} / {planned} 帧", 7), S(3, StageState.Pending, "未开始"), S(4, StageState.Pending, "未开始")],
                Commands = new TaskCommands { CanRerun = true, RetryStageId = PersonClipStageIds.Detect }, Runtime = runtime
            },
            Scenario.Cancelled => new MultiStageTaskSnapshot
            {
                State = MultiStageTaskState.Cancelled, CurrentStageIndex = 2, Progress = 45, Elapsed = TimeSpan.FromSeconds(10), SnapshotUtc = _now, StartedUtc = _now.AddSeconds(-10),
                InterruptedStageId = PersonClipStageIds.Detect, RetainedStageIds = [PersonClipStageIds.ReadVideo, PersonClipStageIds.PrepareModel],
                Stages = [S(0, StageState.Done, probe, .4), S(1, StageState.Done, "NanoDet · CPU", 1.8),
                    S(2, StageState.Cancelled, $"已取消 · {sampled} / {planned} 帧", 8), S(3, StageState.Pending, "未开始"), S(4, StageState.Pending, "未开始")],
                Commands = new TaskCommands { CanRerun = true }, Runtime = runtime
            },
            _ => new MultiStageTaskSnapshot
            {
                State = MultiStageTaskState.Completed, CurrentStageIndex = 4, Progress = 100, Elapsed = TimeSpan.FromSeconds(24.1), SnapshotUtc = _now,
                StartedUtc = _now.AddSeconds(-24.1), CompletedUtc = _now,
                Stages = [S(0, StageState.Done, probe, .4), S(1, StageState.Done, "NanoDet · CPU", 1.8),
                    S(2, StageState.Done, $"{planned} 帧 · {View.Segments.Count} 段", 16.2), S(3, StageState.Done, $"{View.Segments.Count * 4} 处边界", 1.6),
                    S(4, StageState.Done, $"{PersonTaskView.Time(View.KeptSeconds, 1)} · 168 MB", 4.1)],
                Commands = new TaskCommands { CanRerun = true }, Runtime = runtime
            }
        };
    }

    public Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        var start = _now.AddSeconds(-10);
        var info = View.Info!;
        var planned = View.PlannedFrames ?? 0;
        var entries = new List<TaskLogEntry>
        {
            new(start, PersonClipStageIds.ReadVideo, "读取视频", $"{info.Width}×{info.Height} · H.264 · {info.FrameRate:0.##} fps · {PersonTaskView.Time(info.Duration, 1)}"),
            new(start, PersonClipStageIds.PrepareModel, "准备检测模型", "NanoDet · CPU（未检测到可用 GPU 执行器）"),
            new(start.AddSeconds(2), PersonClipStageIds.Detect, "人物检测", "开始扫描 · 每秒 1.5 帧 · 阈值 0.35")
        };
        if (_scenario == Scenario.Configuring) entries.Clear();
        else
        {
            for (var i = 1; i <= 3; i++)
            {
                var frames = Math.Min(View.SampledFrames, View.SampledFrames * i / 3);
                entries.Add(new(start.AddSeconds(2 + i * 2), PersonClipStageIds.Detect, "人物检测", $"已检测 {frames} / {planned} 帧 · 保留 {Math.Max(1, View.Segments.Count * i / 3)} 段"));
            }
            if (_scenario == Scenario.Cancelled)
            {
                entries.Add(new(start.AddSeconds(10), "", "已取消", $"手动停止任务；保留 {PersonTaskView.Time(View.AnalyzedSeconds, 1)} 之前的部分结果", TaskLogLevel.Marker));
                entries.Add(new(start.AddSeconds(10), PersonClipStageIds.Refine, "细化片段边界", "未开始"));
                entries.Add(new(start.AddSeconds(10), PersonClipStageIds.Export, "导出片段", "未开始"));
            }
            if (_scenario == Scenario.Failed)
                entries.Add(new(start.AddSeconds(9), PersonClipStageIds.Detect, "人物检测", $"在 {PersonTaskView.Time(View.AnalyzedSeconds, 1)} 处中断：无法解码视频帧", TaskLogLevel.Error));
        }
        return Task.FromResult<IReadOnlyList<TaskLogEntry>>(entries);
    }

    public Task StartAsync() => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task RerunAsync() => Task.CompletedTask;
    public Task RetryFromStageAsync(string stageId) => Task.CompletedTask;
    public Task ChangeOutputFolderAsync(string folder) => Task.CompletedTask;
    public void Dispose() { }
    private static double Round(double value) => Math.Round(value * 8) / 8;
}
