using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Fixture states shared by the design tool tasks.</summary>
internal enum DesignStage { Configuring, Running, Paused, Failed, Completed, Cancelled }

/// <summary>
/// Design-time/capture-only multi-stage task: builds a fixed snapshot from per-stage details so each tool's window
/// can be reviewed in every state without running models. Never wired to real jobs.
/// </summary>
internal abstract class DesignStageTask : IMultiStageTask
{
    protected readonly DesignStage Scenario;
    protected readonly DateTime Now = DateTime.UtcNow;
    private MultiStageTaskSnapshot? _snapshot;

    protected DesignStageTask(DesignStage scenario) => Scenario = scenario;
    public abstract string FeatureId { get; }
    public abstract IReadOnlyList<TaskStageDescriptor> Stages { get; }
    /// <summary>Stage index reached when running / failed / cancelled.</summary>
    protected abstract int ActiveStage { get; }
    protected abstract TaskRuntimeInfo Runtime { get; }
    protected virtual string Error => "处理失败。";
    protected virtual double Progress => 55;
    protected virtual double TotalSeconds => 42.6;
    protected virtual string? RetryStageId => null;
    protected virtual bool CanChangeOutput => true;
    /// <summary>Detail for stage <paramref name="index"/> in the given state.</summary>
    protected abstract string Detail(int index, StageState state);
    protected virtual StageState CompletedState(int index) => StageState.Done;
    protected virtual double StageSeconds(int index) => TotalSeconds / Stages.Count;

    public MultiStageTaskSnapshot Snapshot => _snapshot ??= Build();
    public event EventHandler? Changed { add { } remove { } }

    private MultiStageTaskSnapshot Build()
    {
        var active = ActiveStage;
        TaskStageStatus S(int index, StageState state, bool clock = false, double? fraction = null) =>
            new(Stages[index], state)
            {
                Detail = Detail(index, state), IsClockRunning = clock, Fraction = fraction,
                Elapsed = state is StageState.Done or StageState.Failed or StageState.Cancelled or StageState.Active or StageState.Paused ? TimeSpan.FromSeconds(StageSeconds(index)) : null
            };
        var runtime = Runtime;
        var elapsed = TimeSpan.FromSeconds(Scenario == DesignStage.Completed ? TotalSeconds : TotalSeconds * Progress / 100);
        switch (Scenario)
        {
            case DesignStage.Configuring:
                return new MultiStageTaskSnapshot
                {
                    State = MultiStageTaskState.Configuring, CurrentStageIndex = -1,
                    Stages = Stages.Select((_, i) => S(i, StageState.Pending)).ToArray(),
                    Commands = new TaskCommands { CanStart = true, CanChangeOutput = CanChangeOutput }, Runtime = new TaskRuntimeInfo(runtime.Model)
                };
            case DesignStage.Running or DesignStage.Paused:
                var running = Scenario == DesignStage.Running;
                return new MultiStageTaskSnapshot
                {
                    State = running ? MultiStageTaskState.Running : MultiStageTaskState.Paused, CurrentStageIndex = active, Progress = Progress,
                    Elapsed = elapsed, IsClockRunning = running, SnapshotUtc = Now, StartedUtc = Now - elapsed, Remaining = TimeSpan.FromSeconds(TotalSeconds) - elapsed,
                    Stages = Stages.Select((_, i) => i < active ? S(i, CompletedState(i)) : i == active ? S(i, running ? StageState.Active : StageState.Paused, running, .55) : S(i, StageState.Pending)).ToArray(),
                    Commands = running ? new TaskCommands { CanPause = true, CanStop = true } : new TaskCommands { CanResume = true, CanStop = true }, Runtime = runtime
                };
            case DesignStage.Failed or DesignStage.Cancelled:
                var failed = Scenario == DesignStage.Failed;
                return new MultiStageTaskSnapshot
                {
                    State = failed ? MultiStageTaskState.Failed : MultiStageTaskState.Cancelled, CurrentStageIndex = active, Progress = Progress,
                    Elapsed = elapsed, SnapshotUtc = Now, StartedUtc = Now - elapsed, Error = failed ? Error : null, InterruptedStageId = Stages[active].Id,
                    RetainedStageIds = Stages.Take(active).Where((_, i) => CompletedState(i) == StageState.Done).Select(stage => stage.Id).ToArray(),
                    Stages = Stages.Select((_, i) => i < active ? S(i, CompletedState(i)) : i == active ? S(i, failed ? StageState.Failed : StageState.Cancelled) : S(i, StageState.Pending)).ToArray(),
                    Commands = new TaskCommands { CanRerun = true, RetryStageId = failed ? RetryStageId : null }, Runtime = runtime
                };
            default:
                return new MultiStageTaskSnapshot
                {
                    State = MultiStageTaskState.Completed, CurrentStageIndex = Stages.Count - 1, Progress = 100, Elapsed = elapsed, SnapshotUtc = Now,
                    StartedUtc = Now - elapsed, CompletedUtc = Now,
                    Stages = Stages.Select((_, i) => S(i, CompletedState(i))).ToArray(),
                    Commands = new TaskCommands { CanRerun = true }, Runtime = runtime
                };
        }
    }

