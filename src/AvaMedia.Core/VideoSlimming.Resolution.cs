namespace AvaMedia.Core;

public sealed partial class VideoSlimming
{
    private sealed record Resolution(int Width, int Height, bool Resampled, double PixelAspectRatio);

    private static IReadOnlyList<Resolution> Resolutions(MediaInfo source)
    {
        var sar = MediaStreams.PixelAspectRatio(source);
        var original = new Resolution(source.Width, source.Height, false, sar);
        // Reduce the densely sampled axis, never enlarge the other one or treat SAR as proof of redundancy.
        var width = sar < 1 ? Math.Min(source.Width, (int)Math.Round(source.Width * sar / 2) * 2) : source.Width;
        var height = sar > 1 ? Math.Min(source.Height, (int)Math.Round(source.Height / sar / 2) * 2) : source.Height;
        if (width < 16 || height < 16 || (width | height) % 2 != 0 ||
            width * (double)height > source.Width * (double)source.Height * .9) return [original];
        Resolution Scaled(int w, int h) => new(w, h, true, source.Width * sar / source.Height * h / w);
        // A gentler reduction can retain real fine detail that a near-square-pixel candidate would lose.
        var middleWidth = sar < 1 ? (int)Math.Round(source.Width * Math.Sqrt(sar) / 2) * 2 : source.Width;
        var middleHeight = sar > 1 ? (int)Math.Round(source.Height / Math.Sqrt(sar) / 2) * 2 : source.Height;
        return middleWidth >= 16 && middleHeight >= 16 && (middleWidth | middleHeight) % 2 == 0 &&
            (middleWidth != width || middleHeight != height) &&
            middleWidth * (double)middleHeight <= source.Width * (double)source.Height * .9
            ? [original, Scaled(middleWidth, middleHeight), Scaled(width, height)] : [original, Scaled(width, height)];
    }

    private async Task<Candidate?> SearchAsync(IReadOnlyList<Sample> samples, Resolution resolution, MediaInfo source,
        VideoSlimmingOptions options, string encoder, string pixelFormat, int threads, bool xpsnr, double audioBytes,
        string workspace, Action<double, string> progress, CancellationToken ct)
    {
        Candidate? best = null;
        var lower = 16; var upper = 42; var iteration = 0; var quality = 26;
        var difficultSample = 0;
        var duration = samples.Sum(sample => sample.Duration);
        // Compare on the original grid so resampling losses cannot disappear into a downscaled reference.
        var restore = resolution.Resampled ? $"scale={source.Width}:{source.Height}:flags=lanczos," : "";
        while (lower <= upper)
        {
            var scores = new VideoSlimmingQuality[samples.Count]; long sampleBytes = 0;
            var accepted = true;
            // Start with the last limiting sample. Rejected candidates do not need every
            // expensive encode, but a passing candidate must still cover the whole timeline.
            var order = Enumerable.Range(0, samples.Count).OrderBy(index => index == difficultSample ? 0 : 1).ToArray();
            for (var i = 0; i < samples.Count; i++)
            {
                var sampleIndex = order[i];
                progress(Math.Min(99, (iteration + i / (double)samples.Count) * 20),
                    $"比较画质 · {resolution.Width} × {resolution.Height} · {quality} · {i + 1}/{samples.Count}");
                var path = Path.Combine(workspace, "candidate.mkv");
                try
                {
                    await Checked(EncodingArguments(samples[sampleIndex].Path, path, encoder, pixelFormat, quality, threads,
                        "mkv", includeSubtitles: false, resolution), ct).ConfigureAwait(false);
                    var score = await MeasureCoreAsync(samples[sampleIndex].Path, path, xpsnr, restore, ct).ConfigureAwait(false);
                    scores[sampleIndex] = score;
                    if (!MeetsWorstQuality(options.Preset, score.Ssim, score.Xpsnr))
                    {
                        // A sample below either worst-sample threshold cannot be rescued by
                        // other samples. This rejects the same CRF as the complete aggregate.
                        accepted = false; difficultSample = sampleIndex; break;
                    }
                    sampleBytes += new FileInfo(path).Length;
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
            if (accepted)
            {
                var mean = scores.Select((score, i) => score.Ssim * samples[i].Duration).Sum() / duration;
                var worst = scores.Min(score => score.Ssim);
                double? worstXpsnr = xpsnr ? scores.Min(score => score.Xpsnr!.Value) : null;
                accepted = MeetsQuality(options.Preset, mean, worst, worstXpsnr);
                difficultSample = Array.FindIndex(scores, score => score.Ssim == worst);
                if (accepted)
                {
                    var estimate = (long)Math.Ceiling(sampleBytes / duration * source.Duration + audioBytes);
                    var candidate = new Candidate(quality, estimate, mean, worst, worstXpsnr, resolution);
                    if (best is null || candidate.EstimatedBytes < best.EstimatedBytes) best = candidate;
                }
            }
            if (accepted) lower = quality + 1;
            else upper = quality - 1;
            quality = (lower + upper) / 2; iteration++;
        }
        progress(100, best is null ? $"{resolution.Width} × {resolution.Height}：未达到画质要求" :
            $"{resolution.Width} × {resolution.Height}：预计 {best.EstimatedBytes / 1000000d:0.##} MB · CRF {best.Quality} · SSIM {best.MeanSsim:0.######} · XPSNR {best.WorstXpsnr?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "不可用"}");
        return best;
    }
}
