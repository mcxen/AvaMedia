namespace AvaMedia.Core;

public static class CoverageValidator
{
    public static CoverageGap[] Gaps(IEnumerable<(double Start, double End)> ranges, double duration, double tolerance = .5)
    {
        var gaps = new List<CoverageGap>(); double end = 0;
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (!double.IsFinite(range.Start) || !double.IsFinite(range.End) || range.Start < 0 || range.End <= range.Start || range.End > duration + tolerance)
                throw new InvalidDataException("分段或章节时间范围无效。");
            if (range.Start < end - tolerance) throw new InvalidDataException("分段或章节存在重叠。");
            if (range.Start > end + tolerance) gaps.Add(new(end, range.Start));
            end = range.End;
        }
        if (end < duration - tolerance) gaps.Add(new(end, duration));
        return gaps.ToArray();
    }
    public static void Assert(IEnumerable<(double Start, double End)> ranges, double duration)
    { if (Gaps(ranges, duration).Length > 0) throw new InvalidDataException("分段或章节没有覆盖全片。"); }
}
