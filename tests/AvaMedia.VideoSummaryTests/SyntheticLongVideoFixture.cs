using AvaMedia.Core;

internal static class SyntheticLongVideoFixture
{
    public static SummarySignals Create(double duration = 10800, bool silent = false)
    {
        var cues = silent ? [] : Enumerable.Range(0, (int)(duration / 5)).Select(i =>
            new SummaryCue($"source-{i}", i * 5, i * 5 + 4, $"主题{i / 96} 讨论 project{i / 96} 的步骤与结果。", Language: "zh")).ToArray();
        var shots = ShotDetector.FromBoundaries(duration, Enumerable.Range(1, (int)(duration / 60) - 1).Select(i => new ShotBoundary(i * 60, i % 4 == 0 ? 1 : .2)));
        var voice = cues.Select(c => new VoiceInterval(c.Start, c.End)).ToArray();
        var frames = shots.Select(s => new SummaryFrame(s.Id, (s.Start + s.End) / 2, s.Id, "演示步骤", $"标题{s.Id}")).ToArray();
        return new(duration, cues, shots, voice, frames);
    }
    public static Task CheckAsync()
    {
        var signals = Create(); var segmenter = new SemanticSegmenter(); var packer = new PromptPacker();
        foreach (var budget in new[] { TokenBudget.Local8K, TokenBudget.For(new("32K", 32768)), TokenBudget.For(new("128K", 131072)) })
        {
            var plan = segmenter.Plan(signals, budget); var again = segmenter.Plan(signals, budget);
            LongSummaryPhaseChecks.Check(LongSummaryJson.Write(plan) == LongSummaryJson.Write(again), "deterministic segmentation");
            double end = 0;
            foreach (var range in plan.Segments)
            {
                LongSummaryPhaseChecks.Check(range.Start == end && range.End - range.Start is >= 60 and <= 480, "continuous segment lengths");
                LongSummaryPhaseChecks.Check(range.End == signals.Duration || signals.Cues.Any(c => c.Start == range.End), "cue boundary alignment");
                var job = SemanticSegmenter.BuildJob(range, signals, budget);
                LongSummaryPhaseChecks.Check(packer.Estimate(job) <= budget.Input, "raw segment input budget");
                LongSummaryPhaseChecks.Check(job.Transcript.Count(c => c.ContextOnly) <= 2 && job.Transcript.Where(c => c.ContextOnly).All(c => c.Start >= range.Start - 20), "context overlap bound");
                end = range.End;
            }
            LongSummaryPhaseChecks.Check(end == 10800, "three hour coverage");
            Console.WriteLine($"PASS Phase 3: synthetic 3h, {plan.Segments.Count} segments at {budget.Context}, boundaries/coverage/budgets/determinism");
        }
        return Task.CompletedTask;
    }
}
