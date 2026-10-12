namespace AvaMedia.Core;

public static class CrossSegmentDeduper
{
    public static IReadOnlyList<SegmentOutput> Clean(IReadOnlyList<SegmentOutput> outputs)
    {
        var canonical = new Dictionary<string, SummaryEntity>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SegmentOutput>(); var events = new List<SummaryEvent>();
        foreach (var output in outputs.OrderBy(o => o.Range.Start))
        {
            var kept = output.Result.Events.Where(e => e.Start >= output.Range.Start && e.Start < output.Range.End)
                .Where(e => !events.Any(old => Overlap(old, e) > .5 && Similar(old.Text, e.Text) > .8)).ToArray();
            events.AddRange(kept);
            var entities = new List<SummaryEntity>();
            foreach (var entity in output.Result.Entities)
            {
                var existing = canonical.Values.FirstOrDefault(e => e.Name.Equals(entity.Name, StringComparison.OrdinalIgnoreCase)
                    || e.Aliases.Contains(entity.Name, StringComparer.OrdinalIgnoreCase) || entity.Aliases.Contains(e.Name, StringComparer.OrdinalIgnoreCase)
                    || Distance(e.Name.ToLowerInvariant(), entity.Name.ToLowerInvariant()) <= Math.Max(e.Name.Length, entity.Name.Length) * .15);
                var merged = existing is null ? entity : existing with { Aliases = existing.Aliases.Concat(entity.Aliases).Append(entity.Name).Where(a => a != existing.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray() };
                canonical[merged.Name] = merged; entities.Add(merged);
            }
            result.Add(output with { Result = output.Result with { Events = kept, Entities = entities.DistinctBy(e => e.Name).Take(15).ToArray() } });
        }
        return result;
    }
    private static double Overlap(SummaryEvent a, SummaryEvent b) => Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start)) / Math.Max(.001, Math.Min(a.End - a.Start, b.End - b.Start));
    private static double Similar(string a, string b) => 1 - Distance(a, b) / (double)Math.Max(1, Math.Max(a.Length, b.Length));
    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++) { var previous = row[0]; row[0] = i; for (int j = 1; j <= b.Length; j++) { var old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1)); previous = old; } }
        return row[b.Length];
    }
}
