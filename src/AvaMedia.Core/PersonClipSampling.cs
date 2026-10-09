using System.Runtime.CompilerServices;

namespace AvaMedia.Core;

public sealed record PersonClipRange(double Start, double End)
{
    public void Validate()
    {
        if (!double.IsFinite(Start) || !double.IsFinite(End) || Start < 0 || End <= Start)
            throw new ArgumentException("免检测区间的结束时间必须晚于开始时间。");
    }
}

public static class PersonClipExclusions
{
    public static PersonClipRange[] Normalize(IEnumerable<PersonClipRange>? ranges)
    {
        var result = new List<PersonClipRange>();
        foreach (var range in (ranges ?? []).OrderBy(range => range.Start))
        {
            range.Validate();
            if (result.Count > 0 && range.Start <= result[^1].End)
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, range.End) };
            else result.Add(range);
        }
        return result.ToArray();
    }

    public static PersonClipRange[] Subtract(IEnumerable<PersonClipRange> ranges, IEnumerable<PersonClipRange>? excluded)
    {
        var masks = Normalize(excluded);
        var result = new List<PersonClipRange>();
        foreach (var range in ranges)
        {
            var start = range.Start;
            foreach (var mask in masks)
            {
                if (mask.End <= start) continue;
                if (mask.Start >= range.End) break;
                if (mask.Start > start) result.Add(new(start, mask.Start));
                start = Math.Max(start, mask.End);
                if (start >= range.End) break;
            }
            if (start < range.End) result.Add(new(start, range.End));
        }
        return result.ToArray();
    }
}

internal enum PersonFrameSkip { None, Excluded, Dark, Blank }
internal sealed record PersonDecodedFrame(double Seconds, byte[]? Rgb);

internal static class PersonClipFrameSource
{
    public static async IAsyncEnumerable<PersonDecodedFrame> ReadAsync(IMediaEngine engine, string path, MediaInfo info,
        PersonClipOptions options, int size, [EnumeratorCancellation] CancellationToken ct)
    {
        var intervals = PersonClipExclusions.Subtract([new(0, info.Duration)], options.ExcludedRanges);
        var lastEnd = 0d;
        foreach (var interval in intervals)
        {
            ct.ThrowIfCancellationRequested();
            // A synthetic negative sample separates ranges without decoding excluded video.
            if (interval.Start > lastEnd) yield return new(lastEnd, null);
            List<string> args = ["-v", "error", "-nostdin", "-threads",
                (engine.Settings.MultiThread ? Math.Clamp(engine.Settings.CpuThreads, 1, 16) : 1).ToString()];
            if (interval.Start > 0) args.AddRange(["-ss", MediaEngine.Number(interval.Start)]);
            args.AddRange(["-i", path, "-t", MediaEngine.Number(interval.End - interval.Start), "-map", $"0:v:{info.VideoStreamIndex}",
                "-an", "-sn", "-vf", $"setpts=PTS-STARTPTS,fps={MediaEngine.Number(options.FramesPerSecond)}:start_time=0:eof_action=pass,{Filter(size)}",
                "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
            using var process = await ProcessRunner.StartAsync(engine.FFmpeg, args, ct).ConfigureAwait(false);
            using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync();
            var frame = new byte[size * size * 3];
            var frames = 0;
            try
            {
                while (await ReadFrameAsync(process.StandardOutput.BaseStream, frame, ct).ConfigureAwait(false))
                {
                    var seconds = interval.Start + frames / options.FramesPerSecond;
                    if (seconds >= interval.End) break;
                    frames++;
                    yield return new(seconds, frame);
                }
                await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidDataException("视频分析解码失败：" + await error.ConfigureAwait(false));
                if (frames == 0) throw new InvalidDataException($"无法解码 {MediaTime.Format(interval.Start)} – {MediaTime.Format(interval.End)} 的检测区间。");
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await error.ConfigureAwait(false);
            }
            lastEnd = interval.End;
        }
        if (lastEnd < info.Duration) yield return new(lastEnd, null);
    }

    internal static string Filter(int size) => $"scale={size}:{size}:force_original_aspect_ratio=decrease,pad={size}:{size}:(ow-iw)/2:(oh-ih)/2:color=0x727272,setsar=1";

    internal static async Task<bool> ReadFrameAsync(Stream input, byte[] frame, CancellationToken ct)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await input.ReadAsync(frame.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) { if (offset == 0) return false; throw new InvalidDataException("视频帧数据不完整。"); }
            offset += read;
        }
        return true;
    }
}

internal readonly record struct PersonFrameBounds(int Left, int Top, int Right, int Bottom)
{
    public static PersonFrameBounds From(MediaInfo info, int size)
    {
        var scale = size / (double)Math.Max(info.Width, info.Height);
        var width = Math.Max(1, (int)(info.Width * scale));
        var height = Math.Max(1, (int)(info.Height * scale));
        var left = (size - width) / 2; var top = (size - height) / 2;
        // Ignore the model's grey letterbox and rounding at the scaled content's edge.
        return new(left + 2, top + 2, left + width - 2, top + height - 2);
    }
}

internal static class PersonFrameVisibility
{
    public static PersonFrameSkip Exclude(byte[] rgb, int size, PersonFrameBounds bounds, PersonClipOptions options)
    {
        if ((!options.SkipDarkFrames && !options.SkipBlankFrames) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            return PersonFrameSkip.None;
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        long sum = 0, squares = 0; var count = 0; var bright = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y += 2)
            for (var x = bounds.Left; x < bounds.Right; x += 2)
            {
                var index = (y * size + x) * 3;
                var luma = (54 * rgb[index] + 183 * rgb[index + 1] + 19 * rgb[index + 2]) >> 8;
                histogram[luma]++; count++; sum += luma; squares += luma * luma;
                if (luma > options.DarkLumaThreshold * 2) bright++;
            }
        if (count == 0) return PersonFrameSkip.None;
        var mean = sum / (double)count;
        var variance = Math.Max(0, squares / (double)count - mean * mean);
        if (options.SkipDarkFrames && mean <= options.DarkLumaThreshold && bright <= count * .005
            && variance <= options.DarkLumaThreshold * options.DarkLumaThreshold)
            return PersonFrameSkip.Dark;
        if (options.SkipBlankFrames && variance <= 1)
        {
            var low = 0; var high = 255; var below = 0; var above = 0;
            while (low < 255 && below + histogram[low] <= count * .01) below += histogram[low++];
            while (high > 0 && above + histogram[high] <= count * .01) above += histogram[high--];
            if (high - low <= 4) return PersonFrameSkip.Blank;
        }
        return PersonFrameSkip.None;
    }
}
