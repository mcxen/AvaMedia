using System.Runtime.InteropServices;
using System.Text.Json;

namespace AvaMedia.Core;

public enum HardwareBackendKind { VideoToolbox, Nvenc, QuickSync, Amf }
public enum HardwareVideoFormat { H264, Hevc, Av1, Vp9 }

public sealed record HardwareEncodingContext(int Quality, int Width, int Height, double FrameRate);
public sealed record HardwareDecodePlan(string Method, IReadOnlyList<string> InputArguments, string? EncoderPixelFormat = null);

public interface IHardwareTranscodingBackend
{
    HardwareBackendKind Kind { get; }
    string Name { get; }
    bool IsAvailableOnPlatform { get; }
    IReadOnlyList<HardwareEncoder> Encoders { get; }
    IReadOnlyList<string> InitializationArguments { get; }
    IReadOnlyList<string> EncodingArguments(HardwareEncoder encoder, HardwareEncodingContext context);
    HardwareDecodePlan? Decoding(MediaInfo input, bool keepHardwareFrames);
}

public static class HardwareTranscoding
{
    public static IReadOnlyList<IHardwareTranscodingBackend> Backends { get; } =
        Array.AsReadOnly<IHardwareTranscodingBackend>([new VideoToolboxBackend(), new NvencBackend(), new AmfBackend(), new QuickSyncBackend()]);

    public static IHardwareTranscodingBackend? Backend(string codec) =>
        Backends.FirstOrDefault(backend => backend.Encoders.Any(encoder => encoder.Codec == codec));

    public static HardwareEncoder? Encoder(string codec) => Backend(codec)?.Encoders.First(encoder => encoder.Codec == codec);

    public static HardwareVideoFormat? VideoFormat(string sourceCodec) => sourceCodec.ToLowerInvariant() switch
    {
        "h264" => HardwareVideoFormat.H264,
        "hevc" or "h265" => HardwareVideoFormat.Hevc,
        "av1" => HardwareVideoFormat.Av1,
        "vp9" => HardwareVideoFormat.Vp9,
        _ => null
    };

    public static bool Compatible(string container, HardwareVideoFormat format) => container.ToLowerInvariant() switch
    {
        "mp4" or "mov" or "m4v" => format is HardwareVideoFormat.H264 or HardwareVideoFormat.Hevc or HardwareVideoFormat.Av1,
        "ts" or "mts" or "m2ts" or "m2t" => format is HardwareVideoFormat.H264 or HardwareVideoFormat.Hevc,
        "mkv" => true,
        "avi" or "flv" => format == HardwareVideoFormat.H264,
        "3gp" or "3g2" => format == HardwareVideoFormat.H264,
        "webm" => format is HardwareVideoFormat.Av1 or HardwareVideoFormat.Vp9,
        _ => false
    };

    public static HardwareEncodingContext Context(ConversionOptions options, IReadOnlyList<MediaInfo> inputs)
    {
        var input = inputs.FirstOrDefault(info => info.HasVideo);
        return new(options.Quality, options.Width > 0 ? options.Width : input?.Width ?? 1920,
            options.Height > 0 ? options.Height : input?.Height ?? 1080,
            options.Fps > 0 ? options.Fps : input?.FrameRate > 0 ? input.FrameRate : 30);
    }

    public static IReadOnlyDictionary<int, HardwareDecodePlan> DecodePlans(Job job, IReadOnlyList<MediaInfo> inputs)
    {
        var backend = Backend(job.Options.VideoCodec);
        if (backend is null || !backend.IsAvailableOnPlatform || job.Options.CopyStreams || job.Options.PreserveSourceAttributes ||
            job.Options.LosslessRotation is not null)
            return new Dictionary<int, HardwareDecodePlan>();

        var operation = Catalog.Find(job.FeatureId).Operation;
        var plans = new Dictionary<int, HardwareDecodePlan>();
        for (var index = 0; index < inputs.Count; index++)
        {
            if (!inputs[index].HasVideo || operation == Operation.Mux && index != 0) continue;
            var keepFrames = inputs.Count == 1 && job.InputOptions is null && operation != Operation.Join &&
                !MediaEngine.HasVideoFilters(job.Options) && IsUnrotatedEightBitInput(inputs[index]);
            if (backend.Decoding(inputs[index], keepFrames) is { } plan) plans[index] = plan;
        }
        return plans;
    }

