namespace AvaMedia.Core;

/// <summary>A decoded boundary frame of an edit segment. Seconds is the requested boundary on the source timeline.</summary>
public sealed record SegmentBoundaryFrame(string Edge, double Seconds, byte[] Image);

/// <summary>
/// Start/end frame previews for clip segments, shared by the desktop editor and the remote web editor.
/// The start shows the first frame at or after the start; the end shows the last frame before the (exclusive) end.
/// </summary>
public static class SegmentBoundaryFrames
{
    public static async Task<SegmentBoundaryFrame[]> ReadAsync(IMediaPreview preview, string path, double start, double end,
        int width, int height, int videoStreamIndex = 0, CancellationToken ct = default)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start) throw new ArgumentException("片段区间无效。");
        var first = await preview.Thumbnail(path, start, width, height, ct, pad: false, videoStreamIndex: videoStreamIndex).ConfigureAwait(false);
        var last = await preview.Thumbnail(path, end, width, height, ct, pad: false, videoStreamIndex: videoStreamIndex, endExclusive: true).ConfigureAwait(false);
        return [new("start", start, first), new("end", end, last)];
    }

    /// <summary>Moves a boundary by whole frames using the media's real frame timestamps (VFR-safe).</summary>
    public static async Task<double> NudgeAsync(IMediaPreview preview, string path, double seconds, int frames,
        int videoStreamIndex = 0, CancellationToken ct = default)
    {
        if (frames == 0 || Math.Abs(frames) > 30) throw new ArgumentException("逐帧移动须为 1–30 帧。");
        var direction = Math.Sign(frames);
        for (var index = 0; index < Math.Abs(frames); index++)
            seconds = await preview.AdjacentFrameTime(path, seconds, direction, ct, videoStreamIndex).ConfigureAwait(false);
        return seconds;
    }
}
