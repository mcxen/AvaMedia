namespace AvaMedia.Core;

public sealed record SummarySignals(double Duration, IReadOnlyList<SummaryCue> Cues, IReadOnlyList<SummaryShot> Shots,
    IReadOnlyList<VoiceInterval> VoiceIntervals, IReadOnlyList<SummaryFrame> Frames, bool OcrAvailable = false, bool HasAudio = true)
{
    public IReadOnlyList<SummaryCue>? RawCues { get; init; }
}
public sealed class SemanticSegmenter(PromptPacker? promptPacker = null)
{
    private readonly PromptPacker _packer = promptPacker ?? new();
    public const double Minimum = 60, Target = 240, Maximum = 480;
    public SegmentPlan Plan(SummarySignals signals, TokenBudget budget, string focus = "", bool visualOnly = false)
    {
        if (!double.IsFinite(signals.Duration) || signals.Duration <= 0) throw new ArgumentException("视频时长无效。");
        var cues = signals.Cues.OrderBy(c => c.Start).ThenBy(c => c.Track).ToArray();
        var topics = TopicShiftScorer.Score(cues, signals.Duration);
        var silences = signals.VoiceIntervals.OrderBy(v => v.StartSeconds).Zip(signals.VoiceIntervals.OrderBy(v => v.StartSeconds).Skip(1))
            .Where(p => p.Second.StartSeconds - p.First.EndSeconds >= 1.5).Select(p => p.Second.StartSeconds).ToArray();
        var ocrChanges = signals.Frames.OrderBy(f => f.T).Zip(signals.Frames.OrderBy(f => f.T).Skip(1))
            .Where(p => p.First.Ocr != p.Second.Ocr && p.Second.Ocr.Length > 0).Select(p => p.Second.T).ToArray();
        var boundaries = cues.Length == 0
            ? signals.Shots.Select(s => s.Start).Where(t => t > 0).Concat(Enumerable.Range(1, (int)Math.Ceiling(signals.Duration / Target)).Select(i => Math.Min(signals.Duration, i * Target))).Distinct().OrderBy(t => t).ToArray()
            : cues.Select(c => c.Start).Where(t => t > 0 && !cues.Any(c => c.Start < t && c.End > t + .001)).Append(signals.Duration).Distinct().OrderBy(t => t).ToArray();
        double Score(double t) => signals.Shots.Where(s => Math.Abs(s.Start - t) <= 5).Select(s => s.Score * .35).DefaultIfEmpty(0).Max()
            + (visualOnly ? 0 : (silences.Any(s => Math.Abs(s - t) <= 5) ? .25 : 0) + topics.Where(s => Math.Abs(s.T - t) <= 5).Select(s => s.Score * .3).DefaultIfEmpty(0).Max())
            + (ocrChanges.Any(s => Math.Abs(s - t) <= 5) ? .1 : 0);
        var segments = new List<SegmentRange>(); double start = 0;
        while (start < signals.Duration)
        {
            var candidates = boundaries.Where(end => end > start && end - start <= Maximum
                && (end - start >= Minimum || signals.Duration < Minimum)
                && (end == signals.Duration || signals.Duration - end >= Minimum))
                .Select(end => new SegmentRange($"s{segments.Count:0000}", start, end, Score(end)))
                .Where(range => _packer.Estimate(BuildJob(range, signals, budget, focus, visualOnly)) <= budget.Input)
                .OrderByDescending(range => range.BoundaryScore - .4 * Math.Abs(range.End - start - Target) / Target).ThenBy(range => range.End).ToArray();
            if (candidates.Length == 0)
                throw new InvalidDataException($"{MediaTime.Format(start)} 附近的完整字幕句无法同时满足 60–480 秒段长和输入预算；请使用更大上下文或缩短异常字幕句。");
            var chosen = candidates[0]; segments.Add(chosen); start = chosen.End;
        }
        var hash = SummaryCheckpointStore.Hash(new { signals.Duration, segments, input = SummaryCheckpointStore.Hash(signals), budget });
        return new(signals.Duration, segments.AsReadOnly(), hash);
    }
    public static SegmentJob BuildJob(SegmentRange range, SummarySignals signals, TokenBudget budget, string focus = "", bool visualOnly = false)
    {
        var owned = signals.Cues.Where(c => c.Start >= range.Start && c.Start < range.End).OrderBy(c => c.Start).ThenBy(c => c.Track).ToArray();
        var overlap = signals.Cues.Where(c => c.Start < range.Start && c.Start >= range.Start - 20).OrderBy(c => c.Start).TakeLast(2).ToArray();
        var prefix = range.Id[1..];
        var transcript = overlap.Select((c, i) => c with { Id = $"t{prefix}-ctx{i:000}", ContextOnly = true })
            .Concat(owned.Select((c, i) => c with { Id = $"t{prefix}-{i:000}", ContextOnly = false })).ToArray();
        var frames = signals.Frames.Where(f => f.T >= range.Start && f.T < range.End)
            .OrderByDescending(f => f.Ocr.Length > 0).ThenByDescending(f => signals.Shots.FirstOrDefault(s => s.Id == f.Shot) is { } shot ? shot.End - shot.Start : 0)
            .Take(budget.FrameLimit).OrderBy(f => f.T).Select((f, i) => f with { Id = $"f{prefix}-{i:00}" }).ToArray();
        var language = owned.GroupBy(c => c.Language).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "auto";
        return new(range.Id, range.Start, range.End, language, RollingContext.Empty, transcript, frames, focus, visualOnly);
    }
}
