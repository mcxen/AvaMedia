namespace AvaMedia.Core;

public interface IMediaPreview
{
    Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360,
        CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false);
    Task<double> AdjacentFrameTime(string input, double seconds, int direction,
        CancellationToken ct = default, int videoStreamIndex = 0);
}
