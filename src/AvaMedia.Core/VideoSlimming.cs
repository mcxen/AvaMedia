using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public enum VideoSlimmingPreset { Preserve, Balanced, Smaller }

public sealed record VideoSlimmingOptions
{
    public VideoSlimmingPreset Preset { get; init; } = VideoSlimmingPreset.Balanced;
    public string Codec { get; init; } = "hevc";
    public string Format { get; init; } = "mkv";
    public VideoSlimmingAnalysis? Analysis { get; init; }
    public void Validate()
    {
        if (!Enum.IsDefined(Preset) || Codec is not ("h264" or "hevc") || Format is not ("mp4" or "mkv"))
            throw new ArgumentException("请选择有效的瘦身档位、编码与输出格式。");
    }
}

public sealed record VideoSlimmingQuality(double Ssim, double? Xpsnr);
public sealed record VideoSlimmingAnalysis(string SourcePath, long SourceBytes, long ModifiedUtcTicks,
    VideoSlimmingPreset Preset, string Codec, string Format, string Encoder, string PixelFormat, int Threads,
    int Quality, long EstimatedBytes, double MeanSsim, double WorstSsim, double? WorstXpsnr, int Samples,
    double SampleSeconds, double Duration, int Width, int Height, double FrameRate, int AudioTracks,
    int SubtitleTracks, string Diagnosis)
{
    public double EstimatedSaving => (1 - EstimatedBytes / (double)SourceBytes) * 100;
    public bool Worthwhile => EstimatedBytes < SourceBytes * .95;
    public bool Matches(string input, VideoSlimmingOptions options, int threads)
    {
        var file = new FileInfo(input);
        return file.Exists && SourcePath == Path.GetFullPath(input) && SourceBytes == file.Length &&
            ModifiedUtcTicks == file.LastWriteTimeUtc.Ticks && Preset == options.Preset && Codec == options.Codec &&
            Format == options.Format && Threads == threads && Quality is >= 16 and <= 42 &&
            EstimatedBytes > 0 && Samples > 0 && Encoder == (Codec == "hevc" ? "libx265" : "libx264") &&
            VideoSlimming.MeetsQuality(Preset, MeanSsim, WorstSsim, WorstXpsnr);
    }
}

/// <summary>Content-based CRF search. The same software encoder, preset and threads are used for samples and export.</summary>
public sealed class VideoSlimming(MediaEngine engine)
{
    private sealed record Sample(string Path, double Duration);
    private sealed record Candidate(int Quality, long EstimatedBytes, double MeanSsim, double WorstSsim, double? WorstXpsnr);

    public static ConversionOptions CreateOptions(VideoSlimmingOptions options) => new()
    { Format = options.Format, VideoSlimming = options, AudioCodec = "copy", KeepAllAudioStreams = true };

    public static IReadOnlyList<Job> CreateJobs(IReadOnlyList<string> files, string folder, VideoSlimmingOptions options,
        IEnumerable<string>? reserved = null) => ConversionBatch.CreateJobs(Catalog.Find("video-slim"), files, folder,
            CreateOptions(options), reserved: reserved);

    public static void ValidateJob(Job job)
    {
        var options = job.Options;
        var spec = options.VideoSlimming ?? new(); spec.Validate();
        if (job.Inputs.Length != 1 || job.InputOptions is not null || options.Format != spec.Format ||
            options.Start != 0 || options.End != 0 || options.Speed != 1 || options.Mute ||
            MediaEngine.HasFilters(options) || options.CopyStreams || options.PreserveSourceAttributes ||
            options.VideoStreamIndex != 0 || options.AudioStreamIndex != 0 || options.VideoCompression is not null ||
            options.LosslessRotation is not null || options.Subtitle.Length > 0)
            throw new ArgumentException("视频瘦身保持完整视频；剪辑与画面编辑请使用快速剪辑。");
    }

    private static (double Ssim, double Xpsnr) Threshold(VideoSlimmingPreset preset) => preset switch
    { VideoSlimmingPreset.Preserve => (.995, 40), VideoSlimmingPreset.Smaller => (.98, 35), _ => (.99, 38) };

    public static bool MeetsQuality(VideoSlimmingPreset preset, double mean, double worst, double? xpsnr)
    {
        var threshold = Threshold(preset);
        return double.IsFinite(mean) && double.IsFinite(worst) && mean >= threshold.Ssim && worst >= threshold.Ssim - .003 &&
            (xpsnr is null || !double.IsNaN(xpsnr.Value) && xpsnr >= threshold.Xpsnr);
    }

