using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public static class TopicShiftScorer
{
    // TF-IDF on adjacent minute windows; character tokens retain CJK topic signals without an embedding dependency.
    public static IReadOnlyList<ShotBoundary> Score(IReadOnlyList<SummaryCue> cues, double duration)
    {
        var windows = Enumerable.Range(0, (int)Math.Ceiling(duration / 60)).Select(i =>
            Regex.Matches(string.Join(' ', cues.Where(c => c.Start >= i * 60 && c.Start < (i + 1) * 60).Select(c => c.Text)).ToLowerInvariant(), @"[a-z0-9]+|[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]")
            .Cast<Match>().GroupBy(m => m.Value).ToDictionary(g => g.Key, g => (double)g.Count())).ToArray();
        var df = windows.SelectMany(w => w.Keys).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var weighted = windows.Select(w => w.ToDictionary(k => k.Key, k => k.Value * (1 + Math.Log((1d + windows.Length) / (1 + df[k.Key]))))).ToArray();
        var results = new List<ShotBoundary>();
        for (int i = 1; i < weighted.Length; i++)
        {
            var left = weighted[i - 1]; var right = weighted[i];
            var norm = Math.Sqrt(left.Values.Sum(v => v * v) * right.Values.Sum(v => v * v));
            if (norm == 0) continue;
            var cosine = left.Sum(p => p.Value * right.GetValueOrDefault(p.Key)) / norm;
            results.Add(new(i * 60, 1 - cosine));
        }
        return results;
    }
}