    private static bool IsUnrotatedEightBitInput(MediaInfo input)
    {
        using var json = JsonDocument.Parse(input.RawJson);
        if (!json.RootElement.TryGetProperty("streams", out var streams)) return false;
        var video = streams.EnumerateArray().Where(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
            .Skip(input.VideoStreamIndex).FirstOrDefault();
        if (video.ValueKind != JsonValueKind.Object || !video.TryGetProperty("pix_fmt", out var pixels) ||
            pixels.GetString() is not ("yuv420p" or "nv12")) return false;
        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotation) &&
            double.TryParse(rotation.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var angle) && angle != 0)
            return false;
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray().Any(data =>
                data.TryGetProperty("rotation", out var value) && value.TryGetDouble(out var angle) && angle != 0)) return false;
        return true;
    }
}

internal abstract class HardwareTranscodingBackend : IHardwareTranscodingBackend
{
    public abstract HardwareBackendKind Kind { get; }
    public abstract string Name { get; }
    public abstract bool IsAvailableOnPlatform { get; }
    public abstract IReadOnlyList<HardwareEncoder> Encoders { get; }
    public virtual IReadOnlyList<string> InitializationArguments => [];
    public abstract IReadOnlyList<string> EncodingArguments(HardwareEncoder encoder, HardwareEncodingContext context);
    public abstract HardwareDecodePlan? Decoding(MediaInfo input, bool keepHardwareFrames);
    protected static string Qp(int quality, int maximum = 51) => Math.Clamp(quality, 1, maximum).ToString(System.Globalization.CultureInfo.InvariantCulture);
    protected static string TransferFormat(MediaInfo input)
    {
        using var json = JsonDocument.Parse(input.RawJson);
        var video = json.RootElement.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "video")
            .Skip(input.VideoStreamIndex).FirstOrDefault();
        // Hardware frame downloads keep the source bit depth; conversion belongs to the existing filter/encoder path.
        var format = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("pix_fmt", out var pixels) ? pixels.GetString() ?? "" : "";
        return format.Contains("10", StringComparison.Ordinal) || format.StartsWith("p010", StringComparison.Ordinal) ? "p010le" : "nv12";
    }
    protected static IReadOnlyList<string> DecodeArguments(string method, MediaInfo input, string pixels) =>
        ["-hwaccel:v:" + input.VideoStreamIndex, method, "-hwaccel_output_format:v:" + input.VideoStreamIndex, pixels];
}

