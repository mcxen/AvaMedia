using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public enum VideoRateMode { Source, Quality, Bitrate }

/// <summary>Shared rate control for video processing, including edited and joined AI clips.</summary>
public static class VideoEncoding
{
    public static void Validate(ConversionOptions options)
    {
        if (!Enum.IsDefined(options.VideoRateMode)) throw new ArgumentException("请选择有效的视频码率控制方式。");
        if (options.VideoRateMode == VideoRateMode.Bitrate && options.VideoBitrate is < 1 or > 200000)
            throw new ArgumentException("视频码率须在 1–200000 kbps 之间。");
    }

    public static long? TargetBitrate(Job job, IReadOnlyList<MediaInfo> inputs)
    {
        var options = job.Options;
        if (options.VideoRateMode == VideoRateMode.Quality) return null;
        if (options.VideoRateMode == VideoRateMode.Bitrate) { Validate(options); return options.VideoBitrate * 1000L; }
        var sources = Sources(job, inputs, video: true);
        double bits = 0, duration = 0;
        foreach (var (info, rates, weight) in sources)
        {
            if (!info.HasVideo) continue;
            if (rates.Video <= 0) throw new ArgumentException("无法读取源视频码率，请在输出配置中选择自定义码率。");
            bits += rates.Video * weight; duration += weight;
        }
        return duration > 0 ? Rate(bits / duration) : null;
    }

    public static IReadOnlyList<long> AudioBitrates(Job job, IReadOnlyList<MediaInfo> inputs, string codec)
    {
        var options = job.Options;
        if (options.VideoRateMode != VideoRateMode.Source || options.VideoCompression is not null || options.Mute ||
            MediaEngine.IsAudio(options.Format) || MediaEngine.IsImage(options.Format) || options.Format == "gif") return [];
        var sources = Sources(job, inputs, video: false).ToArray();
        var all = options.KeepAllAudioStreams;
        var count = all ? sources.Select(source => source.rates.Audio.Count).DefaultIfEmpty().Max() : 1;
        var result = new List<long>();
        for (var index = 0; index < count; index++)
        {
            double bits = 0, duration = 0;
            foreach (var (info, rates, weight) in sources)
            {
                var rate = rates.Audio.ElementAtOrDefault(all ? index : info.AudioStreamIndex);
                if (rate <= 0) continue;
                bits += rate * weight; duration += weight;
            }
            var target = duration > 0 ? Math.Min(options.AudioBitrate * 1000L, Rate(bits / duration)) : options.AudioBitrate * 1000L;
            // MPEG/AC3 audio uses discrete legal rates; arbitrary source averages are not valid settings.
            int[] legal = codec switch
            {
                "mp2" => [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
                "ac3" => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640],
                _ => []
            };
            if (legal.Length > 0) target = legal.LastOrDefault(rate => rate * 1000L <= target, legal[0]) * 1000L;
            if (codec == "libopus") target = Math.Clamp(target, 6000, 510000);
            result.Add(target);
        }
        return result;
    }

    private static IEnumerable<(MediaInfo info, SourceRates rates, double weight)> Sources(Job job, IReadOnlyList<MediaInfo> inputs, bool video)
    {
        var operation = Catalog.Find(job.FeatureId).Operation;
        var joined = operation == Operation.Join;
        var first = !video && operation == Operation.Mux ? 1 : 0;
        var count = joined ? inputs.Count : Math.Min(inputs.Count, first + 1);
        var cache = new Dictionary<(string, int), SourceRates>();
        for (var index = first; index < count; index++)
        {
            var info = inputs[index];
            var edit = job.InputOptions?.ElementAtOrDefault(index) ?? (joined ? new ConversionOptions() : job.Options);
            if (!video && edit.Mute) continue;
            var key = (job.Inputs[index], info.VideoStreamIndex);
            if (!cache.TryGetValue(key, out var rates)) cache[key] = rates = Inspect(info, key.Item1);
            var duration = MediaFilters.Duration(info, edit);
            yield return (info, rates, double.IsFinite(duration) && duration > 0 ? duration : 1);
        }
    }

