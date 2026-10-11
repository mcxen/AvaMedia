namespace AvaMedia.Core;

public static partial class FolderOutfitClassification
{
    private static double Scene(Sample? left, Sample? right)
    {
        if (left is null || right is null) return 0;
        var a = left.Appearance; var b = right.Appearance;
        var global = Dot(a.BackgroundEmbedding, b.BackgroundEmbedding);
        double Regions(double[] source, double[] target)
        {
            var score = 0d; var count = 0;
            for (var region = 0; region < 4; region++)
            {
                var av = source.AsSpan(region * 384, 384); var maximum = 0d; var available = false;
                for (var other = 0; other < 4; other++)
                {
                    var bv = target.AsSpan(other * 384, 384); var aa = 0d; var bb = 0d; var match = 0d;
                    for (var index = 0; index < 384; index++)
                    { aa += av[index] * av[index]; bb += bv[index] * bv[index]; match += av[index] * bv[index]; }
                    if (aa > 0 && bb > 0) { available = true; maximum = Math.Max(maximum, Math.Clamp(match, 0, 1)); }
                }
                if (available) { score += maximum; count++; }
            }
            return count == 0 ? global : score / count;
        }
        // Camera movement shifts the same background feature to another quadrant.
        var visual = .8 * global + .2 * Math.Min(Regions(a.BackgroundRegions, b.BackgroundRegions), Regions(b.BackgroundRegions, a.BackgroundRegions));
        return .9 * visual + .1 * Dot(left.BackgroundPalette, right.BackgroundPalette);
    }

    private static Sample[] ContextViews(IEnumerable<Sample> samples, int maximum)
    {
        var candidates = samples.Where(sample => sample.Appearance.BackgroundEmbedding.Any(value => value != 0)).ToArray();
        if (candidates.Length <= maximum) return candidates;
        var selected = new List<Sample> { candidates.MaxBy(sample => candidates.Sum(other => Scene(sample, other)))! };
        while (selected.Count < maximum)
        {
            var next = candidates.Except(selected).MinBy(sample => selected.Max(other => Scene(sample, other)));
            if (next is null || selected.Max(other => Scene(next, other)) >= .98) break;
            selected.Add(next);
        }
        return selected.ToArray();
    }

    private static double Scene(Sample sample, IReadOnlyList<Sample> views) => views.Count == 0 ? 0 : views.Max(view => Scene(sample, view));
    private static double Scene(IReadOnlyList<Sample> left, IReadOnlyList<Sample> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        // Two supported views when available, in both directions, rather than a lucky single-frame match.
        var scores = new double[left.Count, right.Count];
        for (var a = 0; a < left.Count; a++)
            for (var b = 0; b < right.Count; b++) scores[a, b] = Scene(left[a], right[b]);
        var forward = Enumerable.Range(0, left.Count).Select(a => Enumerable.Range(0, right.Count).Max(b => scores[a, b]))
            .OrderDescending().Take(2).Average();
        var reverse = Enumerable.Range(0, right.Count).Select(b => Enumerable.Range(0, left.Count).Max(a => scores[a, b]))
            .OrderDescending().Take(2).Average();
        return Math.Min(forward, reverse);
    }

    private static TimeSpan? CaptureGap(Profile left, Profile right)
    {
        if (left.CaptureTime is not { } a || right.CaptureTime is not { } b || a.Kind != b.Kind) return null;
        var aEnd = a.AddSeconds(left.File.Media.DurationSeconds); var bEnd = b.AddSeconds(right.File.Media.DurationSeconds);
        return a > bEnd ? a - bEnd : b > aEnd ? b - aEnd : TimeSpan.Zero;
    }

    // Both shoot grouping and garment grouping use the same complete-link clustering.
    // Every cross-group pair must agree, so a single transition frame cannot join unrelated groups.
    private static List<List<T>> Cluster<T>(IEnumerable<T> values, Func<T, T, double> similarity, double threshold, CancellationToken ct)
    {
        var groups = new List<List<T>>();
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            var best = groups.Select(group => (Group: group, Scores: group.Select(other => similarity(value, other)).ToArray()))
                .Where(match => match.Scores.Min() >= threshold - .02 && match.Scores.Average() >= threshold)
                .OrderByDescending(match => match.Scores.Average()).FirstOrDefault();
            if (best.Group is null) groups.Add([value]); else best.Group.Add(value);
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (int Left, int Right, double Mean)? merge = null;
            for (var left = 0; left < groups.Count; left++)
                for (var right = left + 1; right < groups.Count; right++)
                {
                    var scores = groups[left].SelectMany(a => groups[right].Select(b => similarity(a, b))).ToArray();
                    var mean = scores.Average();
                    if (scores.Min() >= threshold - .02 && mean >= threshold && (merge is null || mean > merge.Value.Mean))
                        merge = (left, right, mean);
                }
            if (merge is not { } selected) return groups;
            groups[selected.Left].AddRange(groups[selected.Right]); groups.RemoveAt(selected.Right);
        }
    }
}
