using System.Collections.Frozen;

namespace AvaMedia.Core;

/// <summary>Shared input classification; export support is a separate container decision.</summary>
public static class VideoFormats
{
    public static IReadOnlySet<string> InputExtensions { get; } = new[]
    {
        "mp4", "mkv", "mov", "m4v", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "ts", "mts", "m2ts", "m2t", "mxf",
        "3gp", "3g2", "3gpp", "3gpp2", "rm", "rmvb", "asf", "divx", "f4v", "ogv", "qt",
        "vob", "vro", "dat", "mpe", "m1v", "m2v", "mod", "tod", "dv", "mjpeg", "mjpg", "amv", "nsv", "fli", "flc"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlySet<string> OriginalOutputExtensions { get; } = new[]
    {
        "mp4", "mkv", "mov", "webm", "avi", "flv", "wmv", "mpg", "mpeg", "ts", "mts", "m2ts", "m2t", "m4v", "vob", "3gp", "3g2", "ogv", "asf"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public static bool IsVideo(string path) => InputExtensions.Contains(Path.GetExtension(path).TrimStart('.'));
    public static bool IsTransportStream(string format) => format is "ts" or "mts" or "m2ts" or "m2t";
    internal static void AppendMuxerArguments(List<string> arguments, string format)
    {
        if (IsTransportStream(format)) arguments.AddRange(["-f", "mpegts"]);
        else if (format == "m4v") arguments.AddRange(["-f", "mp4"]);
        else if (format == "wav") arguments.AddRange(["-rf64", "auto"]);
    }
    public static void ValidateTransportOutput(ConversionOptions options)
    {
        if (!IsTransportStream(options.Format) || options.CopyStreams || options.PreserveSourceAttributes) return;
        if (options.VideoCodec is not ("自动" or "copy" or "mpeg2video" or "mpeg4" or "libx264" or "libx265" or "libopenh264" or "libkvazaar" or "h264_mf") &&
            HardwareTranscoding.Encoder(options.VideoCodec)?.Format is not (HardwareVideoFormat.H264 or HardwareVideoFormat.Hevc))
            throw new ArgumentException("TS 输出请选择 H.264、HEVC、MPEG-2 或 MPEG-4 编码。");
        if (!options.Mute && options.AudioCodec is not ("自动" or "copy" or "aac" or "mp2" or "libmp3lame" or "ac3" or "eac3"))
            throw new ArgumentException("TS 音频请选择 AAC、MP2、MP3、AC3 或 EAC3 编码。");
    }
    public static bool IsMobileContainer(string format) => format is "3gp" or "3g2";
    public static void ValidateMobileOutput(ConversionOptions options)
    {
        if (!IsMobileContainer(options.Format) || options.CopyStreams) return;
        if (options.VideoCodec is not ("自动" or "copy" or "mpeg4" or "libx264" or "h264_mf") &&
            HardwareTranscoding.Encoder(options.VideoCodec)?.Format != HardwareVideoFormat.H264)
            throw new ArgumentException("3GP / 3G2 视频请选择 MPEG-4 或 H.264 编码。");
        if (!options.Mute && options.AudioCodec is not ("自动" or "copy" or "aac"))
            throw new ArgumentException("3GP / 3G2 音频请选择 AAC 编码或复制兼容音轨。");
    }
}