    public Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default)
    {
        var entries = new List<TaskLogEntry>();
        if (Scenario == DesignStage.Configuring) return Task.FromResult<IReadOnlyList<TaskLogEntry>>(entries);
        var snapshot = Snapshot;
        var time = Now - snapshot.Elapsed;
        foreach (var stage in snapshot.Stages)
        {
            if (stage.State == StageState.Pending) continue;
            entries.Add(new(time, stage.Stage.Id, stage.Stage.Title, stage.Detail ?? "",
                stage.State == StageState.Failed ? TaskLogLevel.Error : TaskLogLevel.Info));
            time += stage.Elapsed ?? TimeSpan.Zero;
        }
        if (Scenario == DesignStage.Failed) entries.Add(new(time, Stages[ActiveStage].Id, Stages[ActiveStage].Title, Error, TaskLogLevel.Error));
        if (Scenario == DesignStage.Cancelled) entries.Add(new(time, "", "已取消", "手动停止任务；已完成阶段的结果保留", TaskLogLevel.Marker));
        return Task.FromResult<IReadOnlyList<TaskLogEntry>>(entries);
    }

    protected string Pick(StageState state, string done, string active = "", string pending = "等待") => state switch
    {
        StageState.Pending => Scenario == DesignStage.Configuring ? "" : Scenario is DesignStage.Failed or DesignStage.Cancelled ? "未开始" : pending,
        StageState.Active or StageState.Paused => active.Length > 0 ? active : done,
        StageState.Cancelled => "已取消" + (active.Length > 0 ? " · " + active : ""),
        StageState.Failed => active.Length > 0 ? active : "失败",
        StageState.Skipped => "已跳过",
        _ => done
    };
    protected bool IsLive => Scenario is DesignStage.Running or DesignStage.Paused;
    protected bool IsDone => Scenario == DesignStage.Completed;

    public Task StartAsync() => Task.CompletedTask;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task RerunAsync() => Task.CompletedTask;
    public Task RetryFromStageAsync(string stageId) => Task.CompletedTask;
    public Task ChangeOutputFolderAsync(string folder) => Task.CompletedTask;
    public void Dispose() { }
}

