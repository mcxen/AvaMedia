namespace AvaMedia.Core;

// Tool-specific result views for the multi-stage task window (UISPEC 10.16). Each session keeps the generic
// IMultiStageTask snapshot and additionally exposes one immutable view, refreshed before Changed is raised.
// Values come only from real execution or saved results; unknown values stay null/empty, never estimated.

/// <summary>Common output facts shown in the window footer.</summary>
public sealed record TaskOutputView
{
    /// <summary>Final output file or folder, or the planned path before completion.</summary>
    public string Path { get; init; } = "";
    public string Folder { get; init; } = "";
    public bool Exists { get; init; }
    public long? Bytes { get; init; }
    /// <summary>Result is stored in an app-managed location; the folder cannot be changed.</summary>
    public bool IsFixedLocation { get; init; }
}

// ---------------- 自动旋转（方向检测） ----------------

/// <summary>One sampled frame and its four-direction scores (0/90/180/270, 0..1). Image is an encoded PNG/JPEG thumbnail when available.</summary>
public sealed record OrientationSampleView(double Seconds, IReadOnlyList<double> Scores)
{
    public byte[]? Image { get; init; }
    /// <summary>Best clockwise rotation for this frame; null when the frame was not usable.</summary>
    public int? Rotation { get; init; }
    public bool Agrees { get; init; }
}

public sealed record OrientationTaskView
{
    public string SourcePath { get; init; } = "";
    public MediaInfo? Info { get; init; }
    public int SampledFrames { get; init; }
    public int ValidFrames { get; init; }
    public int AgreeingFrames { get; init; }
    public int? PlannedFrames { get; init; }
    /// <summary>Clockwise degrees needed to correct the video; null while unknown or uncertain.</summary>
    public int? Rotation { get; init; }
    /// <summary>Short Chinese direction, e.g. "顺时针旋转 90°" / "方向正常" / "无法确定"; empty before a decision.</summary>
    public string Decision { get; init; } = "";
    public string Reliability { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Backend { get; init; } = "";
    public IReadOnlyList<OrientationSampleView> Samples { get; init; } = [];
    public bool HasResult { get; init; }
    public bool IsPartial { get; init; } = true;
    /// <summary>A confirmed rotation can be exported as a separate rotate job.</summary>
    public bool CanConfirm { get; init; }
    public TaskOutputView Output { get; init; } = new();
}

public interface IOrientationTask : IMultiStageTask { OrientationTaskView View { get; } }

// ---------------- 自动字幕 ----------------

public sealed record SubtitleLineView(int Number, double Start, double End, string Text)
{
    public string Language { get; init; } = "";
}

public sealed record SubtitleTaskView
{
    public string SourcePath { get; init; } = "";
    public MediaInfo? Info { get; init; }
    /// <summary>Recognition range in source seconds (whole file when not limited).</summary>
    public double RangeStart { get; init; }
    public double? RangeEnd { get; init; }
    /// <summary>Source position recognition has reached.</summary>
    public double RecognizedSeconds { get; init; }
    public IReadOnlyList<SubtitleLineView> Lines { get; init; } = [];
    public string Language { get; init; } = "";
    public string ModelName { get; init; } = "";
    /// <summary>e.g. "SRT" / "ASS" / "烧录到视频".</summary>
    public string OutputFormat { get; init; } = "";
    public bool HasResult { get; init; }
    public bool IsPartial { get; init; } = true;
    /// <summary>Lines can be reviewed and edited in the subtitle review window.</summary>
    public bool CanReview { get; init; }
    public TaskOutputView Output { get; init; } = new();
}

public interface ISubtitleTask : IMultiStageTask { SubtitleTaskView View { get; } }

// ---------------- 视频总结 ----------------

public sealed record VideoSummarySegmentView(int Number, double Start, double End, StageState State)
{
    public string Title { get; init; } = "";
}

public sealed record VideoSummaryChapterView(double Start, double End, string Title)
{
    public string Text { get; init; } = "";
}

public sealed record VideoSummaryTaskView
{
    public string SourcePath { get; init; } = "";
    public MediaInfo? Info { get; init; }
    public bool IsLongVideo { get; init; }
    /// <summary>Semantic segments once planned; empty before planning or for short videos.</summary>
    public IReadOnlyList<VideoSummarySegmentView> Segments { get; init; } = [];
    public int FramesAnalyzed { get; init; }
    public int TranscriptLines { get; init; }
    /// <summary>e.g. "内嵌字幕" / "语音转写" / "无字幕".</summary>
    public string TranscriptSource { get; init; } = "";
    public string ModelName { get; init; } = "";
    /// <summary>Overview paragraph; empty until written (or partial text when IsPartial).</summary>
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Highlights { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<VideoSummaryChapterView> Chapters { get; init; } = [];
    public bool HasResult { get; init; }
    public bool IsPartial { get; init; } = true;
    public TaskOutputView Output { get; init; } = new();
}

public interface IVideoSummaryTask : IMultiStageTask { VideoSummaryTaskView View { get; } }

// ---------------- AI 媒体整理 / 文件夹分类 ----------------

public sealed record MediaClassificationItemView(string Path, StageState State)
{
    /// <summary>Category or top tags, e.g. "风景 · 海边"; empty before analysis.</summary>
    public string Result { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Confidence { get; init; }
}

public sealed record MediaClassificationGroupView(string Name, int Count)
{
    /// <summary>Destination folder for classification; empty for tagging.</summary>
    public string Target { get; init; } = "";
}

public sealed record MediaClassificationTaskView
{
    /// <summary>Folder or file being processed.</summary>
    public string SourcePath { get; init; } = "";
    /// <summary>True for 媒体标签 (media-ai), false for 文件夹分类.</summary>
    public bool IsTagging { get; init; }
    public int TotalItems { get; init; }
    public int DoneItems { get; init; }
    public int FailedItems { get; init; }
    /// <summary>Items in processing order; sessions may cap very long lists (latest active/failed items always included).</summary>
    public IReadOnlyList<MediaClassificationItemView> Items { get; init; } = [];
    public IReadOnlyList<MediaClassificationGroupView> Groups { get; init; } = [];
    public string ModelName { get; init; } = "";
    /// <summary>Most recent analysed image (encoded) and its caption, when available.</summary>
    public byte[]? Preview { get; init; }
    public string PreviewCaption { get; init; } = "";
    public bool HasResult { get; init; }
    public bool IsPartial { get; init; } = true;
    public TaskOutputView Output { get; init; } = new();
}

public interface IMediaClassificationTask : IMultiStageTask { MediaClassificationTaskView View { get; } }
