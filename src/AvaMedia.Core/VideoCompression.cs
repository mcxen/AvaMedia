using System.Globalization;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public enum VideoCompressionMode { Percentage, TargetSize }

public sealed record VideoCompressionOptions
{
    public VideoCompressionMode Mode { get; init; }
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
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("请选择压缩目标模式。");
        if (!double.IsFinite(Percentage) || Percentage is < 5 or > 95)
            throw new ArgumentException("目标体积百分比须在 5–95 之间。");
        if (!double.IsFinite(TargetMegabytes) || TargetMegabytes is < .1 or > 1000000)
            throw new ArgumentException("目标体积须在 0.1–1000000 MB 之间。");
        if (Format is not ("mp4" or "mkv") || Codec is not ("h264" or "hevc"))
            throw new ArgumentException("视频压缩支持 MP4 / MKV 和 H.264 / HEVC。");
        if (MaxDimension is not (0 or 1920 or 1280 or 854) || MaxFrameRate is not (0 or 30 or 24))
            throw new ArgumentException("请选择有效的分辨率与帧率上限。");
        if (AudioBitrate is not (64 or 96 or 128 or 192)) throw new ArgumentException("请选择有效的音频码率。");
    }
}

public sealed record VideoCompressionPlan(long SourceBytes, long TargetBytes, long EstimatedBytes,
    int VideoBitrate, int AudioBitrate, int Width, int Height, double FrameRate);

/// <summary>One size budget and encoder policy shared by the dialog and queued execution.</summary>
public static class VideoCompression
{
    public static VideoCompressionPlan Plan(long sourceBytes, MediaInfo source, VideoCompressionOptions options)
    {
        options.Validate();
        if (!source.HasVideo || !double.IsFinite(source.Duration) || source.Duration <= 0 || source.Width < 2 || source.Height < 2)
            throw new ArgumentException("源文件须包含有有效时长的视频画面。");
        if (sourceBytes <= 0) throw new ArgumentException("无法读取源视频体积。");
        var target = options.Mode == VideoCompressionMode.Percentage
            ? sourceBytes * options.Percentage / 100 : options.TargetMegabytes * 1000000;
        if (target >= sourceBytes) throw new ArgumentException("目标体积须小于源视频；请减小目标 MB 或改用百分比。");
        // Reserve 3% for container overhead. The UI calls this an estimate, not an exact size guarantee.
        var audio = source.HasAudio && options.KeepAudio ? options.AudioBitrate : 0;
        var video = Math.Floor(target * .97 * 8 / source.Duration / 1000 - audio);
        if (video < 64) throw new ArgumentException("目标体积过小，无法分配视频码率；请增大目标或移除声音。");
        if (video > 200000) throw new ArgumentException("目标码率过高，请降低目标体积。");
        var factor = options.MaxDimension == 0 ? 1 : Math.Min(1, options.MaxDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(2, (int)Math.Floor(source.Width * factor / 2) * 2);
        var height = Math.Max(2, (int)Math.Floor(source.Height * factor / 2) * 2);
        var fps = options.MaxFrameRate > 0 && double.IsFinite(source.FrameRate) && source.FrameRate > 0
            ? Math.Min(options.MaxFrameRate, source.FrameRate) : 0;
        return new(sourceBytes, (long)Math.Floor(target),
            (long)Math.Ceiling((video + audio) * 1000 / 8 * source.Duration / .97), (int)video, audio, width, height, fps);
    }

    public static ConversionOptions CreateOptions(VideoCompressionOptions options)
    {
        options.Validate();
        return new() { Format = options.Format, VideoCompression = options, AudioBitrate = options.AudioBitrate,
            Mute = !options.KeepAudio, KeepMetadata = false, AudioCodec = "aac" };
    }

    public static ConversionOptions Resolve(ConversionOptions options, MediaInfo source, VideoCompressionPlan plan)
    {
        var result = options.Clone();
        result.VideoCompression ??= new();
        result.Width = plan.Width; result.Height = plan.Height; result.Fps = plan.FrameRate;
        result.VideoBitrate = plan.VideoBitrate; result.Mute = plan.AudioBitrate == 0;
        result.AudioBitrate = result.VideoCompression.AudioBitrate;
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

    public static string SoftwareEncoder(string codec, string listing)
    {
        var available = Regex.Matches(listing, @"(?m)^\s*V[A-Z\.]{5}\s+(\S+)")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var candidates = codec == "hevc" ? new[] { "libx265", "libkvazaar" } : ["libx264", "libopenh264"];
        return candidates.FirstOrDefault(available.Contains)
            ?? throw new InvalidOperationException("当前 FFmpeg 缺少所选压缩编码器；请选择 H.264 或安装对应编码器。");
    }

    public static IReadOnlyList<string> EncodingArguments(string codec, int bitrate)
    {
        if (bitrate is < 64 or > 200000) throw new ArgumentException("压缩视频码率无效。");
        var rate = bitrate.ToString(CultureInfo.InvariantCulture) + "k";
        List<string> arguments = ["-b:v", rate, "-maxrate", rate, "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture) + "k"];
        if (codec.EndsWith("_nvenc", StringComparison.Ordinal)) arguments.AddRange(["-preset", "p5", "-rc", "vbr"]);
        else if (codec.EndsWith("_amf", StringComparison.Ordinal)) arguments.AddRange(["-quality", "balanced", "-rc", "vbr_peak"]);
        else if (codec.EndsWith("_qsv", StringComparison.Ordinal)) arguments.AddRange(["-preset", "medium"]);
        else if (codec.EndsWith("_videotoolbox", StringComparison.Ordinal)) arguments.AddRange(["-allow_sw", "0", "-realtime", "0"]);
        else if (codec is "libx264" or "libx265") arguments.AddRange(["-preset", "medium"]);
        else if (codec == "libopenh264") arguments.AddRange(["-rc_mode", "bitrate"]);
        return arguments;
    }

    public static IReadOnlyList<Job> CreateJobs(IReadOnlyList<string> files, string folder, VideoCompressionOptions options,
        IEnumerable<string>? reserved = null) => ConversionBatch.CreateJobs(Catalog.Find("video-compress"), files, folder, CreateOptions(options), reserved: reserved);
}
