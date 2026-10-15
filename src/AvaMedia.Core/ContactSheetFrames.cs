using AvaMedia.Core.Compute;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace AvaMedia.Core;

public static partial class BatchVideoTools
{
    public static string ContactSheetVideoInformation(MediaInfo info)
        => FormattableString.Invariant($"{info.Width} × {info.Height} · {TimeSpan.FromSeconds(info.Duration):hh\\:mm\\:ss} · {info.FrameRate:0.##} 帧/秒 · {info.VideoCodec}");

    private static async Task<(byte[] Data, double Seconds)> ExtractContactSheetCandidate(IMediaEngine engine, string input,
        MediaInfo info, double seconds, int width, int height, ContactSheetOptions options, double fps, double duration, CancellationToken ct)
    {
        var original = await ExtractContactSheetFrame(engine, input, info, seconds, width, height, options.Sharpen, ct).ConfigureAwait(false);
        if (!options.AvoidBlur) return original;
        var score = Sharpness(original.Data); var bestScore = score;
        var best = original;
        var end = options.EndSeconds > 0 ? Math.Min(options.EndSeconds, duration) : duration;
        var last = Math.Max(options.StartSeconds, end - Math.Max(.08, 1 / fps));
        // Only inspect the immediate neighbours. Keep the exact requested frame unless
        // another is clearly sharper; timestamps always describe the frame actually chosen.
        foreach (var offset in new[] { -1 / fps, 1 / fps })
        {
            var candidate = Math.Clamp(seconds + offset, options.StartSeconds, last);
            if (Math.Abs(candidate - seconds) < .000001) continue;
            var data = await ExtractContactSheetFrame(engine, input, info, candidate, width, height, options.Sharpen, ct).ConfigureAwait(false);
            var sharpness = Sharpness(data.Data);
            if (sharpness > Math.Max(score * 1.5, bestScore)) { best = data; bestScore = sharpness; }
        }
        return best;
    }

    private static double Sharpness(byte[] data)
    {
        using var frame = SKBitmap.Decode(data) ?? throw new InvalidDataException("无法读取高清候选帧。");
        var step = Math.Max(1, Math.Max(frame.Width, frame.Height) / 128);
        double sum = 0, squares = 0; var count = 0;
        double Luma(int x, int y)
        {
            var pixel = frame.GetPixel(x, y);
            return .2126 * pixel.Red + .7152 * pixel.Green + .0722 * pixel.Blue;
        }
        for (var y = 1; y < frame.Height - 1; y += step)
            for (var x = 1; x < frame.Width - 1; x += step)
            {
                var laplacian = 4 * Luma(x, y) - Luma(x - 1, y) - Luma(x + 1, y) - Luma(x, y - 1) - Luma(x, y + 1);
                sum += laplacian; squares += laplacian * laplacian; count++;
            }
        return count == 0 ? 0 : squares / count - Math.Pow(sum / count, 2);
    }

    private static async Task<(byte[] Data, double Seconds)> ExtractContactSheetFrame(IMediaEngine engine, string input, MediaInfo info,
        double seconds, int width, int height, bool sharpen, CancellationToken ct)
    {
        // Decode the source at its native resolution. The first seek jumps near the target;
        // trimming then discards decoded interframes until the precise target. showinfo reports
        // the first retained frame's actual PTS, so VFR/low-frame-rate captions remain truthful.
        var coarse = Math.Max(0, seconds - 2);
        List<string> args = ["-hide_banner", "-v", "info", "-nostdin", "-filter_threads", "1", "-threads", "2",
            "-ss", MediaEngine.Number(coarse), "-accurate_seek", "-i", input,
            "-map", "0:" + MediaStreams.VideoSpecifier(info.VideoStreamIndex),
            "-an", "-sn", "-dn"];
        var color = VideoCompressionColor.Inspect(info);
        var filters = new List<string> { "trim=start=" + MediaEngine.Number(seconds - coarse), "showinfo=checksum=0" };
        if (color.ToneMap)
        {
            // Reuse the existing HDR detection and linear-light mapping, but keep RGB and
            // finish in sRGB for an image: no intermediate 4:2:0 chroma subsampling.
            filters.AddRange(color.Filters().Take(4));
            filters.Add("zscale=transfer=iec61966-2-1:matrix=gbr:range=full:dither=error_diffusion");
            filters.Add("sidedata=mode=delete");
        }
        else if (color.DolbyVision) filters.Add("sidedata=mode=delete");
        filters.Add($"scale={width}:{height}:flags=lanczos+accurate_rnd:out_range=full");
        filters.Add("setsar=1");
        if (sharpen) filters.Add("unsharp=5:5:0.25:5:5:0");
        filters.Add("format=rgb24");
        args.AddRange(["-vf", string.Join(',', filters), "-frames:v", "1", "-threads", "1",
            "-fps_mode", "passthrough", "-f", "image2pipe", "-c:v", "png", "pipe:1"]);
        using var output = new MemoryStream();
        var decoded = await FrameDecodeArguments.RunAsync(engine, info, args, output, ct).ConfigureAwait(false);
        var diagnostic = decoded.Error;
        ct.ThrowIfCancellationRequested();
        if (decoded.ExitCode != 0 || output.Length == 0)
            throw new InvalidDataException(string.IsNullOrWhiteSpace(diagnostic) ? "无法精确读取高清画面。" : diagnostic);
        var match = Regex.Match(diagnostic, @"showinfo[^\r\n]*\bn:\s*0\s+[^\r\n]*\bpts_time:([-+0-9.eE]+)");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
            || !double.IsFinite(pts)) throw new InvalidDataException("高清抽帧缺少实际帧时间戳。");
        return (output.ToArray(), Math.Max(0, coarse + pts));
    }
}
