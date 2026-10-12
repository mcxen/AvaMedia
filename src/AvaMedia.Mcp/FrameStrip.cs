namespace AvaMedia.Mcp;

/// <summary>Evenly spaced filmstrip sample times (cell centres) for the remote clip editor.</summary>
public static class FrameStrip
{
    public const int Width = Core.FrameStrip.Width, Height = Core.FrameStrip.Height, MaxCount = Core.FrameStrip.MaxCount;

    public static double[] Times(double duration, int count)
        => Core.FrameStrip.Times(duration, count);
}
