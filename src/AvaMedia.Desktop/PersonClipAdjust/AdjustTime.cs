using System.Globalization;

namespace AvaMedia.Desktop.PersonClipAdjust;

internal static class AdjustTime
{
    public static long Key(double seconds) => (long)Math.Round(seconds * 10);
    public static string Clock(double seconds, int decimals)
    {
        seconds = Math.Max(0, seconds);
        var scale = Math.Pow(10, decimals); var rounded = Math.Round(seconds * scale) / scale;
        var minutes = (int)(rounded / 60); var rest = rounded - minutes * 60;
        var format = decimals == 0 ? "00" : "00." + new string('0', decimals);
        return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + rest.ToString(format, CultureInfo.InvariantCulture);
    }

}
