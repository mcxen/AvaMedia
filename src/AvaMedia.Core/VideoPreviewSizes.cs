namespace AvaMedia.Core;

public readonly record struct VideoPreviewSize(int Width, int Height);

/// <summary>Pixel budgets for display previews; media analysis and exports use their own dimensions.</summary>
public static class VideoPreviewSizes
{
    public static readonly VideoPreviewSize Preview = new(640, 480);
    public static readonly VideoPreviewSize Scrub = new(384, 216);
    public static readonly VideoPreviewSize Compact = new(480, 320);
    public static readonly VideoPreviewSize Boundary = new(320, 180);
    public static readonly VideoPreviewSize Row = new(160, 100);
    public const int PickerWidth = 320;

    public static VideoPreviewSize Bound(MediaInfo info, VideoPreviewSize budget)
    {
        var display = MediaStreams.DisplaySize(info);
        if (!double.IsFinite(display.Width) || !double.IsFinite(display.Height) || display.Width <= 0 || display.Height <= 0) return budget;
        return new(Math.Max(1, (int)Math.Min(budget.Width, Math.Ceiling(display.Width))),
            Math.Max(1, (int)Math.Min(budget.Height, Math.Ceiling(display.Height))));
    }

    public static int RemoteWidth(int? requested)
    {
        var width = requested.GetValueOrDefault(Preview.Width);
        foreach (var bucket in new[] { 160, 240, 320, 480 })
            if (width <= bucket) return bucket;
        return Preview.Width;
    }
}