    private sealed record SourceRates(long Video, IReadOnlyList<long> Audio);
    private static SourceRates Inspect(MediaInfo info, string path)
    {
        using var json = JsonDocument.Parse(info.RawJson);
        var root = json.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var videos = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "video").ToArray();
        var audios = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
        var video = StreamRate(videos.ElementAtOrDefault(info.VideoStreamIndex));
        var total = 0d;
        if (root.TryGetProperty("format", out var format))
        {
            // Container rates and Matroska BPS tags can be absent or stale after clipping.
            // The complete input's byte count and duration give a safe average-rate fallback.
            var bytes = Number(format, "size");
            if (bytes <= 0 && info.Duration > 0 && File.Exists(path)) bytes = new FileInfo(path).Length;
            total = bytes > 0 && info.Duration > 0 ? bytes * 8 / info.Duration : Number(format, "bit_rate");
        }
        var audio = audios.Select(StreamRate).Select(rate => rate > 0 ? rate :
            (total > 0 ? total * (info.HasVideo ? .15 : 1) : video * .15) / Math.Max(1, audios.Length)).ToArray();
        if (video <= 0 && total > 0 && info.HasVideo)
            video = Math.Max(0, total - audio.Sum()) / Math.Max(1, videos.Length);
        var combined = video + audio.Sum();
        var scale = total > 0 && combined > total ? total / combined : 1;
        return new(video > 0 ? Rate(video * scale) : 0, audio.Select(rate => rate > 0 ? Rate(rate * scale) : 0).ToArray());
    }
    private static double StreamRate(JsonElement stream)
    {
        if (stream.ValueKind != JsonValueKind.Object) return 0;
        var rate = Number(stream, "bit_rate");
        if (rate > 0) return rate;
        if (!stream.TryGetProperty("tags", out var tags)) return 0;
        foreach (var tag in tags.EnumerateObject())
            if (tag.Name.Equals("BPS", StringComparison.OrdinalIgnoreCase) || tag.Name.Equals("BPS-eng", StringComparison.OrdinalIgnoreCase))
                if (double.TryParse(tag.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out rate) && double.IsFinite(rate) && rate > 0) return rate;
        return 0;
    }
    private static double Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number > 0 ? number : 0;
    private static long Rate(double bits) => (long)Math.Clamp(Math.Floor(bits), 1, 2_000_000_000);

    public static string SoftwareEncoder(string format, string sourceCodec, string listing)
    {
        var available = Regex.Matches(listing, @"(?m)^\s*V[A-Z\.]{5}\s+(\S+)").Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        string[] source = sourceCodec switch
        {
            "h264" => ["libx264", "libopenh264"], "hevc" => ["libx265", "libkvazaar"],
            "av1" => ["libaom-av1"], "vp9" => ["libvpx-vp9"], "mpeg4" => ["mpeg4"], _ => []
        };
        string[] candidates = format switch
        {
            "wmv" or "asf" => ["wmv2"], "mpg" or "mpeg" or "vob" => ["mpeg2video"],
            "webm" => [..source.Where(codec => codec is "libaom-av1" or "libvpx-vp9"), "libvpx-vp9", "libaom-av1"],
            "avi" or "3gp" or "3g2" => ["libx264", "libopenh264", "mpeg4"],
            "flv" => ["libx264", "libopenh264", "flv"],
            "ts" or "mts" or "m2ts" or "m2t" => [..source.Where(codec => codec is not ("libaom-av1" or "libvpx-vp9")), "libx264", "libopenh264", "mpeg2video"],
            _ => [..source, "libx264", "libopenh264", "mpeg4"]
        };
        return candidates.FirstOrDefault(available.Contains) ?? throw new ArgumentException("当前 FFmpeg 缺少此输出格式的视频编码器。");
    }

    public static IReadOnlyList<string> EncodingArguments(string codec, ConversionOptions options, IReadOnlyList<MediaInfo> inputs,
        long? bitrate, string stream = ":v")
    {
        if (codec == "copy") return [];
        if (bitrate is not { } bits)
        {
            if (codec is "mpeg4" or "wmv2" or "flv" or "mpeg2video") return ["-q" + stream, MediaEngine.Number(Math.Clamp(options.Quality / 4d, 2, 12))];
            if (codec == "libvpx-vp9") return ["-crf", options.Quality.ToString(), "-b" + stream, "0", "-deadline", "good", "-cpu-used", "4"];
            if (codec is "libx264" or "libx265") return ["-crf", options.Quality.ToString(), "-preset", "medium"];
            if (codec == "libaom-av1") return ["-crf", options.Quality.ToString(), "-b" + stream, "0", "-cpu-used", "6"];
            if (HardwareTranscoding.Backend(codec) is { } backend && HardwareTranscoding.Encoder(codec) is { } encoder)
                return backend.EncodingArguments(encoder, HardwareTranscoding.Context(options, inputs));
            if (codec == "h264_mf") return ["-rate_control", "quality", "-quality", Math.Clamp(100 - options.Quality * 100 / 63, 1, 100).ToString()];
            return [];
        }
        List<string> arguments = ["-b" + stream, bits.ToString(CultureInfo.InvariantCulture),
            "-maxrate" + stream, Math.Min(2_000_000_000, (long)(bits * 1.25)).ToString(CultureInfo.InvariantCulture),
            "-bufsize" + stream, Math.Min(2_000_000_000, bits * 2).ToString(CultureInfo.InvariantCulture)];
        // Bitrate modes must not also set CRF, CQ or QSCALE: QSV/AMF/VideoToolbox otherwise select quality control.
        if (codec is "libx264" or "libx265" || codec.EndsWith("_qsv", StringComparison.Ordinal)) arguments.AddRange(["-preset", "medium"]);
        else if (codec.EndsWith("_nvenc", StringComparison.Ordinal)) arguments.AddRange(["-preset", "p4", "-tune", "hq", "-rc", "vbr"]);
        else if (codec.EndsWith("_amf", StringComparison.Ordinal)) arguments.AddRange(["-quality", "balanced", "-rc", "vbr_peak"]);
        else if (codec.EndsWith("_videotoolbox", StringComparison.Ordinal)) arguments.AddRange(["-allow_sw", "0", "-realtime", "0"]);
        else if (codec == "h264_mf") arguments.AddRange(["-rate_control", "pc_vbr"]);
        else if (codec == "libvpx-vp9") arguments.AddRange(["-deadline", "good", "-cpu-used", "4"]);
        else if (codec == "libaom-av1") arguments.AddRange(["-cpu-used", "6"]);
        else if (codec == "libopenh264") arguments.AddRange(["-rc_mode", "bitrate"]);
        else if (codec == "libkvazaar") arguments.AddRange(["-kvazaar-params", "preset=medium"]);
        return arguments;
    }
}
