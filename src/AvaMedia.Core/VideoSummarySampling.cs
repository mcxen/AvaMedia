using SkiaSharp;
using System.Globalization;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record VideoSummarySamplingInfo(int ScannedFrames, double ScanInterval, int ChangeCandidates,
    int CandidateFrames, int DuplicateFrames, int SelectedFrames);
internal sealed record SummarySample(double Seconds, byte[] Image, string Reason);
internal sealed record SummarySamples(IReadOnlyList<SummarySample> Frames, VideoSummarySamplingInfo Info);

/// <summary>Bounded low-resolution scan plus uniform coverage; pixel changes are candidates, never asserted cuts.</summary>
internal static class VideoSummarySampling
{
    private const int ScanSize = 64, MaximumScanFrames = 2048;
    private sealed record Change(double Seconds, double Score);
    private sealed record Candidate(double Seconds, string Reason, bool Coverage);

    internal static async Task<SummarySamples> SelectAsync(IMediaEngine engine, string path, MediaInfo info, int budget,
        Action<double, string> report, CancellationToken ct)
    {
        var count = Math.Min(budget, Math.Max(1, (int)Math.Min(48, Math.Ceiling(info.Duration))));
        var videoStart = VideoStart(info);
        var span = info.Duration - videoStart;
        var fps = Math.Min(2, MaximumScanFrames / span);
        var interval = 1 / fps;
        var changes = new List<Change>();
        var scanned = 0;
        var args = new[] { "-v", "error", "-nostdin", "-i", path, "-map", $"0:v:{info.VideoStreamIndex}",
            "-vf", $"setpts=PTS-STARTPTS,fps={fps.ToString("0.################", CultureInfo.InvariantCulture)}:start_time=0:eof_action=pass,scale={ScanSize}:{ScanSize}",
            "-frames:v", MaximumScanFrames.ToString(), "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" };
        using (var process = await ProcessRunner.StartAsync(engine.FFmpeg, args, ct).ConfigureAwait(false))
        {
            using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync();
            var current = new byte[ScanSize * ScanSize * 3]; var previous = new byte[current.Length];
            try
            {
                while (await ReadFrameAsync(process.StandardOutput.BaseStream, current, ct).ConfigureAwait(false))
                {
                    var seconds = videoStart + scanned * interval;
                    if (seconds < info.Duration && scanned > 0)
                    {
                        var score = Difference(current, previous);
                        if (score >= .035) changes.Add(new(seconds, score));
                    }
                    scanned++;
                    (current, previous) = (previous, current);
                    report(Math.Min(.5, .5 * scanned * interval / span), "扫描画面变化");
                }
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidDataException("视频采样扫描失败：" + await error.ConfigureAwait(false));
                if (scanned == 0) throw new InvalidDataException("视频未解码出可分析的画面。");
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
                await process.WaitForExitAsync().ConfigureAwait(false); await error.ConfigureAwait(false);
            }
        }
        var candidates = new List<Candidate>();
        var coverageCount = Math.Max(1, (count + 1) / 2);
        var minimumGap = Math.Min(.1, info.Duration / (count * 4));
        void Add(double seconds, string reason, bool coverage)
        {
            seconds = Math.Clamp(seconds, videoStart, Math.Max(videoStart, info.Duration - .001));
            if (candidates.All(item => Math.Abs(item.Seconds - seconds) > minimumGap)) candidates.Add(new(seconds, reason, coverage));
        }
        for (var index = 0; index < coverageCount; index++) Add(videoStart + span * (index + .5) / coverageCount, "均匀覆盖", true);
        // Spread change candidates across the timeline before filling unused slots.
        var ranked = changes.GroupBy(item => (int)((item.Seconds - videoStart) / span * count))
            .Select(group => group.MaxBy(item => item.Score)!).OrderByDescending(item => item.Score).ToArray();
        foreach (var change in ranked.Take(count))
        {
            var before = Math.Max(0, change.Seconds - interval);
            Add(before, "变化前", false); Add(change.Seconds, "变化后", false);
        }
        for (var index = 0; index < count; index++) Add(videoStart + span * (index + .5) / count, "均匀补充", false);
        var selected = new List<(SummarySample Sample, byte[] Signature)>(); var duplicates = 0; var examined = 0;
        foreach (var candidate in candidates.OrderByDescending(item => item.Coverage))
        {
            ct.ThrowIfCancellationRequested();
            var image = await engine.Thumbnail(path, candidate.Seconds, 768, 768, ct, pad: false,
                videoStreamIndex: info.VideoStreamIndex, endExclusive: candidate.Seconds > 0).ConfigureAwait(false);
            var signature = VideoFrameSimilarity.FromEncoded(image);
            // Compare against every accepted frame so a repeated slide/shot cannot consume the budget.
            if (selected.Any(item => VideoFrameSimilarity.Similar(signature, item.Signature))) duplicates++;
            else selected.Add((new(candidate.Seconds, image, candidate.Reason), signature));
            report(.5 + .5 * ++examined / candidates.Count, "选择关键画面");
            if (selected.Count >= count) break;
        }
        var frames = selected.Select(item => item.Sample).OrderBy(item => item.Seconds).ToArray();
        return new(frames, new(scanned, interval, changes.Count, examined, duplicates, frames.Length));
    }

    private static double VideoStart(MediaInfo info)
    {
        using var json = JsonDocument.Parse(info.RawJson);
        static double? Start(JsonElement item) => item.TryGetProperty("start_time", out var value)
            && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) ? seconds : null;
        var root = json.RootElement;
        var origin = root.TryGetProperty("format", out var format) ? Start(format) ?? 0 : 0;
        var video = root.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "video")
            .ElementAt(info.VideoStreamIndex);
        return Math.Clamp((Start(video) ?? origin) - origin, 0, Math.Max(0, info.Duration - .001));
    }

    // A local block score keeps small moving subjects from vanishing in a full-frame average.
    private static double Difference(byte[] current, byte[] previous)
    {
        double sum = 0, peak = 0;
        for (var by = 0; by < ScanSize; by += 8)
            for (var bx = 0; bx < ScanSize; bx += 8)
            {
                double block = 0;
                for (var y = by; y < by + 8; y++)
                    for (var x = bx; x < bx + 8; x++)
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var offset = (y * ScanSize + x) * 3 + channel;
                            block += Math.Abs(current[offset] - previous[offset]);
                        }
                sum += block; peak = Math.Max(peak, block / (8 * 8 * 3 * 255));
            }
        return Math.Max(sum / (current.Length * 255), peak * .5);
    }

    private static async Task<bool> ReadFrameAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset != 0) throw new InvalidDataException("视频扫描返回了不完整的画面。");
                return false;
            }
            offset += read;
        }
        return true;
    }

    internal static byte[] ModelImage(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image) ?? throw new InvalidDataException("无法读取采样画面。");
        var ratio = Math.Min(1, 384d / Math.Max(bitmap.Width, bitmap.Height));
        using var reduced = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * ratio)), Math.Max(1, (int)(bitmap.Height * ratio))), SKFilterQuality.Medium)
            ?? throw new InvalidDataException("无法缩小采样画面。");
        using var encoded = reduced.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
