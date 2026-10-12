namespace AvaMedia.Core;

public sealed record ShotBoundary(double T, double Score);
public sealed record SummaryShot(string Id, double Start, double End, double Score);
public static class ShotDetector
{
    public static double Difference(byte[] left, byte[] right)
    {
        if (left.Length != right.Length || left.Length == 0) throw new ArgumentException("镜头签名尺寸不同。");
        return left.Zip(right).Sum(p => Math.Abs(p.First - p.Second)) / (left.Length * 255d);
    }
    public static IReadOnlyList<SummaryShot> FromBoundaries(double duration, IEnumerable<ShotBoundary> boundaries)
    {
        var points = boundaries.Where(b => b.T > 0 && b.T < duration).OrderBy(b => b.T).DistinctBy(b => b.T).ToArray();
        var starts = new[] { new ShotBoundary(0, 0) }.Concat(points).ToArray();
        return starts.Select((b, i) => new SummaryShot($"sh{i:0000}", b.T, i + 1 < starts.Length ? starts[i + 1].T : duration, b.Score)).ToArray();
    }
    public static async Task<IReadOnlyList<SummaryShot>> ScanAsync(IMediaEngine engine, string source, double duration,
        CancellationToken ct, double step = 5)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        var boundaries = new List<ShotBoundary>();
        var args = new[] { "-v", "error", "-nostdin", "-i", source, "-map", "0:v:0", "-vf", $"setpts=PTS-STARTPTS,fps={MediaEngine.Number(1 / step)},scale=64:64", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" };
        using var process = await ProcessRunner.StartAsync(engine.FFmpeg, args, ct);
        using var cancelled = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync();
        var current = new byte[64 * 64 * 3]; var previous = new byte[current.Length]; int scanned = 0; double previousScore = 0;
        try
        {
            while (await VideoSummarySampling.ReadFrameAsync(process.StandardOutput.BaseStream, current, ct))
            {
                if (scanned > 0)
                {
                    var score = VideoSummarySampling.Difference(current, previous);
                    if (score >= .12 && score >= previousScore && scanned * step < duration) boundaries.Add(new(scanned * step, score));
                    previousScore = score;
                }
                scanned++; (current, previous) = (previous, current);
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 || scanned == 0) throw new InvalidDataException("镜头扫描失败：" + await error);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync(); await error;
        }
        return FromBoundaries(duration, boundaries);
    }
}
