using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Hardware encoding that keeps the source codec, bit depth, chroma and copied tracks.</summary>
public static class SourceVideoGpu
{
    public static bool IsHardware(string codec) => codec.EndsWith("_nvenc", StringComparison.Ordinal) ||
        codec.EndsWith("_qsv", StringComparison.Ordinal) || codec.EndsWith("_amf", StringComparison.Ordinal) ||
        codec.EndsWith("_videotoolbox", StringComparison.Ordinal);

    public static bool CanEncode(string codec, MediaInfo media, int videoStreamIndex)
    {
        var family = media.VideoCodec.ToLowerInvariant() is "h265" ? "hevc" : media.VideoCodec.ToLowerInvariant();
        if (!IsHardware(codec) || !codec.StartsWith(family + "_", StringComparison.Ordinal)) return false;
        using var json = JsonDocument.Parse(media.RawJson);
        var video = Video(json.RootElement, videoStreamIndex);
        return video.TryGetProperty("pix_fmt", out var pixels) && PixelFormat(pixels.GetString()) is not null;
    }

    public static List<string> BuildArguments(Job job, IReadOnlyList<MediaInfo> infos)
    {
        var arguments = SourceVideoExport.BuildArguments(job, infos);
        var options = job.Options;
        var codec = options.VideoCodec;
        if (!options.PreserveSourceAttributes || !IsHardware(codec)) return arguments;
        if (!CanEncode(codec, infos[0], options.VideoStreamIndex))
            throw new ArgumentException("此硬件编码器不能保留源视频的编码、位深和色度采样。");

        using var json = JsonDocument.Parse(infos[0].RawJson);
        var video = Video(json.RootElement, options.VideoStreamIndex);
        var pixels = video.GetProperty("pix_fmt").GetString();
        var outputPixels = PixelFormat(pixels)!;
        var stream = ":v:" + options.VideoStreamIndex;
        var fullRange = pixels == "yuvj420p" || video.TryGetProperty("color_range", out var range) && range.GetString() == "pc";
        // Explicit swscale is required: strict +pix_fmt disables FFmpeg's automatic converters.
        // NV12/P010 change memory layout while retaining 4:2:0 samples and 8/10-bit depth.
        var conversion = "scale=iw:ih" + (fullRange ? ":in_range=full:out_range=full" : "") + ",format=" + outputPixels;
        var filterIndex = arguments.IndexOf("-filter" + stream);
        if (filterIndex >= 0) arguments[filterIndex + 1] += "," + conversion;
        else Add(["-filter" + stream, conversion]);
        var pixelIndex = arguments.IndexOf("-pix_fmt" + stream);
        if (pixelIndex >= 0) arguments[pixelIndex + 1] = "+" + outputPixels;
        else Add(["-pix_fmt" + stream, "+" + outputPixels]);
        if (fullRange)
        {
            var rangeIndex = arguments.IndexOf("-color_range" + stream);
            if (rangeIndex >= 0) arguments[rangeIndex + 1] = "pc";
            else Add(["-color_range" + stream, "pc"]);
        }

        var quality = Math.Clamp(options.Quality, 1, 51).ToString(CultureInfo.InvariantCulture);
        if (codec.EndsWith("_nvenc", StringComparison.Ordinal))
        {
            Add(["-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", quality]);
            if (!arguments.Contains("-b" + stream)) Add(["-b" + stream, "0"]);
        }
        else if (codec.EndsWith("_qsv", StringComparison.Ordinal))
        {
            arguments.InsertRange(arguments.IndexOf("-i"), ["-init_hw_device", "qsv=avamedia_qsv:hw_any"]);
            var qsvQuality = codec.StartsWith("av1_", StringComparison.Ordinal)
                ? Math.Clamp((int)Math.Round(Math.Clamp(options.Quality, 1, 63) * 255d / 63), 1, 255).ToString(CultureInfo.InvariantCulture) : quality;
            Add(["-preset", "medium", "-q" + stream, qsvQuality]);
        }
        else if (codec.EndsWith("_amf", StringComparison.Ordinal))
        {
            var amfQuality = codec.StartsWith("av1_", StringComparison.Ordinal)
                ? Math.Clamp((int)Math.Round(Math.Clamp(options.Quality, 1, 63) * 255d / 63), 1, 255).ToString(CultureInfo.InvariantCulture) : quality;
            Add(["-quality", "balanced", "-rc", "cqp", "-qp_i", amfQuality, "-qp_p", amfQuality]);
        }
        else
        {
            Add(["-allow_sw", "0", "-realtime", "0"]);
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                Add(["-q" + stream, MediaEngine.Number(100 - (Math.Clamp(options.Quality, 1, 63) - 1) * 99d / 62)]);
            else if (!arguments.Contains("-b" + stream)) Add(["-b" + stream, "4000000"]);
        }
        return arguments;

        void Add(IEnumerable<string> values) => arguments.InsertRange(arguments.Count - 1, values);
    }

    private static string? PixelFormat(string? pixels) => pixels switch
    {
        "yuv420p" or "yuvj420p" or "nv12" => "nv12",
        "yuv420p10le" or "p010le" => "p010le",
        _ => null
    };

    private static JsonElement Video(JsonElement root, int index) => root.GetProperty("streams").EnumerateArray()
        .Where(stream => stream.GetProperty("codec_type").GetString() == "video").ElementAt(index);
}