internal sealed class VideoToolboxBackend : HardwareTranscodingBackend
{
    public override HardwareBackendKind Kind => HardwareBackendKind.VideoToolbox;
    public override string Name => "Apple VideoToolbox";
    public override bool IsAvailableOnPlatform => OperatingSystem.IsMacOS();
    public override IReadOnlyList<HardwareEncoder> Encoders { get; } = Array.AsReadOnly<HardwareEncoder>(
        [new("Apple H.264", "h264_videotoolbox", HardwareVideoFormat.H264), new("Apple HEVC", "hevc_videotoolbox", HardwareVideoFormat.Hevc)]);
    public override IReadOnlyList<string> EncodingArguments(HardwareEncoder encoder, HardwareEncodingContext context)
    {
        List<string> arguments = ["-allow_sw", "0", "-realtime", "0"];
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            arguments.AddRange(["-q:v", MediaEngine.Number(100 - (Math.Clamp(context.Quality, 1, 63) - 1) * 99d / 62)]);
        else
        {
            // FFmpeg's VideoToolbox constant-quality mode is available only on Apple Silicon.
            var bits = context.Width * (double)context.Height * context.FrameRate * (0.04 + (64 - Math.Clamp(context.Quality, 1, 63)) / 63d * 0.20);
            if (encoder.Format == HardwareVideoFormat.Hevc) bits *= 0.65;
            arguments.AddRange(["-b:v", Math.Max(256000, (long)bits).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        return arguments;
    }
    public override HardwareDecodePlan? Decoding(MediaInfo input, bool keepHardwareFrames) => HardwareTranscoding.VideoFormat(input.VideoCodec) is null
        ? null : new("videotoolbox", DecodeArguments("videotoolbox", input, TransferFormat(input)));
}

internal sealed class NvencBackend : HardwareTranscodingBackend
{
    public override HardwareBackendKind Kind => HardwareBackendKind.Nvenc;
    public override string Name => "NVIDIA NVENC";
    public override bool IsAvailableOnPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public override IReadOnlyList<HardwareEncoder> Encoders { get; } = Array.AsReadOnly<HardwareEncoder>(
        [new("NVIDIA H.264", "h264_nvenc", HardwareVideoFormat.H264), new("NVIDIA HEVC", "hevc_nvenc", HardwareVideoFormat.Hevc), new("NVIDIA AV1", "av1_nvenc", HardwareVideoFormat.Av1)]);
    public override IReadOnlyList<string> EncodingArguments(HardwareEncoder encoder, HardwareEncodingContext context) =>
        ["-preset", "p4", "-tune", "hq", "-rc", "vbr",
            "-cq", Qp(context.Quality, encoder.Format == HardwareVideoFormat.Av1 ? 63 : 51), "-b:v", "0"];
    public override HardwareDecodePlan? Decoding(MediaInfo input, bool keepHardwareFrames) => HardwareTranscoding.VideoFormat(input.VideoCodec) is null
        ? null : new("cuda", DecodeArguments("cuda", input, keepHardwareFrames ? "cuda" : TransferFormat(input)), keepHardwareFrames ? "cuda" : null);
}

internal sealed class QuickSyncBackend : HardwareTranscodingBackend
{
    public override HardwareBackendKind Kind => HardwareBackendKind.QuickSync;
    public override string Name => "Intel Quick Sync";
    public override bool IsAvailableOnPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public override IReadOnlyList<string> InitializationArguments => ["-init_hw_device", "qsv=avamedia_qsv:hw_any"];
    public override IReadOnlyList<HardwareEncoder> Encoders { get; } = Array.AsReadOnly<HardwareEncoder>(
        [new("Intel H.264", "h264_qsv", HardwareVideoFormat.H264), new("Intel HEVC", "hevc_qsv", HardwareVideoFormat.Hevc),
            new("Intel AV1", "av1_qsv", HardwareVideoFormat.Av1), new("Intel VP9", "vp9_qsv", HardwareVideoFormat.Vp9)]);
    public override IReadOnlyList<string> EncodingArguments(HardwareEncoder encoder, HardwareEncodingContext context)
    {
        var quality = encoder.Format == HardwareVideoFormat.Av1 ? Qp((int)Math.Round(Math.Clamp(context.Quality, 1, 63) * 255d / 63), 255) : Qp(context.Quality);
        // -q:v sets both QSCALE and FF_QP2LAMBDA units, which QSV requires for CQP.
        return ["-preset", "medium", "-q:v", quality];
    }
    public override HardwareDecodePlan? Decoding(MediaInfo input, bool keepHardwareFrames)
    {
        var format = HardwareTranscoding.VideoFormat(input.VideoCodec);
        if (format is null) return null;
        var decoder = format switch { HardwareVideoFormat.H264 => "h264_qsv", HardwareVideoFormat.Hevc => "hevc_qsv", HardwareVideoFormat.Av1 => "av1_qsv", _ => "vp9_qsv" };
        return new("qsv", [..DecodeArguments("qsv", input, keepHardwareFrames ? "qsv" : TransferFormat(input)),
            "-hwaccel_device:v:" + input.VideoStreamIndex, "avamedia_qsv", "-c:v:" + input.VideoStreamIndex, decoder], keepHardwareFrames ? "qsv" : null);
    }
}

internal sealed class AmfBackend : HardwareTranscodingBackend
{
    public override HardwareBackendKind Kind => HardwareBackendKind.Amf;
    public override string Name => "AMD AMF";
    public override bool IsAvailableOnPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public override IReadOnlyList<HardwareEncoder> Encoders { get; } = Array.AsReadOnly<HardwareEncoder>(
        [new("AMD H.264", "h264_amf", HardwareVideoFormat.H264), new("AMD HEVC", "hevc_amf", HardwareVideoFormat.Hevc), new("AMD AV1", "av1_amf", HardwareVideoFormat.Av1)]);
    public override IReadOnlyList<string> EncodingArguments(HardwareEncoder encoder, HardwareEncodingContext context)
    {
        var quality = encoder.Format == HardwareVideoFormat.Av1 ? Qp((int)Math.Round(Math.Clamp(context.Quality, 1, 63) * 255d / 63), 255) : Qp(context.Quality);
        return ["-quality", "balanced", "-rc", "cqp", "-qp_i", quality, "-qp_p", quality];
    }
    public override HardwareDecodePlan? Decoding(MediaInfo input, bool keepHardwareFrames) =>
        !OperatingSystem.IsWindows() || HardwareTranscoding.VideoFormat(input.VideoCodec) is null
            ? null : new("d3d11va", DecodeArguments("d3d11va", input, TransferFormat(input)));
}