/// <summary>Capture-only 自动旋转 fixture: a portrait phone clip recorded sideways.</summary>
internal sealed class DesignOrientationTask : DesignStageTask, IOrientationTask
{
    public DesignOrientationTask(DesignStage scenario, string source, MediaInfo info, IReadOnlyList<byte[]> frames, string outputFolder) : base(scenario)
    {
        const int planned = 12;
        var count = scenario switch { DesignStage.Configuring => 0, DesignStage.Running or DesignStage.Paused => 7, DesignStage.Failed => 5, DesignStage.Cancelled => 6, _ => planned };
        var samples = Enumerable.Range(0, count).Select(i =>
        {
            var seconds = Math.Round(info.Duration * (i + .5) / planned, 1);
            var unusable = i == 4;
            double[] scores = unusable ? [.31, .27, .22, .2] : i == 9 ? [.18, .41, .11, .3] : [.06, .82 - i % 3 * .05, .04, .08 + i % 3 * .05];
            return new OrientationSampleView(seconds, scores)
            {
                Image = frames.Count == 0 ? null : frames[i % frames.Count], Rotation = unusable ? null : i == 9 ? 90 : 90, Agrees = !unusable
            };
        }).ToArray();
        var valid = samples.Count(s => s.Rotation is not null);
        View = new OrientationTaskView
        {
            SourcePath = source, Info = info, SampledFrames = count, ValidFrames = valid, AgreeingFrames = samples.Count(s => s.Agrees), PlannedFrames = planned,
            Rotation = IsDone ? 90 : null, Decision = IsDone ? "顺时针旋转 90°" : "", Reliability = IsDone ? "高" : "",
            Reason = IsDone ? $"{valid} 个有效采样中 {samples.Count(s => s.Agrees)} 个判定需要顺时针旋转 90°，画面中人物与文字方向一致。" : "",
            Backend = "OrientationNet · CPU", Samples = samples, HasResult = scenario != DesignStage.Configuring, IsPartial = !IsDone, CanConfirm = IsDone,
            Output = new TaskOutputView { Folder = outputFolder, Path = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(source) + "_rotated.mp4") }
        };
    }
    public OrientationTaskView View { get; }
    public override string FeatureId => "rotate";
    public override IReadOnlyList<TaskStageDescriptor> Stages => OrientationStageIds.All;
    protected override int ActiveStage => 1;
    protected override TaskRuntimeInfo Runtime => new("OrientationNet", "CPU");
    protected override string Error => "无法解码第 5 个采样点的视频帧（数据损坏或编码不受支持）。";
    protected override double Progress => Scenario switch { DesignStage.Failed => 38, DesignStage.Cancelled => 45, _ => 52 };
    protected override double TotalSeconds => 9.4;
    protected override string? RetryStageId => OrientationStageIds.Sample;
    protected override StageState CompletedState(int index) => index == 4 ? StageState.Skipped : StageState.Done;
    protected override string Detail(int index, StageState state) => index switch
    {
        0 => Pick(state, $"{View.Info!.Width}×{View.Info.Height} · H.264 · {PersonTaskView.Time(View.Info.Duration, 1)}"),
        1 => Pick(state, $"{View.SampledFrames} 帧 · {View.ValidFrames} 帧有效", $"{View.SampledFrames} / {View.PlannedFrames} 帧"),
        2 => Pick(state, "顺时针旋转 90° · 可信度高"),
        3 => Pick(state, "已保存检测结果"),
        _ => state == StageState.Skipped ? "确认后由旋转任务导出" : Pick(state, "", "", "确认后导出")
    };
}

