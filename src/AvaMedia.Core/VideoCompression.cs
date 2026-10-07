using System.Globalization;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public enum VideoCompressionMode { Automatic, Quality, Bitrate, Percentage, TargetSize }
public enum VideoCompressionPreset { High, Balanced, Small }
public enum VideoEncodingSpeed { Fast, Balanced, Slow }

public sealed record VideoCompressionOptions
{
    public VideoCompressionMode Mode { get; init; }
    public VideoCompressionPreset Preset { get; init; } = VideoCompressionPreset.Balanced;
    public int Quality { get; init; } = 23;
    public int VideoBitrate { get; init; } = 4000;
    public VideoEncodingSpeed Speed { get; init; } = VideoEncodingSpeed.Balanced;
    public double Percentage { get; init; } = 60;
    public double TargetMegabytes { get; init; } = 50;
    public string Format { get; init; } = "mp4";
    public string Codec { get; init; } = "h264";
    public int MaxDimension { get; init; } = 1920;
    public int MaxFrameRate { get; init; }
    public int AudioBitrate { get; init; } = 128;
    public bool KeepAudio { get; init; } = true;
    public bool PreferGpu { get; init; } = true;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Preset) || !Enum.IsDefined(Speed))
            throw new ArgumentException("请选择有效的压缩模式、画质档位与速度。");
        if (Quality is < 1 or > 51) throw new ArgumentException("质量值须在 1–51 之间，数值越小画质越高。");
        if (VideoBitrate is < 64 or > 200000) throw new ArgumentException("视频码率须在 64–200000 kbps 之间。");
        if (!double.IsFinite(Percentage) || Percentage is < 5 or > 95)
            throw new ArgumentException("目标体积百分比须在 5–95 之间。");
        if (!double.IsFinite(TargetMegabytes) || TargetMegabytes is < .1 or > 1000000)
            throw new ArgumentException("目标体积须在 0.1–1000000 MB 之间。");
        if (Format is not ("mp4" or "mov" or "m4v" or "mkv") || Codec is not ("h264" or "hevc"))
            throw new ArgumentException("视频压缩支持 MP4 / MOV / M4V / MKV 和 H.264 / HEVC。");
        if (MaxDimension is not (0 or 1920 or 1280 or 854) || MaxFrameRate is not (0 or 30 or 24))
            throw new ArgumentException("请选择有效的分辨率与帧率上限。");
        if (AudioBitrate is not (64 or 96 or 128 or 192)) throw new ArgumentException("请选择有效的音频码率。");
    }
}

public sealed record VideoCompressionPlan(long SourceBytes, long? TargetBytes, long? EstimatedBytes,
    int VideoBitrate, int AudioBitrate, int Width, int Height, double FrameRate, bool QualityDriven, int Quality);

/// <summary>One size budget and encoder policy shared by the dialog and queued execution.</summary>
public static class VideoCompression
{
    public static bool UsesQuality(VideoCompressionMode mode) => mode == VideoCompressionMode.Quality;

    public static VideoCompressionOptions ApplyPreset(VideoCompressionOptions options, VideoCompressionPreset preset) => preset switch
    {
        VideoCompressionPreset.High => options with { Mode = VideoCompressionMode.Automatic, Preset = preset,
            Percentage = 85, MaxDimension = 0, MaxFrameRate = 0, AudioBitrate = 192, Speed = VideoEncodingSpeed.Slow },
        VideoCompressionPreset.Balanced => options with { Mode = VideoCompressionMode.Automatic, Preset = preset,
            Percentage = 70, MaxDimension = 1920, MaxFrameRate = 30, AudioBitrate = 128, Speed = VideoEncodingSpeed.Balanced },
        VideoCompressionPreset.Small => options with { Mode = VideoCompressionMode.Automatic, Preset = preset,
            Percentage = 50, MaxDimension = 1280, MaxFrameRate = 30, AudioBitrate = 96, Speed = VideoEncodingSpeed.Fast },
        _ => throw new ArgumentException("请选择有效的画质档位。")
    };

    public static VideoCompressionOptions Effective(VideoCompressionOptions options)
    {
        options.Validate();
        return options.Mode == VideoCompressionMode.Automatic ? ApplyPreset(options, options.Preset) : options;
    }

