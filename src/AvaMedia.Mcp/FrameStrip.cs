namespace AvaMedia.Mcp;

/// <summary>Evenly spaced filmstrip sample times (cell centres) for the remote clip editor.</summary>
public static class FrameStrip
{
    public const int Width = 160, Height = 90, MaxCount = 24;

    public static double[] Times(double duration, int count)
    {
        if (count is < 1 or > MaxCount) throw new ArgumentException($"胶片条帧数须为 1–{MaxCount}。");
        if (!double.IsFinite(duration) || duration <= 0) return [0];
        var last = Math.Max(0, duration - .05);
        return Enumerable.Range(0, count).Select(index => Math.Min(last, Math.Round(duration * (index + .5) / count, 3))).ToArray();
    }
}