/// <summary>Capture-only 自动字幕 fixture.</summary>
internal sealed class DesignSubtitleTask : DesignStageTask, ISubtitleTask
{
    private static readonly string[] Script =
    [
        "大家好，今天我们去海边看日出。", "现在是早上五点半，天还没有完全亮。", "我们先沿着这条小路走到礁石那边。", "风有点大，大家注意脚下。",
        "你看，东边的云已经开始变红了。", "这里的视野特别开阔，能看到整个海湾。", "太阳马上就要出来了。", "出来了！今天的日出非常完整。",
        "我们在这里多拍几张照片。", "接下来去附近的早市吃早饭。", "这家的海鲜粥是本地人推荐的。", "好了，今天的视频就到这里，我们下次见。"
    ];
    public DesignSubtitleTask(DesignStage scenario, string source, MediaInfo info, string outputFolder) : base(scenario)
    {
        var duration = info.Duration;
        var reached = scenario switch { DesignStage.Running or DesignStage.Paused => duration * .58, DesignStage.Failed => duration * .41, DesignStage.Cancelled => duration * .47, DesignStage.Completed => duration, _ => 0 };
        var lines = Script.Select((text, i) =>
        {
            var start = Math.Round(duration * (i * .08 + .02), 1);
            return new SubtitleLineView(i + 1, start, Math.Round(start + duration * .055, 1), text) { Language = "zh" };
        }).Where(line => line.End <= reached).ToArray();
        var output = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(source) + ".srt");
        View = new SubtitleTaskView
        {
            SourcePath = source, Info = info, RecognizedSeconds = reached, Lines = scenario == DesignStage.Configuring ? [] : lines,
            Language = "中文", ModelName = "Whisper small", OutputFormat = "SRT", HasResult = lines.Length > 0, IsPartial = !IsDone, CanReview = IsDone,
            Output = new TaskOutputView { Folder = outputFolder, Path = output, Exists = IsDone, Bytes = IsDone ? 2_416 : null }
        };
    }
    public SubtitleTaskView View { get; }
    public override string FeatureId => "auto-subtitle";
    public override IReadOnlyList<TaskStageDescriptor> Stages => SubtitleStageIds.All;
    protected override int ActiveStage => 2;
    protected override TaskRuntimeInfo Runtime => new("Whisper small", "CPU", "未检测到可用 GPU 执行器");
    protected override string Error => "音频解码失败：音轨在 0:24 处数据损坏。";
    protected override double Progress => Scenario switch { DesignStage.Failed => 45, DesignStage.Cancelled => 50, _ => 61 };
    protected override double TotalSeconds => 38.2;
    protected override string? RetryStageId => SubtitleStageIds.Recognize;
    protected override string Detail(int index, StageState state) => index switch
    {
        0 => Pick(state, $"AAC 立体声 · {PersonTaskView.Time(View.Info!.Duration, 1)}"),
        1 => Pick(state, "Whisper small · 已下载"),
        2 => Pick(state, $"{View.Lines.Count} 条字幕", $"已识别至 {PersonTaskView.Time(View.RecognizedSeconds, 1)} · {View.Lines.Count} 条"),
        3 => Pick(state, "时间轴连续，无重叠"),
        _ => Pick(state, "SRT · 2 KB")
    };
}

