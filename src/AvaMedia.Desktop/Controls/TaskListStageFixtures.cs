using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Design fixtures for the task-list stage display (tasklist-stages/design.html): one job per single-stage and
/// multi-stage state with a matching queue-level summary. Used only by UI capture tools; never by the queue.</summary>
public static class TaskListStageFixtures
{
    private sealed record Fixture(string Name, string FeatureId, JobState State, Func<JobStageSummary> Summary, string Error = "");

    private static JobStageItem S(string id, string title, StageState state, string detail = "", double? fraction = null, double? seconds = null)
        => new(id, title, state) { Detail = detail, Fraction = fraction, Elapsed = seconds is { } s ? TimeSpan.FromSeconds(s) : null };

    private static JobStageSummary Single(JobState state, string title, double? fraction, string activity = "", double? remaining = null, string error = "")
        => new()
        {
            State = state, Stages = [S("run", title, state switch
            {
                JobState.Running => StageState.Active, JobState.Paused => StageState.Paused, JobState.Failed => StageState.Failed,
                JobState.Completed => StageState.Done, JobState.Cancelled => StageState.Cancelled, _ => StageState.Pending
            }, fraction: fraction)],
            CurrentIndex = state is JobState.Running or JobState.Paused or JobState.Failed or JobState.Cancelled ? 0 : -1,
            OverallFraction = fraction, StageFraction = fraction, ActivityText = activity, IsClockRunning = state == JobState.Running,
            Remaining = remaining is { } r ? TimeSpan.FromSeconds(r) : null, Error = error, FailedStageId = state == JobState.Failed ? "run" : "",
            SnapshotUtc = DateTime.UtcNow
        };

    private static readonly string[] PersonStages = ["读取视频", "准备检测模型", "人物检测", "细化片段边界", "导出片段"];
    private static JobStageItem[] Person(params (StageState State, string Detail, double? Fraction, double? Seconds)[] states)
        => states.Select((s, i) => S(new[] { "read-video", "prepare-model", "detect", "refine", "export" }[i], PersonStages[i], s.State, s.Detail, s.Fraction, s.Seconds)).ToArray();

