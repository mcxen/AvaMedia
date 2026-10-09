using System.Globalization;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Shared interpretation of FFprobe streams; video indices remain raw v:N indices.</summary>
public static class MediaStreams
{
    public static bool IsContentVideo(JsonElement stream) => Text(stream, "codec_type") == "video" &&
        !Disposition(stream, "attached_pic") && !Disposition(stream, "timed_thumbnails");

    internal static int VideoIndex(IReadOnlyList<JsonElement> videos, int requested)
    {
        if (requested != 0) return requested;
        for (var index = 0; index < videos.Count; index++)
            if (IsContentVideo(videos[index])) return index;
        return 0;
    }

    internal static string VideoSpecifier(int index) => index == 0 ? "V:0" : "v:" + index;
    internal static bool IsQuarterTurn(double angle) => double.IsFinite(angle) && Math.Abs(Math.Abs(angle) % 180 - 90) < .01;

    internal static double Duration(JsonElement root, IEnumerable<JsonElement> streams)
    {
        var duration = root.TryGetProperty("format", out var format) ? PositiveNumber(format, "duration") : 0;
        return duration > 0 ? duration : streams.Where(stream => IsContentVideo(stream) || Text(stream, "codec_type") == "audio")
            .Select(StreamDuration).DefaultIfEmpty().Max();
    }

    internal static double StreamDuration(JsonElement stream)
    {
        var duration = PositiveNumber(stream, "duration");
        if (duration > 0) return duration;
        duration = PositiveNumber(stream, "duration_ts") * Ratio(Text(stream, "time_base"));
        if (double.IsFinite(duration) && duration > 0) return duration;
        if (stream.TryGetProperty("tags", out var tags))
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.Equals("DURATION", StringComparison.OrdinalIgnoreCase) &&
                    TimeSpan.TryParse(tag.Value.ToString(), CultureInfo.InvariantCulture, out var time) && time.TotalSeconds > 0)
                    return time.TotalSeconds;
        return 0;
    }

    internal static double FrameRate(JsonElement stream)
    {
        var rate = Ratio(Text(stream, "avg_frame_rate"));
        return rate > 0 ? rate : Ratio(Text(stream, "r_frame_rate"));
    }

    private static double Ratio(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) || denominator <= 0) return 0;
        var ratio = numerator / denominator;
        return double.IsFinite(ratio) && ratio > 0 ? ratio : 0;
    }

    private static double PositiveNumber(JsonElement element, string name) =>
        double.TryParse(Text(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value > 0 ? value : 0;

    private static bool Disposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object &&
        disposition.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var flag) && flag != 0;

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) ? value.ToString() : "";
}
