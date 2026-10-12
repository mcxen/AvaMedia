namespace AvaMedia.Core;

public static class ChapterBoundaryPlanner
{
    public static IReadOnlyList<IReadOnlyList<SegmentOutput>> Plan(IReadOnlyList<SegmentOutput> segments, double duration)
    {
        CoverageValidator.Assert(segments.Select(s => (s.Range.Start, s.Range.End)), duration);
        if (segments.Count <= 2) return [segments];
        int count = Math.Min(segments.Count / 2, Math.Clamp((int)Math.Round(duration / 480), 3, 40));
        for (; count >= 1; count--)
        {
            var cost = new double[count + 1, segments.Count + 1]; var previous = new int[count + 1, segments.Count + 1];
            for (int c = 0; c <= count; c++) for (int n = 0; n <= segments.Count; n++) cost[c, n] = double.PositiveInfinity;
            cost[0, 0] = 0; var target = duration / count;
            for (int c = 1; c <= count; c++)
                for (int end = c * 2; end <= segments.Count; end++)
                    for (int start = (c - 1) * 2; start <= end - 2; start++)
                    {
                        var length = segments[end - 1].Range.End - segments[start].Range.Start;
                        if (length is < 120 or > 1500 || double.IsPositiveInfinity(cost[c - 1, start])) continue;
                        var topic = start > 0 && segments[start - 1].Result.TopicLabel != segments[start].Result.TopicLabel ? .3 : 0;
                        var candidate = cost[c - 1, start] + Math.Pow((length - target) / target, 2) - (start > 0 ? segments[start - 1].Range.BoundaryScore + topic : 0);
                        if (candidate < cost[c, end]) { cost[c, end] = candidate; previous[c, end] = start; }
                    }
            if (double.IsPositiveInfinity(cost[count, segments.Count])) continue;
            var groups = new List<IReadOnlyList<SegmentOutput>>(); int finish = segments.Count;
            for (int c = count; c > 0; c--) { int begin = previous[c, finish]; groups.Add(segments.Skip(begin).Take(finish - begin).ToArray()); finish = begin; }
            groups.Reverse(); return groups;
        }
        throw new InvalidDataException("无法在章节数量和 2–25 分钟约束内覆盖全片。");
    }
}
