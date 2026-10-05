using System.Globalization;

namespace AvaMedia.Desktop;

/// <summary>Stable timecode presentation; unchanged fields retain the exact media boundary.</summary>
public static class EditorTime
{
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        var milliseconds = (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        var whole = milliseconds / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}.{milliseconds % 1000:000}");
    }

    public static bool TryRead(string? text, double original, out double seconds)
    {
        seconds = 0;
        var parts = (text ?? "").Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fraction)
            || hours < 0 || minutes is < 0 or > 59 || fraction is < 0 or >= 60 || !double.IsFinite(fraction)) return false;
        seconds = text == Format(original) ? original : hours * 3600d + minutes * 60 + fraction;
        return true;
    }
}
