namespace AvaMedia.Core;

/// <summary>Evenly spaced filmstrip sample times (cell centres).</summary>
public static class FrameStrip
{
    public const int Width = 160, Height = 90, MaxCount = 24;

    public static double[] Times(double duration, int count)
    {
        count = Math.Clamp(count, 1, MaxCount);
        if (!double.IsFinite(duration) || duration <= 0) return [0];
        return Enumerable.Range(0, count).Select(index => duration * ((index + .5) / count)).ToArray();
    }
}
