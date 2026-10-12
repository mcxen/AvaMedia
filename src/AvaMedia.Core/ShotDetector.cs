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
        var boundaries = new List<ShotBoundary>(); byte[]? previous = null; double previousScore = 0;
        for (var time = 0d; time < duration; time += step)
        {
            ct.ThrowIfCancellationRequested();
            var image = await engine.Thumbnail(source, time, 96, 64, ct);
            var signature = VideoFrameSimilarity.FromEncoded(image);
            if (previous is not null)
            {
                var score = Difference(previous, signature);
                if (score >= .12 && score >= previousScore) boundaries.Add(new(time, score));
                previousScore = score;
            }
            previous = signature;
        }
        return FromBoundaries(duration, boundaries);
    }
}
