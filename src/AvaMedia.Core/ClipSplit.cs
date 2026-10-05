using System.Globalization;

namespace AvaMedia.Core;

public enum ClipSplitMode { EqualParts, FixedDuration, TimePoints }
public sealed record ClipSplitSettings(ClipSplitMode Mode = ClipSplitMode.EqualParts, int Parts = 2,
    double SegmentSeconds = 60, IReadOnlyList<double>? TimePoints = null);

/// <summary>Partitions a source-time interval; each independent draft retains its editing options.</summary>
public static class ClipSplit
{
    public const int MaximumSegments = 100;

    public static IReadOnlyList<ConversionOptions> Create(ConversionOptions draft, double duration, ClipSplitSettings settings)
    {
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(draft.Start) ||
            !double.IsFinite(draft.End) || draft.Start < 0 || draft.End < 0 || draft.End > duration + .001)
            throw new ArgumentException("分割区间无效或超出视频时长。");
        var end = draft.End > 0 ? Math.Min(draft.End, duration) : duration;
        var span = end - draft.Start;
        if (span <= 0) throw new ArgumentException("分割区间必须有有效时长。");
        var points = new List<double> { draft.Start };
        switch (settings.Mode)
        {
            case ClipSplitMode.EqualParts:
                if (settings.Parts is < 2 or > MaximumSegments)
                    throw new ArgumentException($"段数须在 2–{MaximumSegments} 之间。");
                for (var i = 1; i < settings.Parts; i++) points.Add(draft.Start + span * ((double)i / settings.Parts));
                break;
            case ClipSplitMode.FixedDuration:
                var step = settings.SegmentSeconds;
                if (!double.IsFinite(step) || step <= 0 || step >= span)
                    throw new ArgumentException("每段时长必须大于零并小于当前区间时长，至少分为两段。");
                var ratio = span / step;
                // Avoid a phantom tail caused solely by floating-point arithmetic (e.g. 0.3 / 0.1).
                if (Math.Round(ratio) >= 2 && Math.Abs(ratio - Math.Round(ratio)) <= 1e-10) ratio = Math.Round(ratio);
                if (!double.IsFinite(ratio) || Math.Ceiling(ratio) > MaximumSegments)
                    throw new ArgumentException($"最多分割为 {MaximumSegments} 段，请增大每段时长。");
                for (var i = 1; i < (int)Math.Ceiling(ratio); i++) points.Add(draft.Start + i * step);
                break;
            case ClipSplitMode.TimePoints:
                var cuts = settings.TimePoints?.ToArray() ?? [];
                if (cuts.Length is < 1 or >= MaximumSegments)
                    throw new ArgumentException($"请输入 1–{MaximumSegments - 1} 个分割时间点。");
                points.AddRange(cuts);
                break;
            default:
                throw new ArgumentException("请选择有效的分割方式。");
        }
        points.Add(end);
        for (var i = 1; i < points.Count; i++)
            if (!double.IsFinite(points[i]) || points[i] <= points[i - 1] || points[i] > end)
                throw new ArgumentException("时间点须按顺序严格递增，且位于当前区间内；不能重复或形成零时长片段。");
        return Enumerable.Range(0, points.Count - 1).Select(i =>
        {
            var segment = draft.Clone(); segment.Start = points[i]; segment.End = points[i + 1]; return segment;
        }).ToArray();
    }

    public static double ParseTime(string? value)
    {
        var text = value?.Trim() ?? "";
        var fields = text.Split(':');
        if (fields.Length is < 1 or > 3) throw new ArgumentException("时间请输入秒数、MM:SS 或 HH:MM:SS，可含小数秒。");
        var numbers = new double[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!double.TryParse(fields[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out numbers[i]) ||
                !double.IsFinite(numbers[i]) || numbers[i] < 0 ||
                (i < fields.Length - 1 && numbers[i] != Math.Truncate(numbers[i])) ||
                (fields.Length > 1 && i == fields.Length - 1 && numbers[i] >= 60) ||
                (fields.Length == 3 && i == 1 && numbers[i] >= 60))
                throw new ArgumentException("时间格式无效；使用小数点，冒号后的分钟和秒须小于 60。");
        }
        var result = numbers.Aggregate(0d, (sum, number) => sum * 60 + number);
        if (!double.IsFinite(result)) throw new ArgumentException("时间数值过大。");
        return result;
    }

    public static double[] ParsePoints(string? value) => (value ?? "")
        .Split([',', '，', ';', '；', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ParseTime).ToArray();
}