/// <summary>Capture-only 视频总结 fixture: a long video understood in eight segments.</summary>
internal sealed class DesignVideoSummaryTask : DesignStageTask, IVideoSummaryTask
{
    private static readonly string[] Titles = ["出发与路线介绍", "海边步道", "等待日出", "日出全程", "拍照与合影", "早市", "海鲜粥", "总结与道别"];
    public DesignVideoSummaryTask(DesignStage scenario, string source, MediaInfo info, string outputFolder) : base(scenario)
    {
        var duration = info.Duration;
        var understood = scenario switch { DesignStage.Running or DesignStage.Paused => 5, DesignStage.Failed => 3, DesignStage.Cancelled => 4, DesignStage.Completed => 8, _ => 0 };
        var segments = scenario == DesignStage.Configuring ? [] : Titles.Select((title, i) =>
        {
            var state = i < understood ? StageState.Done : i == understood && scenario != DesignStage.Completed
                ? scenario switch { DesignStage.Running => StageState.Active, DesignStage.Paused => StageState.Paused, DesignStage.Failed => StageState.Failed, _ => StageState.Cancelled }
                : StageState.Pending;
            return new VideoSummarySegmentView(i + 1, Math.Round(duration * i / 8), Math.Round(duration * (i + 1) / 8), state) { Title = i < understood ? title : "" };
        }).ToArray();
        View = new VideoSummaryTaskView
        {
            SourcePath = source, Info = info, IsLongVideo = true, Segments = segments, FramesAnalyzed = scenario == DesignStage.Configuring ? 0 : 96,
            TranscriptLines = scenario == DesignStage.Configuring ? 0 : 184, TranscriptSource = "内嵌字幕", ModelName = "Qwen2.5-VL 7B",
            Summary = IsDone ? "视频记录了一次海边看日出的短途旅行：清晨沿步道走到礁石区等待日出，完整拍到太阳升起的过程，随后到附近早市品尝本地推荐的海鲜粥。整体节奏舒缓，画面以海景和人物合影为主。" : "",
            Highlights = IsDone ? ["5:30 出发，沿海边步道前往礁石区", "日出过程完整，云层由暗转红", "早市海鲜粥为本地人推荐"] : [],
            Keywords = IsDone ? ["日出", "海边", "早市", "旅行"] : [],
            Chapters = IsDone ? Titles.Select((title, i) => new VideoSummaryChapterView(Math.Round(duration * i / 8), Math.Round(duration * (i + 1) / 8), title)
                { Text = i == 3 ? "太阳从海平面升起，云层颜色由灰转红，镜头保持固定机位拍摄全过程。" : "" }).ToArray() : [],
            HasResult = IsDone, IsPartial = !IsDone,
            Output = new TaskOutputView { Folder = outputFolder, Path = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(source) + "_summary.md"), Exists = IsDone, Bytes = IsDone ? 6_200 : null }
        };
    }
    public VideoSummaryTaskView View { get; }
    public override string FeatureId => "video-summary";
    public override IReadOnlyList<TaskStageDescriptor> Stages => VideoSummaryStageIds.All;
    protected override int ActiveStage => 4;
    protected override TaskRuntimeInfo Runtime => new("Qwen2.5-VL 7B", "本地服务");
    protected override string Error => "模型服务无响应（请求超时 120 秒）。";
    protected override double Progress => Scenario switch { DesignStage.Failed => 52, DesignStage.Cancelled => 58, _ => 64 };
    protected override double TotalSeconds => 312;
    protected override string? RetryStageId => VideoSummaryStageIds.UnderstandSegments;
    protected override double StageSeconds(int index) => index == 4 ? 180 : 16;
    protected override string Detail(int index, StageState state)
    {
        var understood = View.Segments.Count(s => s.State == StageState.Done);
        return index switch
        {
            0 => Pick(state, $"{View.Info!.Width}×{View.Info.Height} · {PersonTaskView.Time(View.Info.Duration, 0)}"),
            1 => Pick(state, "内嵌字幕 · 184 条"),
            2 => Pick(state, "96 帧"),
            3 => Pick(state, "8 段"),
            4 => Pick(state, "8 段已理解", $"{understood} / 8 段"),
            5 => Pick(state, "8 个章节"),
            6 => Pick(state, "8 个章节已核验"),
            7 => Pick(state, "概要 · 3 条要点"),
            _ => Pick(state, "报告 6 KB")
        };
    }
}

/// <summary>Capture-only 媒体标签 / 文件夹分类 fixture.</summary>
internal sealed class DesignMediaClassificationTask : DesignStageTask, IMediaClassificationTask
{
    private static readonly (string Name, string Result)[] Files =
    [
        ("IMG_2031.jpg", "风景 · 海边"), ("IMG_2032.jpg", "风景 · 日出"), ("IMG_2035.jpg", "人物 · 合影"), ("VID_2036.mp4", "人物 · 户外"),
        ("IMG_2040.jpg", "美食 · 海鲜"), ("IMG_2041.jpg", "美食"), ("IMG_2044.jpg", "风景 · 礁石"), ("IMG_2047.jpg", "人物 · 自拍"),
        ("IMG_2050.jpg", "街景 · 早市"), ("IMG_2052.heic", ""), ("IMG_2055.jpg", "风景 · 海湾"), ("IMG_2058.jpg", "美食 · 早餐")
    ];
    private readonly bool _tagging;
    public static IEnumerable<string> ImageFileNames => Files.Select(file => file.Name).Where(name => Path.GetExtension(name) == ".jpg");

