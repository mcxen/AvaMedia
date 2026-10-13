namespace AvaMedia.Core;

/// <summary>Reserve deterministic timeline coverage before spending frames on agent follow-up questions.</summary>
internal static class VideoSummaryCoverage
{
    internal static int SeedCount(int frameBudget) => Math.Min(frameBudget, Math.Clamp(frameBudget / 2, 3, 6));

    internal static double[] Select(IEnumerable<double> candidates, int frameBudget)
    {
        var ordered = candidates.Distinct().Order().ToArray();
        var count = Math.Min(ordered.Length, SeedCount(frameBudget));
        if (count == 0) return [];
        if (count == 1) return [ordered[ordered.Length / 2]];
        return Enumerable.Range(0, count)
            .Select(index => ordered[(int)Math.Round(index * (ordered.Length - 1d) / (count - 1))]).ToArray();
    }

    internal static double[] Window(double start, double end, int frameBudget)
    {
        var count = SeedCount(frameBudget);
        return Enumerable.Range(0, count).Select(index => start + (end - start) * (index + .5) / count).ToArray();
    }
}