    public static VideoCompressionPlan Plan(long sourceBytes, MediaInfo source, VideoCompressionOptions options)
    {
        options = Effective(options);
        if (!source.HasVideo || !double.IsFinite(source.Duration) || source.Duration <= 0 || source.Width < 2 || source.Height < 2)
            throw new ArgumentException("源文件须包含有有效时长的视频画面。");
        if (sourceBytes <= 0) throw new ArgumentException("无法读取源视频体积。");
        var audio = source.HasAudio && options.KeepAudio ? options.AudioBitrate : 0;
        var factor = options.MaxDimension == 0 ? 1 : Math.Min(1, options.MaxDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(2, (int)Math.Floor(source.Width * factor / 2) * 2);
        var height = Math.Max(2, (int)Math.Floor(source.Height * factor / 2) * 2);
        var fps = options.MaxFrameRate > 0 && double.IsFinite(source.FrameRate) && source.FrameRate > 0
            ? Math.Min(options.MaxFrameRate, source.FrameRate) : 0;
        if (UsesQuality(options.Mode))
            return new(sourceBytes, null, null, 0, audio, width, height, fps, true, options.Quality);

        // Size and bitrate modes share one budget; quality modes never fabricate a size estimate.
        double? target = options.Mode switch
        {
            VideoCompressionMode.Automatic or VideoCompressionMode.Percentage => sourceBytes * options.Percentage / 100,
            VideoCompressionMode.TargetSize => options.TargetMegabytes * 1000000,
            _ => null
        };
        if (target >= sourceBytes) throw new ArgumentException("目标体积须小于源视频；请减小目标 MB 或改用百分比。");
        if (options.Mode == VideoCompressionMode.Automatic && audio > 0)
        {
            var totalBitrate = target!.Value * .97 * 8 / source.Duration / 1000;
            // Reserve most of a small budget for the picture, without silently removing sound.
            audio = new[] { 32, 48, 64, 96, 128, 192 }
                .LastOrDefault(rate => rate <= audio && rate <= totalBitrate * .25, 32);
        }
        var video = target is { } bytes ? Math.Floor(bytes * .97 * 8 / source.Duration / 1000 - audio) : options.VideoBitrate;
        if (video < 64) throw new ArgumentException("目标体积过小，无法分配视频码率；请增大目标或移除声音。");
        if (video > 200000) throw new ArgumentException("目标码率过高，请降低目标体积。");
        if (options.Mode == VideoCompressionMode.Automatic)
        {
            // A spatial budget heuristic avoids spreading very low bitrates over a full-HD frame.
            // This is a resolution policy, not a prediction of perceptual quality.
            var encodingFps = fps > 0 ? fps : double.IsFinite(source.FrameRate) && source.FrameRate > 0 ? source.FrameRate : 30;
            var bitsPerPixel = options.Codec == "hevc" ? .04 : .06;
            var scale = Math.Min(1, Math.Sqrt(video * 1000 / (width * (double)height * encodingFps * bitsPerPixel)));
            width = Math.Max(2, (int)Math.Floor(width * scale / 2) * 2);
            height = Math.Max(2, (int)Math.Floor(height * scale / 2) * 2);
        }
        return new(sourceBytes, target is { } size ? (long)Math.Floor(size) : null,
            (long)Math.Ceiling((video + audio) * 1000 / 8 * source.Duration / .97), (int)video, audio, width, height, fps, false, options.Quality);
    }

    public static ConversionOptions CreateOptions(VideoCompressionOptions options)
    {
        options = Effective(options);
        return new() { Format = options.Format, VideoCompression = options, AudioBitrate = options.AudioBitrate,
            Mute = !options.KeepAudio, KeepMetadata = false, AudioCodec = "aac" };
    }

    public static ConversionOptions Resolve(ConversionOptions options, MediaInfo source, VideoCompressionPlan plan)
    {
        var result = options.Clone();
        result.VideoCompression = Effective(result.VideoCompression ?? new());
        result.Width = plan.Width; result.Height = plan.Height; result.Fps = plan.FrameRate;
        result.VideoBitrate = plan.VideoBitrate; result.Mute = plan.AudioBitrate == 0;
        result.Quality = plan.Quality;
        result.AudioBitrate = plan.AudioBitrate;
        result.AudioCodec = "aac"; result.SampleRate = 48000;
        result.AudioChannels = source.AudioChannels > 2 ? 2 : 0;
        return result;
    }

    public static void ValidateJob(Job job)
    {
        var options = job.Options;
        var compression = options.VideoCompression ?? new();
        compression.Validate();
        if (job.Inputs.Length != 1 || job.InputOptions is not null)
            throw new ArgumentException("每个视频压缩任务处理一个视频。");
        if (options.Format != compression.Format || options.CopyStreams || options.PreserveSourceAttributes || options.LosslessRotation is not null ||
            options.VideoCodec == "copy" || options.Start != 0 || options.End != 0 || options.Speed != 1 ||
            options.KeepAllAudioStreams || SubtitleOptions.Mode(options) != SubtitleMode.None)
            throw new ArgumentException("视频压缩使用独立压缩参数；剪辑与轨道编辑请使用快速剪辑。");
    }

    public static string SoftwareEncoder(string codec, string listing, int width, int height)
    {
        var available = Regex.Matches(listing, @"(?m)^\s*V[A-Z\.]{5}\s+(\S+)")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var candidates = codec == "hevc" ? new[] { "libx265", "libkvazaar" } : ["libx264", "libopenh264"];
        var encoder = candidates.FirstOrDefault(available.Contains)
            ?? throw new InvalidOperationException("当前 FFmpeg 缺少所选压缩编码器；请选择 H.264 或安装对应编码器。");
        if (encoder == "libkvazaar" && (width % 8 != 0 || height % 8 != 0))
            throw new ArgumentException("当前 HEVC 软件编码器要求宽高为 8 的倍数；请选择 H.264 或使用可用的 GPU 编码。");
        return encoder;
    }

    public static IReadOnlyList<string> EncodingArguments(string codec, ConversionOptions options)
    {
        var spec = Effective(options.VideoCompression ?? new());
        var speed = spec.Speed switch { VideoEncodingSpeed.Fast => "fast", VideoEncodingSpeed.Slow => "slow", _ => "medium" };
        List<string> arguments = [];
        if (codec is "libx264" or "libx265" || codec.EndsWith("_qsv", StringComparison.Ordinal))
            arguments.AddRange(["-preset", speed]);
        else if (codec.EndsWith("_nvenc", StringComparison.Ordinal))
            arguments.AddRange(["-preset", spec.Speed switch { VideoEncodingSpeed.Fast => "p3", VideoEncodingSpeed.Slow => "p7", _ => "p5" }]);
        else if (codec.EndsWith("_amf", StringComparison.Ordinal))
            arguments.AddRange(["-quality", spec.Speed switch { VideoEncodingSpeed.Fast => "speed", VideoEncodingSpeed.Slow => "quality", _ => "balanced" }]);
        else if (codec.EndsWith("_videotoolbox", StringComparison.Ordinal))
            arguments.AddRange(["-allow_sw", "0", "-realtime", "0", "-prio_speed", spec.Speed == VideoEncodingSpeed.Fast ? "1" : "0"]);

        if (UsesQuality(spec.Mode))
        {
            var quality = spec.Quality.ToString(CultureInfo.InvariantCulture);
            if (codec is "libx264" or "libx265") arguments.AddRange(["-crf", quality]);
            else if (codec == "libopenh264") arguments.AddRange(["-rc_mode", "quality", "-qmin", quality, "-qmax", quality]);
            else if (codec == "libkvazaar") arguments.AddRange(["-b:v", "0", "-kvazaar-params", "preset=" + speed + ",qp=" + quality]);
            else if (codec.EndsWith("_nvenc", StringComparison.Ordinal)) arguments.AddRange(["-tune", "hq", "-rc", "vbr", "-cq", quality, "-b:v", "0"]);
            else if (codec.EndsWith("_qsv", StringComparison.Ordinal)) arguments.AddRange(["-q:v", quality]);
            else if (codec.EndsWith("_amf", StringComparison.Ordinal)) arguments.AddRange(["-rc", "cqp", "-qp_i", quality, "-qp_p", quality]);
            else if (codec.EndsWith("_videotoolbox", StringComparison.Ordinal)) arguments.AddRange(["-q:v", MediaEngine.Number(100 - (spec.Quality - 1) * 99d / 50)]);
            else throw new ArgumentException("当前编码器不支持所选画质控制。");
            return arguments;
        }
        var bitrate = options.VideoBitrate;
        if (bitrate is < 64 or > 200000) throw new ArgumentException("压缩视频码率无效。");
        var rate = bitrate.ToString(CultureInfo.InvariantCulture) + "k";
        arguments.AddRange(["-b:v", rate, "-maxrate", rate, "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture) + "k"]);
        if (codec.EndsWith("_nvenc", StringComparison.Ordinal)) arguments.AddRange(["-rc", "vbr"]);
        else if (codec.EndsWith("_amf", StringComparison.Ordinal)) arguments.AddRange(["-rc", "vbr_peak"]);
        else if (codec == "libkvazaar") arguments.AddRange(["-kvazaar-params", "preset=" + speed]);
        else if (codec == "libopenh264") arguments.AddRange(["-rc_mode", "bitrate"]);
        return arguments;
    }

    public static IReadOnlyList<Job> CreateJobs(IReadOnlyList<string> files, string folder, VideoCompressionOptions options,
        IEnumerable<string>? reserved = null) => ConversionBatch.CreateJobs(Catalog.Find("video-compress"), files, folder, CreateOptions(options), reserved: reserved);
}