    public DesignMediaClassificationTask(DesignStage scenario, bool tagging, string folder, string outputFolder) : base(scenario)
    {
        _tagging = tagging;
        var total = 48;
        var done = scenario switch { DesignStage.Running or DesignStage.Paused => 29, DesignStage.Failed => 17, DesignStage.Cancelled => 22, DesignStage.Completed => 48, _ => 0 };
        var shown = Math.Min(Files.Length, scenario == DesignStage.Completed ? Files.Length : done * Files.Length / total + (IsLive ? 1 : 0));
        var items = scenario == DesignStage.Configuring ? [] : Files.Take(shown).Select((file, i) =>
        {
            var active = IsLive && i == shown - 1;
            var failed = file.Result.Length == 0;
            return new MediaClassificationItemView(Path.Combine(folder, file.Name), active ? (Scenario == DesignStage.Paused ? StageState.Paused : StageState.Active) : failed ? StageState.Failed : StageState.Done)
            {
                Result = active || failed ? "" : file.Result, Detail = failed ? "无法读取 HEIC 图像（缺少解码器）" : "", Confidence = active || failed ? null : .78 + i % 4 * .05
            };
        }).ToArray();
        var groups = scenario == DesignStage.Configuring ? [] : new (string Name, int Count)[] { ("风景", 17), ("人物", 12), ("美食", 10), ("街景", 6), ("其他", 2) }
            .Select(g => new MediaClassificationGroupView(g.Name, Math.Max(1, g.Count * done / total)) { Target = tagging ? "" : Path.Combine(outputFolder, g.Name) }).ToArray();
        // Load the actual JPEG named by the preview caption, never a video thumbnail with a JPG label.
        var previewItem = items.LastOrDefault(item => item.State == StageState.Done && Path.GetExtension(item.Path) == ".jpg");
        var previewPath = previewItem?.Path ?? Path.Combine(folder, ImageFileNames.First());
        View = new MediaClassificationTaskView
        {
            SourcePath = folder, IsTagging = tagging, TotalItems = scenario == DesignStage.Configuring ? total : total, DoneItems = done, FailedItems = items.Count(i => i.State == StageState.Failed),
            Items = items, Groups = tagging || scenario is DesignStage.Completed or DesignStage.Failed or DesignStage.Cancelled ? groups : groups.Take(3).ToArray(),
            ModelName = "SigLIP 2 · Qwen2.5-VL 3B", Preview = File.ReadAllBytes(previewPath),
            PreviewCaption = Path.GetFileName(previewPath) + (previewItem is null ? "" : " · " + previewItem.Result),
            HasResult = done > 0, IsPartial = !IsDone,
            Output = new TaskOutputView { Folder = tagging ? "" : outputFolder, Path = tagging ? "" : outputFolder, Exists = IsDone && !tagging, IsFixedLocation = tagging }
        };
    }
    public MediaClassificationTaskView View { get; }
    public override string FeatureId => _tagging ? "media-ai" : "folder-classification";
    public override IReadOnlyList<TaskStageDescriptor> Stages => _tagging ? MediaClassificationStageIds.All.Where(s => s.Id != MediaClassificationStageIds.PlanClassification).ToArray() : MediaClassificationStageIds.All;
    protected override int ActiveStage => 2;
    protected override TaskRuntimeInfo Runtime => new("SigLIP 2", "CPU");
    protected override bool CanChangeOutput => !_tagging;
    protected override string Error => "模型推理失败：内存不足（已分析 17 个文件，检查点已保存）。";
    protected override double Progress => Scenario switch { DesignStage.Failed => 38, DesignStage.Cancelled => 46, _ => 60 };
    protected override double TotalSeconds => 96;
    protected override string? RetryStageId => MediaClassificationStageIds.AnalyzeItems;
    protected override string Detail(int index, StageState state) => Stages[index].Id switch
    {
        MediaClassificationStageIds.ScanFiles => Pick(state, "48 个文件 · 无检查点"),
        MediaClassificationStageIds.PrepareModel => Pick(state, "SigLIP 2 · 已下载"),
        MediaClassificationStageIds.AnalyzeItems => Pick(state, "48 个文件 · 1 个未能识别", $"{View.DoneItems} / {View.TotalItems} 个文件"),
        MediaClassificationStageIds.PlanClassification => Pick(state, "5 个类别"),
        _ => Pick(state, _tagging ? "标签已写入媒体库" : "已移动 47 个文件")
    };
}
