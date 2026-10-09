namespace AvaMedia.Core;

/// <summary>One sampled frame described by the vision step. Refused frames stay in the list with Description null.</summary>
/// <param name="Model">Display text such as "provider · model".</param>
public sealed record FrameCaption(TimeSpan Timestamp, string? Description, bool Refused, string Model)
{
    /// <summary>Saved frame image relative to the result output folder (e.g. frames/frame-001.png); null when no image was saved.</summary>
    public string? FramePath { get; init; }
    /// <summary>Online provider id (OnlineAiOptions.Id); null for local models.</summary>
    public string? ProviderId { get; init; }
    /// <summary>Model identifier sent to the provider, or the local ModelCatalog id.</summary>
    public string? ModelId { get; init; }
}

/// <summary>
/// Result of the two-step frame summary pipeline. When <see cref="Aborted"/> is true no description or summary
/// model was called: <see cref="Frames"/> is empty and <see cref="Summary"/> is null.
/// </summary>
public sealed record VideoSummaryResult(IReadOnlyList<FrameCaption> Frames, string? Summary, string? SummaryModel, bool Aborted, string? AbortReason)
{
    /// <summary>The summary model answered with a refusal; Summary is null and the refusal is surfaced instead of hidden.</summary>
    public bool SummaryRefused { get; init; }
    /// <summary>Online provider id of the summary model (OnlineAiOptions.Id); null for local models or aborted runs.</summary>
    public string? SummaryProviderId { get; init; }
    /// <summary>Summary model identifier sent to the provider, or the local ModelCatalog id; null when no summary model ran.</summary>
    public string? SummaryModelId { get; init; }
    public int RefusedFrames => Frames.Count(frame => frame.Refused);
    public static VideoSummaryResult Abort(string reason) => new([], null, null, true, reason);
}

/// <summary>A sampled frame handed to the safety check and the vision model. Image is an encoded PNG/JPEG.</summary>
public sealed record VideoSummaryFrame(TimeSpan Timestamp, byte[] Image);

/// <summary>Raised by job execution when the hard-coded minor-safety check stops a video summary.</summary>
public sealed class VideoSummaryAbortedException(VideoSummaryResult result)
    : InvalidOperationException("视频总结已安全中止：" + result.AbortReason)
{
    public VideoSummaryResult Result { get; } = result;
}
