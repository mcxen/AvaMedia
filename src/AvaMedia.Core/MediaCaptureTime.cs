using System.Globalization;
using System.Text.Json;
using ImageMagick;

namespace AvaMedia.Core;

/// <summary>Read camera timestamps; copying or downloading a file does not establish a shooting time.</summary>
internal static class MediaCaptureTime
{
    public static async Task<DateTime?> ReadAsync(IMediaEngine engine, string path, CancellationToken ct)
    {
        try
        {
            if (!VideoFormats.IsVideo(path))
            {
                var metadata = await ImageCodec.ReadMetadataAsync(path, ct).ConfigureAwait(false);
                if (!metadata.TryGetValue("DateTimeOriginal", out var text)
                    || !DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
                if (metadata.TryGetValue("OffsetTimeOriginal", out var offset)
                    && DateTimeOffset.TryParse(time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + offset,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var zoned)) return zoned.UtcDateTime;
                return DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
            }
            var info = await engine.Probe(path, ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(info.RawJson);
            var containers = new List<JsonElement>();
            if (json.RootElement.TryGetProperty("format", out var format)) containers.Add(format);
            if (json.RootElement.TryGetProperty("streams", out var streams)) containers.AddRange(streams.EnumerateArray());
            foreach (var key in new[] { "com.apple.quicktime.creationdate", "creation_time" })
                foreach (var container in containers)
                    if (container.TryGetProperty("tags", out var tags) && tags.TryGetProperty(key, out var value)
                        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                        return time.UtcDateTime;
            return null;
        }
        catch (Exception error) when (error is IOException or MagickException or JsonException)
        { System.Diagnostics.Trace.TraceInformation("Capture metadata unavailable: {0}", error.Message); return null; }
    }
}