    public async Task<VideoSlimmingAnalysis> AnalyzeAsync(string input, VideoSlimmingOptions options,
        Action<double, string>? progress = null, CancellationToken ct = default)
    {
        options.Validate(); input = Path.GetFullPath(input);
        var file = new FileInfo(input);
        var sourceBytes = file.Length; var modified = file.LastWriteTimeUtc.Ticks;
        var source = await engine.Probe(input, ct).ConfigureAwait(false);
        ValidateSource(source, options);
        var threads = Threads();
        var encoder = options.Codec == "hevc" ? "libx265" : "libx264";
        var encoders = await Checked(["-hide_banner", "-encoders"], ct).ConfigureAwait(false);
        if (!Regex.IsMatch(encoders.Output + encoders.Error, @"\b" + encoder + @"\b"))
            throw new InvalidOperationException("当前 FFmpeg 缺少瘦身所需的软件编码器，请使用应用内置版本。");
        var filters = await Checked(["-hide_banner", "-filters"], ct).ConfigureAwait(false);
        var listing = filters.Output + filters.Error;
        if (!Regex.IsMatch(listing, @"\bssim\b")) throw new InvalidOperationException("当前 FFmpeg 缺少 SSIM 画质分析滤镜。");
        var xpsnr = Regex.IsMatch(listing, @"\bxpsnr\b");
        using var document = JsonDocument.Parse(source.RawJson);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.First(MediaStreams.IsContentVideo);
        var audioBytes = await EstimateAudioBytesAsync(input, source, ct).ConfigureAwait(false);
        var pixelFormat = Text(video, "pix_fmt") switch
        {
            "yuv420p10le" or "yuv422p10le" or "yuv444p10le" => Text(video, "pix_fmt"),
            "yuv422p" or "yuv444p" => Text(video, "pix_fmt"),
            _ => "yuv420p"
        };
        var workspace = Path.Combine(Path.GetTempPath(), "AvaMedia-slim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var samples = new List<Sample>();
            // Non-overlapping strata cover the whole timeline, including its beginning and end.
            // Include a locally complex interval found from low-resolution motion measurements.
            var seconds = Math.Min(3, source.Duration);
            var count = Math.Clamp((int)Math.Ceiling(source.Duration / 10), 1, 6);
            var starts = Enumerable.Range(0, count).Select(i => count == 1 ? 0 :
                i * (source.Duration - seconds) / (count - 1)).ToList();
            progress?.Invoke(0, "分析画面复杂度…");
            var complexStart = await ComplexStart(input, source.Duration, ct).ConfigureAwait(false);
            complexStart = Math.Clamp(complexStart - seconds / 2, 0, Math.Max(0, source.Duration - seconds));
            if (starts.All(start => Math.Abs(start - complexStart) >= seconds)) starts.Add(complexStart);
            for (var i = 0; i < starts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(i * 15d / starts.Count, $"采样 {i + 1}/{starts.Count}");
                var path = Path.Combine(workspace, $"reference-{i}.mkv");
                samples.Add(await CreateSampleAsync(input, path, starts[i], seconds, threads, ct).ConfigureAwait(false));
            }
            Candidate? best = null;
            var lower = 16; var upper = 42; var iteration = 0; var quality = 26;
            while (lower <= upper)
            {
                var scores = new List<VideoSlimmingQuality>(); long sampleBytes = 0;
                for (var i = 0; i < samples.Count; i++)
                {
                    progress?.Invoke(15 + Math.Min(84, (iteration + i / (double)samples.Count) * 12),
                        $"比较画质 · {quality} · {i + 1}/{samples.Count}");
                    var path = Path.Combine(workspace, "candidate.mkv");
                    try
                    {
                        await Checked(EncodingArguments(samples[i].Path, path, encoder, pixelFormat, quality, threads,
                            "mkv", includeSubtitles: false), ct).ConfigureAwait(false);
                        scores.Add(await MeasureAsync(samples[i].Path, path, xpsnr, ct).ConfigureAwait(false));
                        sampleBytes += new FileInfo(path).Length;
                    }
                    finally { if (File.Exists(path)) File.Delete(path); }
                }
                var mean = scores.Select((score, i) => score.Ssim * samples[i].Duration).Sum() / samples.Sum(sample => sample.Duration);
                var worst = scores.Min(score => score.Ssim);
                double? worstXpsnr = xpsnr ? scores.Min(score => score.Xpsnr!.Value) : null;
                // Audio is measured separately: copying seek preroll into reference clips shifts video timestamps.
                var estimate = (long)Math.Ceiling(sampleBytes / samples.Sum(sample => sample.Duration) * source.Duration + audioBytes);
                if (MeetsQuality(options.Preset, mean, worst, worstXpsnr))
                {
                    var candidate = new Candidate(quality, estimate, mean, worst, worstXpsnr);
                    if (best is null || candidate.EstimatedBytes < best.EstimatedBytes) best = candidate;
                    lower = quality + 1;
                }
                else upper = quality - 1;
                quality = (lower + upper) / 2; iteration++;
            }
            if (best is null) throw new InvalidOperationException("未找到满足画质要求的瘦身参数；请调整档位或保留原视频。");
            file.Refresh();
            if (file.Length != sourceBytes || file.LastWriteTimeUtc.Ticks != modified)
                throw new IOException("分析期间源视频发生变化，请重新分析。");
            var result = new VideoSlimmingAnalysis(input, sourceBytes, modified, options.Preset, options.Codec, options.Format,
                encoder, pixelFormat, threads, best.Quality, best.EstimatedBytes, best.MeanSsim, best.WorstSsim, best.WorstXpsnr,
                samples.Count, samples.Sum(sample => sample.Duration), source.Duration, source.Width, source.Height,
                source.FrameRate, streams.Count(stream => Text(stream, "codec_type") == "audio"),
                streams.Count(stream => Text(stream, "codec_type") == "subtitle"), Diagnosis(source, sourceBytes, best.EstimatedBytes));
            progress?.Invoke(100, result.Worthwhile ? "分析完成" : "不建议瘦身：预计节省不足 5%");
            return result;
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    private async Task<Sample> CreateSampleAsync(string input, string path, double start, double seconds,
        int threads, CancellationToken ct)
    {
        // TS duration/seek estimates can land beyond the last decodable frame. FFmpeg may
        // exit successfully with a header-only MKV, which then fails ffprobe with EBML errors.
        // Only retry empty output, moving back to a nearby keyframe without accurate-seek discard.
        var positions = new[] { start, Math.Max(0, start - seconds), Math.Max(0, start - Math.Max(12, seconds * 4)) }
            .Distinct().ToArray();
        for (var attempt = 0; attempt < positions.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            List<string> args = ["-v", "error", "-nostdin", "-n", "-threads", threads.ToString()];
            if (positions[attempt] > 0)
            {
                if (attempt > 0) args.Add("-noaccurate_seek");
                args.AddRange(["-ss", MediaEngine.Number(positions[attempt])]);
            }
            args.AddRange(["-i", input, "-t", MediaEngine.Number(seconds), "-map", "0:V:0", "-an",
                "-vf", "setpts=PTS-STARTPTS", "-c:v", "ffv1", "-level", "3", "-threads", threads.ToString(),
                "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-progress", "pipe:1", "-nostats", path]);
            long frames = -1;
            await Checked(args, ct, line =>
            {
                if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                    long.TryParse(line[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                    frames = Math.Max(frames, count);
            }).ConfigureAwait(false);
            if (frames < 0) throw new InvalidDataException("无法读取视频采样帧数。");
            if (frames > 0)
            {
                var info = await engine.Probe(path, ct).ConfigureAwait(false);
                if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0)
                    throw new InvalidDataException("无法获取有效的视频样本。");
                return new(path, info.Duration);
            }
            // Never probe or compare a header-only file, and remove it before retrying with -n.
            if (File.Exists(path)) File.Delete(path);
        }
        throw new InvalidDataException($"无法在 {MediaEngine.Number(start)} 秒附近提取视频画面，源文件可能存在时长或时间戳异常。");
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        ValidateJob(job);
        var options = job.Options.VideoSlimming ?? new();
        var analysis = options.Analysis;
        if (analysis is null || !analysis.Matches(job.Inputs[0], options, Threads()))
            analysis = await AnalyzeAsync(job.Inputs[0], options, (value, detail) =>
            { job.ProgressDetail = detail; progress(value * .35); }, ct).ConfigureAwait(false);
        job.Duration = analysis.Duration;
        job.AppendLog($"视频瘦身：{analysis.SourceBytes} B；预计 {analysis.EstimatedBytes} B；{analysis.Diagnosis}");
        job.AppendLog($"{analysis.Encoder} medium CRF {analysis.Quality}；{analysis.Samples} 段 / {analysis.SampleSeconds:0.##} 秒；SSIM 平均 {analysis.MeanSsim:0.######}，最低 {analysis.WorstSsim:0.######}；XPSNR {analysis.WorstXpsnr?.ToString("0.##", CultureInfo.InvariantCulture) ?? "不可用"}。");
        if (!analysis.Worthwhile) throw new InvalidOperationException("不建议瘦身：预计节省不足 5%，保留原视频。");
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!,
            ".AvaMedia-slim-" + Guid.NewGuid().ToString("N") + "." + options.Format);
        try
        {
            job.ProgressDetail = "正在瘦身"; progress(35);
            await Checked(EncodingArguments(job.Inputs[0], temporary, analysis.Encoder, analysis.PixelFormat,
                analysis.Quality, analysis.Threads, options.Format, includeSubtitles: true), ct, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                    progress(35 + Math.Clamp(time / 1000000 / analysis.Duration, 0, 1) * 63);
            }).ConfigureAwait(false);
            var output = await engine.Probe(temporary, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(output.RawJson);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            if (Math.Abs(output.Duration - analysis.Duration) > Math.Max(.25, 3 / Math.Max(1, analysis.FrameRate)) ||
                output.Width != analysis.Width || output.Height != analysis.Height ||
                streams.Count(stream => Text(stream, "codec_type") == "audio") != analysis.AudioTracks ||
                streams.Count(stream => Text(stream, "codec_type") == "subtitle") != analysis.SubtitleTracks)
                throw new InvalidDataException("瘦身输出的时长、尺寸或轨道不完整，已保留原视频。");
            var bytes = new FileInfo(temporary).Length;
            if (bytes <= 0 || bytes >= analysis.SourceBytes * .95)
                throw new InvalidOperationException("实际节省不足 5%，已跳过输出并保留原视频。");
            if (!analysis.Matches(job.Inputs[0], options, Threads())) throw new IOException("瘦身期间源视频发生变化，请重新分析。");
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, job.Output);
            var saving = (1 - bytes / (double)analysis.SourceBytes) * 100;
            job.Options.VideoSlimming = options with { Analysis = analysis };
            job.ProgressDetail = $"节省 {saving:0.##}%";
            job.AppendLog($"实际输出 {bytes} B，节省 {saving:0.##}%；保持分辨率、帧率和全部音轨 / 字幕轨。画质评分来自采样，不等于无损。");
            progress(100);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<VideoSlimmingQuality> MeasureAsync(string reference, string distorted, bool xpsnr = true,
        CancellationToken ct = default)
    {
        // Matroska rounds timestamps to milliseconds. Match the nearest frame so rounding does not
        // compare a frame to its predecessor; never repeat the last frame to hide truncation.
        var filter = xpsnr
            ? "[0:V:0]settb=AVTB,setpts=PTS-STARTPTS,split=2[d0][d1];[1:V:0]settb=AVTB,setpts=PTS-STARTPTS,split=2[r0][r1];[d0][r0]ssim=shortest=1:repeatlast=0:ts_sync_mode=nearest[s];[d1][r1]xpsnr=shortest=1:repeatlast=0:ts_sync_mode=nearest[x]"
            : "[0:V:0]settb=AVTB,setpts=PTS-STARTPTS[d];[1:V:0]settb=AVTB,setpts=PTS-STARTPTS[r];[d][r]ssim=shortest=1:repeatlast=0:ts_sync_mode=nearest[s]";
        List<string> args = ["-hide_banner", "-nostdin", "-threads", Threads().ToString(), "-i", distorted,
            "-threads", Threads().ToString(), "-i", reference, "-filter_complex_threads", "1", "-filter_complex", filter, "-map", "[s]"];
        if (xpsnr) args.AddRange(["-map", "[x]"]);
        args.AddRange(["-an", "-f", "null", "-"]);
        var result = await Checked(args, ct).ConfigureAwait(false);
        var ssim = Regex.Matches(result.Error, @"SSIM[^\r\n]*All:([0-9.]+)").LastOrDefault();
        if (ssim is null || !double.TryParse(ssim.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || !double.IsFinite(score))
            throw new InvalidDataException("无法读取 SSIM 画质评分。");
        double? perceptual = null;
        if (xpsnr)
        {
            var match = Regex.Matches(result.Error, @"XPSNR[^\r\n]*y:\s*([0-9.]+|inf)[^\r\n]*u:\s*([0-9.]+|inf)[^\r\n]*v:\s*([0-9.]+|inf)").LastOrDefault();
            if (match is null) throw new InvalidDataException("无法读取 XPSNR 画质评分。");
            perceptual = Enumerable.Range(1, 3).Select(i => match.Groups[i].Value == "inf" ? double.PositiveInfinity :
                double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).Min();
            // Keep persisted JSON finite even when two planes are identical.
            if (double.IsPositiveInfinity(perceptual.Value)) perceptual = 100;
        }
        return new(score, perceptual);
    }

    private async Task<double> ComplexStart(string input, double duration, CancellationToken ct)
    {
        // Seek into eight short windows instead of decoding an entire multi-hour source to select samples.
        double bestTime = 0, bestScore = -1;
        var length = Math.Min(1, duration);
        var count = Math.Min(8, Math.Max(1, (int)Math.Ceiling(duration / 5)));
        for (var i = 0; i < count; i++)
        {
            var start = count == 1 ? 0 : i * (duration - length) / (count - 1);
            var result = await Checked(["-hide_banner", "-nostdin", "-threads", Threads().ToString(),
                "-ss", MediaEngine.Number(start), "-i", input, "-t", MediaEngine.Number(length), "-map", "0:V:0", "-an", "-vf",
                "fps=4,scale=160:-2,signalstats,metadata=mode=print", "-f", "null", "-"], ct).ConfigureAwait(false);
            double time = 0;
            foreach (var line in result.Error.Split('\n'))
            {
                var stamp = Regex.Match(line, @"pts_time:([0-9.]+)");
                if (stamp.Success) double.TryParse(stamp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out time);
                var metric = Regex.Match(line, @"lavfi.signalstats.YDIF=([0-9.]+)");
                if (metric.Success && double.TryParse(metric.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var score) && score > bestScore)
                { bestScore = score; bestTime = start + time; }
            }
        }
        return bestTime;
    }

    private int Threads() => engine.Settings.MultiThread ? Math.Clamp(engine.Settings.CpuThreads, 1, 16) : 1;
    private async Task<double> EstimateAudioBytesAsync(string input, MediaInfo source, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(source.RawJson);
        var audios = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(stream => Text(stream, "codec_type") == "audio").ToArray();
        if (audios.Length == 0) return 0;
        var rates = audios.Select(stream => double.TryParse(Text(stream, "bit_rate"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var rate) && double.IsFinite(rate) && rate > 0 ? rate : 0).ToArray();
        if (rates.All(rate => rate > 0)) return rates.Sum() * source.Duration / 8;
        double bytesPerSecond = 0;
        foreach (var fraction in new[] { .1, .5, .9 })
        {
            var length = Math.Min(4, source.Duration);
            var start = Math.Max(0, source.Duration * fraction - length / 2);
            var result = await ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-select_streams", "a",
                "-read_intervals", $"{MediaEngine.Number(start)}%+{MediaEngine.Number(length)}", "-show_packets",
                "-show_entries", "packet=stream_index,pts_time,duration_time,size", "-of", "json", input], ct,
                maximumOutputChars: 4000000).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidDataException(result.Error);
            using var packets = JsonDocument.Parse(result.Output);
            var found = new HashSet<int>();
            double sampleRate = 0;
            foreach (var group in packets.RootElement.GetProperty("packets").EnumerateArray().GroupBy(packet => packet.GetProperty("stream_index").GetInt32()))
            {
                var timed = group.Where(packet => packet.TryGetProperty("pts_time", out _)).ToArray();
                if (timed.Length < 2) continue;
                var first = timed.Min(packet => double.Parse(Text(packet, "pts_time"), CultureInfo.InvariantCulture));
                var last = timed.Max(packet => double.Parse(Text(packet, "pts_time"), CultureInfo.InvariantCulture));
                var duration = last - first + (last - first) / (timed.Length - 1);
                if (duration <= 0) continue;
                sampleRate += timed.Sum(packet => long.Parse(Text(packet, "size"), CultureInfo.InvariantCulture)) / duration;
                found.Add(group.Key);
            }
            if (found.Count != audios.Length)
                throw new InvalidDataException("无法估算全部音轨体积，请使用视频压缩指定体积。");
            bytesPerSecond += sampleRate / 3;
        }
        return bytesPerSecond * source.Duration;
    }
    private async Task<ProcessResult> Checked(IEnumerable<string> args, CancellationToken ct, Action<string>? line = null)
    {
        var result = await ProcessRunner.Run(engine.FFmpeg, args, ct, line).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error);
        return result;
    }

    private static IReadOnlyList<string> EncodingArguments(string input, string output, string encoder,
        string pixelFormat, int quality, int threads, string format, bool includeSubtitles)
    {
        List<string> args = ["-hide_banner", "-nostdin", "-n", "-threads", threads.ToString(), "-i", input,
            "-map", "0:V:0", "-map", "0:a?", "-c:v", encoder, "-preset", "medium", "-crf", quality.ToString(),
            "-pix_fmt", pixelFormat, "-threads", threads.ToString(), "-c:a", "copy", "-fps_mode", "passthrough", "-enc_time_base:v", "demux",
            "-metadata:s:v:0", "rotate=0"];
        if (encoder == "libx265") args.AddRange(["-x265-params", $"pools={threads}:frame-threads=1:log-level=error"]);
        if (includeSubtitles)
        {
            args.AddRange(["-map", "0:s?", "-c:s", "copy", "-map_metadata", "0", "-map_chapters", "0"]);
            if (format == "mkv") args.AddRange(["-map", "0:t?", "-c:t", "copy"]);
        }
        if (format == "mp4")
        {
            args.AddRange(["-movflags", "+faststart"]);
            if (encoder == "libx265") args.AddRange(["-tag:v", "hvc1"]);
        }
        args.AddRange(["-progress", "pipe:1", "-nostats", output]);
        return args;
    }

    public static void ValidateSource(MediaInfo source, VideoSlimmingOptions options)
    {
        options.Validate();
        using var document = JsonDocument.Parse(source.RawJson);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var allVideos = streams.Where(stream => Text(stream, "codec_type") == "video").ToArray();
        var videos = allVideos.Where(MediaStreams.IsContentVideo).ToArray();
        if (videos.Length == 0 || !source.HasVideo || !double.IsFinite(source.Duration) || source.Duration <= 0 || source.Width < 2 || source.Height < 2)
            throw new ArgumentException("请选择有有效时长的视频。");
        if (videos.Length != 1) throw new ArgumentException("多视频轨文件请先提取需要的视频轨。");
        var color = VideoCompressionColor.Inspect(source with { VideoStreamIndex = Array.FindIndex(allVideos, MediaStreams.IsContentVideo) });
        if (color.ToneMap || color.DolbyVision) throw new ArgumentException("HDR / Dolby Vision 视频请使用视频压缩；瘦身暂仅分析 SDR 视频。");
        if (videos[0].TryGetProperty("field_order", out var order) && order.GetString() is "tt" or "bb" or "tb" or "bt")
            throw new ArgumentException("隔行视频请先在格式转换中去隔行。");
        foreach (var stream in streams)
        {
            var type = Text(stream, "codec_type"); var codec = Text(stream, "codec_name");
            if (options.Format == "mp4" && (type == "audio" && codec is not ("aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus" or "flac") ||
                type == "subtitle" && codec != "mov_text" || type == "attachment"))
                throw new ArgumentException("此音轨、字幕或附件无法原样保留在 MP4，请选择 MKV。");
            if (options.Format == "mkv" && type == "subtitle" && codec == "mov_text")
                throw new ArgumentException("此视频的字幕适合 MP4，请选择 MP4 以原样保留。");
        }
    }

    private static string Diagnosis(MediaInfo source, long bytes, long estimate)
    {
        var totalMbps = bytes * 8d / source.Duration / 1000000;
        return estimate < bytes * .5 ? $"总码率 {totalMbps:0.##} Mbps，采样显示有较大压缩空间。" :
            estimate < bytes * .95 ? $"总码率 {totalMbps:0.##} Mbps，有一定压缩空间。" : "当前编码已较紧凑。";
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
}
