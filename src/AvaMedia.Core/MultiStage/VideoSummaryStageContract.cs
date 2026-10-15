namespace AvaMedia.Core;

/// <summary>Observed execution order. Frame sampling and pixel understanding interleave in the frame agent.</summary>
public static class VideoSummaryStageIds
{
    public const string ReadVideo = "read-video";
    public const string Transcript = "transcript";
    public const string VisualAnalysis = "visual-analysis";
    public const string PlanSegments = "plan-segments";
    public const string UnderstandSegments = "understand-segments";
    public const string ReduceChapters = "reduce-chapters";
    public const string VerifyChapters = "verify-chapters";
    public const string Summarize = "summarize";
    public const string SaveResults = "save-results";

    public static IReadOnlyList<TaskStageDescriptor> All { get; } = Array.AsReadOnly(new TaskStageDescriptor[]
    {
        new(ReadVideo, "读取视频", "探测视频流与时长，校验任务参数"),
        new(Transcript, "字幕与语音", "读取字幕或转写音轨"),
        new(VisualAnalysis, "采样与画面理解", "按需采样画面并理解实际像素"),
        new(PlanSegments, "语义分段", "按字幕、镜头和上下文预算规划分段"),
        new(UnderstandSegments, "逐段理解", "理解各段证据并衔接上下文", CanRetryFrom: true),
        new(ReduceChapters, "章节归纳", "合并分段结论并归纳章节"),
        new(VerifyChapters, "章节核验", "根据证据核验章节结论"),
        new(Summarize, "汇总成文", "依据已确认的证据生成总结"),
        new(SaveResults, "保存结果", "保存报告、字幕与分析资料")
    });
}
