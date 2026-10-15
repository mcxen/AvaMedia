namespace AvaMedia.Core;

/// <summary>The detection job saves a proposal; confirmed rotations are separate conversion jobs.</summary>
public static class OrientationStageIds
{
    public const string ReadVideo = "read-video";
    public const string Sample = "sample-orientation";
    public const string Decide = "decide-orientation";
    public const string SaveResult = "save-result";
    public const string Export = "rotate-export";

    public static IReadOnlyList<TaskStageDescriptor> All { get; } = Array.AsReadOnly(new TaskStageDescriptor[]
    {
        new(ReadVideo, "读取视频", "校验源文件并读取视频轨道信息"),
        new(Sample, "方向采样", "按需以 320 和 640 分辨率采样并检测四个方向"),
        new(Decide, "判定方向", "汇总采样证据并确认源视频未改变"),
        new(SaveResult, "保存结果", "保存方向检测结果，供确认后创建旋转任务"),
        new(Export, "旋转导出", "确认方向后由独立的旋转任务导出")
    });
}
