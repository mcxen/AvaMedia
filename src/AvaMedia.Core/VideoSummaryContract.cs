namespace AvaMedia.Core;

/// <summary>One sampled frame described by the vision step. Refused frames stay in the list with Description null.</summary>
public sealed record FrameCaption(TimeSpan Timestamp, string? Description, bool Refused, string Model);

/// <summary>
/// Result of the two-step frame summary pipeline. When <see cref="Aborted"/> is true no description or summary
/// model was called: <see cref="Frames"/> is empty and <see cref="Summary"/> is null.
/// </summary>
public sealed record VideoSummaryResult(IReadOnlyList<FrameCaption> Frames, string? Summary, string? SummaryModel, bool Aborted, string? AbortReason)
{
    /// <summary>The summary model answered with a refusal; Summary is null and the refusal is surfaced instead of hidden.</summary>
    public bool SummaryRefused { get; init; }
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
