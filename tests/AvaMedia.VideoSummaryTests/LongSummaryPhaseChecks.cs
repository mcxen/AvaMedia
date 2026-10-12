using AvaMedia.Core;

internal static class LongSummaryPhaseChecks
{
    public static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); }
    public static Task RunAsync(int phase)
    {
        if (phase == 2) return LongSummaryCheckpointChecks.RunAsync();
        if (phase == 3) return SyntheticLongVideoFixture.CheckAsync();
        if (phase != 1) throw new ArgumentException("Unknown phase");
        var random = new Random(7241); var packer = new PromptPacker();
        foreach (var budget in new[] { TokenBudget.Local8K, TokenBudget.For(new("32K", 32768)), TokenBudget.For(new("128K", 131072)) })
        {
            for (var run = 0; run < 10000; run++)
            {
                string Words() => string.Concat(Enumerable.Repeat(run % 2 == 0 ? "长视频 abc 日本語 😀 \"\n" : "Latin text\t", random.Next(1, run % 1000 == 0 ? 3000 : 60)));
                var cues = Enumerable.Range(0, random.Next(1, 18)).Select(i => new SummaryCue("t" + i, i * 5, i * 5 + 4, Words(), i == 0)).ToArray();
                if (cues.Length == 1) cues[0] = cues[0] with { ContextOnly = false };
                var frames = Enumerable.Range(0, random.Next(0, 20)).Select(i => new SummaryFrame("f" + i, i, "sh", Words(), Words())).ToArray();
                var source = new SegmentJob("s0000", 0, 240, "auto", new(Words(), [Words()], [Words()]), cues, frames, Words());
                var packed = packer.Pack(source, budget);
                Check(packer.Estimate(packed) <= budget.Input, "budget");
                Check(packed.Transcript.Any(c => c.Id == cues.First(c => !c.ContextOnly).Id), "first owned cue retained");
            }
            Console.WriteLine($"PASS Phase 1: {budget.Context} context, 10000 randomized packs");
        }
        return Task.CompletedTask;
    }
}
