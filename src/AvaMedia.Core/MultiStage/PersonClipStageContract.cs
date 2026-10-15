namespace AvaMedia.Core;

/// <summary>Stage ids of the 保留有人片段 pipeline, in order.</summary>
public static class PersonClipStageIds
{
    public const string ReadVideo = "read-video";
    public const string PrepareModel = "prepare-model";
    public const string Detect = "detect";
    public const string Refine = "refine";
    public const string Export = "export";

    public static IReadOnlyList<TaskStageDescriptor> All { get; } = Array.AsReadOnly(new TaskStageDescriptor[]
    {
        new(ReadVideo, "读取视频", "探测时长、编码与音视频流"),
        new(PrepareModel, "准备检测模型", "校验并加载人物检测模型"),
        new(Detect, "人物检测", "按采样帧检测画面中的人物", CanRetryFrom: true),
        new(Refine, "细化片段边界", "在片段边界附近补充检测并合并片段", CanRetryFrom: true),
        new(Export, "导出片段", "按保留片段导出视频", CanRetryFrom: true)
    });
}

/// <summary>One kept range; IsOpen marks the newest range still growing during detection.</summary>
public sealed record PersonClipKeptSegment(int Number, double Start, double End)
{
    public bool IsOpen { get; init; }
    public double Duration => Math.Max(0, End - Start);
}

/// <summary>Detection box normalized to the frame (0..1).</summary>
public sealed record PersonClipDetectionBox(double X, double Y, double Width, double Height, double Confidence, string Label = "人物");

/// <summary>Latest frame the detector actually processed. Image is an encoded PNG/JPEG; Boxes may be empty when the detector does not expose geometry.</summary>
public sealed record PersonClipLiveFrame(double Seconds, int FrameNumber, byte[] Image)
{
    public IReadOnlyList<PersonClipDetectionBox> Boxes { get; init; } = [];
    public int PersonCount { get; init; }
}

/// <summary>Real submitted parameters, shown read-only in the configuring state and in stage plan lines.</summary>
public sealed record PersonClipSettingsSummary
{
    public string ModelName { get; init; } = "";
    /// <summary>null while unknown.</summary>
    public bool? ModelInstalled { get; init; }
    public string ModeLabel { get; init; } = "";
    public double FramesPerSecond { get; init; }
    public double Threshold { get; init; }
    public double PaddingSeconds { get; init; }
    public double MergeGapSeconds { get; init; }
    public double MinimumSeconds { get; init; }
    public bool SkipDarkFrames { get; init; }
    public bool SkipBlankFrames { get; init; }
    public bool IncludeHands { get; init; }
    public bool AnalysisOnly { get; init; }
    public string ExportLabel { get; init; } = "";
}

/// <summary>Tool-specific result model for the content slot.</summary>
public sealed record PersonClipTaskView
{
    public string SourcePath { get; init; } = "";
    /// <summary>Probe result; available for queued tasks too (backend probes in the background). null while unknown.</summary>
    public MediaInfo? Info { get; init; }
    public double AnalyzedSeconds { get; init; }
    public int SampledFrames { get; init; }
    /// <summary>Planned sample count for the whole video; null when unknown.</summary>
    public int? PlannedFrames { get; init; }
    public IReadOnlyList<PersonClipKeptSegment> Segments { get; init; } = [];
    /// <summary>True until detection covered the whole video.</summary>
    public bool IsPartial { get; init; } = true;
    public bool HasResult { get; init; }
    public double KeptSeconds { get; init; }
    public PersonClipLiveFrame? LiveFrame { get; init; }
    public PersonClipSettingsSummary Settings { get; init; } = new();
    /// <summary>Final output file (completed export) or the planned output path.</summary>
    public string OutputPath { get; init; } = "";
    public string OutputFolder { get; init; } = "";
    public bool OutputExists { get; init; }
    public long? OutputBytes { get; init; }
    /// <summary>Segments can be adjusted in the editor (complete, unchanged source).</summary>
    public bool CanAdjust { get; init; }
}

/// <summary>Person-clip session: generic stage snapshot plus the kept-segments model. Backend type: PersonClipTaskSession(Job, IMultiStageQueueHost).</summary>
public interface IPersonClipTask : IMultiStageTask
{
    PersonClipTaskView View { get; }
}