    private static Fixture[] Fixtures() =>
    [
        new("航拍素材_02.mp4", "video-compress", JobState.Waiting, () => Single(JobState.Waiting, "视频压缩", null)),
        new("旅行vlog_成都.mp4", "mp4", JobState.Running, () => Single(JobState.Running, "格式转换", .34, "编码 1.8× 实时", 128)),
        new("宣传片_v3.mp4", "delogo", JobState.Paused, () => Single(JobState.Paused, "去除水印", .41)),
        new("老家录像.avi", "repair", JobState.Failed, () => Single(JobState.Failed, "修复 / 重新封装", null, error: "无法解析输入：文件头损坏"), "无法解析输入：文件头损坏"),
        new("产品演示.mov", "join", JobState.Cancelled, () => Single(JobState.Cancelled, "视频合并", .2)),
        new("播客_第12期.mp4", "audio-mp3", JobState.Completed, () => Single(JobState.Completed, "→ MP3", 1)),
        new("我做了20年的拿手菜.mp4", "person-clip", JobState.Running, () => new()
        {
            State = JobState.Running, IsMultiStage = true, CurrentIndex = 2, OverallFraction = .48, StageFraction = .62,
            Elapsed = TimeSpan.FromSeconds(41), IsClockRunning = true, Remaining = TimeSpan.FromSeconds(40), SnapshotUtc = DateTime.UtcNow,
            Stages = Person((StageState.Done, "1920×1080 · H.264 · 04:12.4", null, .4), (StageState.Skipped, "已跳过 · 模型已在内存中", null, null),
                (StageState.Active, "234 / 378 帧", .62, 38), (StageState.Pending, "", null, null), (StageState.Pending, "", null, null))
        }),
        new("城市夜景延时.mp4", "video-summary", JobState.Paused, () => new()
        {
            State = JobState.Paused, IsMultiStage = true, CurrentIndex = 1, OverallFraction = .31, StageFraction = .35, Elapsed = TimeSpan.FromSeconds(204),
            SnapshotUtc = DateTime.UtcNow,
            Stages = [S("prepare", "读取视频与字幕", StageState.Done, "3840×2160 · 12:40", seconds: 3.2), S("speech", "语音识别", StageState.Paused, "04:26 / 12:40", .35, 198),
                S("frames", "画面理解", StageState.Pending), S("summary", "生成总结", StageState.Pending)]
        }),
        new("访谈_第3集.mp4", "person-clip", JobState.Failed, () => new()
        {
            State = JobState.Failed, IsMultiStage = true, CurrentIndex = 3, OverallFraction = .7, Elapsed = TimeSpan.FromSeconds(372),
            FailedStageId = "refine", Error = "模型输出为空", RetryStageId = "refine", SnapshotUtc = DateTime.UtcNow,
            Stages = Person((StageState.Done, "1920×1080 · H.264 · 38:02", null, .6), (StageState.Done, "NanoDet · CPU", null, 2.1),
                (StageState.Done, "57060 / 57060 帧 · 41 段", null, 358), (StageState.Failed, "模型输出为空", null, 11), (StageState.Pending, "", null, null))
        }, "模型输出为空"),
        new("相册导入_2026-09.mp4", "rotate", JobState.Completed, () => new()
        {
            State = JobState.Completed, IsMultiStage = true, OverallFraction = 1, Elapsed = TimeSpan.FromSeconds(72), SnapshotUtc = DateTime.UtcNow,
            Stages = [S("read", "读取视频", StageState.Done, "1920×1080", seconds: .5), S("sample", "抽取样本帧", StageState.Done, "24 帧", seconds: 6),
                S("model", "准备模型", StageState.Skipped, "已跳过 · 模型已在内存中"), S("decide", "判定方向", StageState.Done, "顺时针 90°", seconds: 65)]
        }),
        new("发布会_直播回放.mp4", "auto-subtitle", JobState.Waiting, () => new()
        {
            State = JobState.Waiting, IsMultiStage = true, SnapshotUtc = DateTime.UtcNow,
            Stages = [S("audio", "提取音频", StageState.Pending), S("model", "准备语音模型", StageState.Pending), S("speech", "语音识别", StageState.Pending), S("write", "写入字幕", StageState.Pending)]
        }),
        new("家庭聚会_2025.mp4", "person-clip", JobState.Cancelled, () => new()
        {
            State = JobState.Cancelled, IsMultiStage = true, CurrentIndex = 2, OverallFraction = .3, Elapsed = TimeSpan.FromSeconds(95), SnapshotUtc = DateTime.UtcNow,
            Stages = Person((StageState.Done, "1920×1080 · H.264 · 25:10", null, .5), (StageState.Done, "NanoDet · CPU", null, 1.9),
                (StageState.Cancelled, "已取消 · 812 / 3020 帧", null, 92), (StageState.Pending, "", null, null), (StageState.Pending, "", null, null))
        }),
    ];

    /// <summary>Creates fixture jobs whose inputs are copies of <paramref name="sampleVideo"/> under <paramref name="folder"/>.</summary>
    public static Job[] CreateJobs(string sampleVideo, string folder)
    {
        Directory.CreateDirectory(folder);
        return Fixtures().Select(fixture =>
        {
            var input = Path.Combine(folder, fixture.Name);
            if (!File.Exists(input)) File.Copy(sampleVideo, input);
            return new Job { FeatureId = fixture.FeatureId, Inputs = [input], Output = Path.Combine(folder, "out", Path.GetFileNameWithoutExtension(fixture.Name) + "_结果.mp4") };
        }).ToArray();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Job, JobStageSummary> Pinned = new();

    /// <summary>Puts every fixture job into its designed state and summary and pins that summary: the live publisher
    /// re-derives summaries for running rows, so a fixture row restores its designed value whenever it is replaced.</summary>
    public static void Apply(IEnumerable<Job> jobs)
    {
        var fixtures = Fixtures().ToDictionary(f => f.Name);
        foreach (var job in jobs)
        {
            if (!fixtures.TryGetValue(Path.GetFileName(job.Inputs.FirstOrDefault() ?? ""), out var fixture)) continue;
            job.State = fixture.State;
            job.Error = fixture.Error;
            if (fixture.State == JobState.Completed) job.Progress = 100;
            var summary = fixture.Summary();
            if (Pinned.TryGetValue(job, out _)) Pinned.AddOrUpdate(job, summary);
            else
            {
                Pinned.Add(job, summary);
                job.PropertyChanged += (sender, e) =>
                {
                    if (e.PropertyName == nameof(Job.StageSummary) && sender is Job changed && Pinned.TryGetValue(changed, out var pinned)
                        && !ReferenceEquals(changed.StageSummary, pinned)) changed.StageSummary = pinned;
                };
            }
            job.StageSummary = summary;
        }
    }
}
