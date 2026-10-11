namespace AvaMedia.Core;

public static partial class FolderOutfitClassification
{
    private static double Scene(Sample? left, Sample? right)
    {
        if (left is null || right is null) return 0;
        var a = left.Appearance; var b = right.Appearance;
        var global = Dot(a.BackgroundEmbedding, b.BackgroundEmbedding);
        var regionScore = 0d; var regionCount = 0;
        for (var region = 0; region < 4; region++)
        {
            var offset = region * 384;
            var av = a.BackgroundRegions.AsSpan(offset, 384); var bv = b.BackgroundRegions.AsSpan(offset, 384);
            var aa = 0d; var bb = 0d; var match = 0d;
            for (var index = 0; index < 384; index++)
            { aa += av[index] * av[index]; bb += bv[index] * bv[index]; match += av[index] * bv[index]; }
            if (aa > 0 && bb > 0) { regionScore += Math.Clamp(match, 0, 1); regionCount++; }
        }
        var visual = regionCount == 0 ? global : .8 * global + .2 * regionScore / regionCount;
        return .9 * visual + .1 * Dot(left.BackgroundPalette, right.BackgroundPalette);
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
