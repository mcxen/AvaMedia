using System.Globalization;

namespace AvaMedia.Core;

public static class MediaTime
{
    /// <summary>Displays total hours and retains sub-millisecond editing boundaries.</summary>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "无效";
        var fine = seconds > 0 && (seconds < .01 || Math.Abs(seconds * 1000 - Math.Round(seconds * 1000)) > .00001);
        var units = fine ? 1000000L : 1000L;
        var rounded = Math.Round(seconds * units);
        if (!double.IsFinite(rounded) || rounded >= long.MaxValue) return "无效";
        var ticks = (long)rounded;
        var wholeSeconds = ticks / units;
        var fraction = (ticks % units).ToString(fine ? "D6" : "D3", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{wholeSeconds / 3600:00}:{wholeSeconds / 60 % 60:00}:{wholeSeconds % 60:00}.{fraction}");
    }
}
