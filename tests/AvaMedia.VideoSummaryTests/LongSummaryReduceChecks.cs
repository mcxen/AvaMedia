using System.Text.Json;
using AvaMedia.Core;

internal static class LongSummaryReduceChecks
{
    internal static string Reply(string prompt, TokenBudget budget)
    {
        LongSummaryPhaseChecks.Check(new ConservativeTokenCounter().Count(prompt) <= budget.Input, "every Reduce request bounded");
        using var json = JsonDocument.Parse(prompt);
        var nodes = json.RootElement.GetProperty("children").Deserialize<ChapterNode[]>(LongSummaryJson.Options)!;
        return LongSummaryJson.Write(new { title = "章节", summary = "归纳已提供的内容。", keyPoints = nodes.SelectMany(n => n.KeyPoints).Take(5).ToArray() });
    }
    public static async Task RunAsync()
    {
        var signals = SyntheticLongVideoFixture.Create(); var budget = TokenBudget.Local8K;
        var plan = new SemanticSegmenter().Plan(signals, budget);
        var outputs = plan.Segments.Select(r => new SegmentOutput(r, SegmentMapper.Degraded(SemanticSegmenter.BuildJob(r, signals, budget)))).ToArray();
        var groups = ChapterBoundaryPlanner.Plan(outputs, signals.Duration); var model = new LongSummaryMapChecks.FakeModel((p, _, _) => Reply(p, budget));
        var reducer = new HierarchicalReducer(model, budget, new(model.ModelId, true)); var chapters = new List<ChapterNode>();
        foreach (var group in groups) chapters.Add(await reducer.ReduceAsync(group.Select(HierarchicalReducer.FromSegment).ToArray(), "c" + chapters.Count, "zh", default));
        CoverageValidator.Assert(chapters.Select(c => (c.Start, c.End)), signals.Duration);
        LongSummaryPhaseChecks.Check(chapters.All(c => c.End - c.Start is >= 120 and <= 1500 && c.KeyPoints.All(p => p.Citations.Length <= 8)), "chapter durations/coverage/citations");
        var many = Enumerable.Range(0, 1000).Select(i => new ChapterNode($"s{i:0000}", i * 240, (i + 1) * 240, "topic", "segment summary", [new("point", [$"t{i:0000}-000"])], [$"t{i:0000}-000"])).ToArray();
        var root = await reducer.ReduceAsync(many, "full", "zh", default);
        LongSummaryPhaseChecks.Check(reducer.TreeHeight <= 3 && root.KeyPoints.All(p => p.Citations.Length <= 8), "1000 segments tree height <=3");
        LongSummaryPhaseChecks.Check(CoverageValidator.Gaps([(0, 60), (61, 120)], 120).Length == 1, "coverage gaps reported");
        LongSummaryPhaseChecks.Check(CitationLimiter.Limit(Enumerable.Range(0, 100).Select(i => "t" + i)).Length == 8, "citation union bounded");
        var duplicate = outputs.Take(2).Select((o, i) => o with { Result = o.Result with {
            Events = [new(o.Range.Start - (i == 1 ? 10 : 0), o.Range.Start + 10, "重复事件", ["t0"])],
            Entities = [new(i == 0 ? "产品" : "产品简称", "product", i == 0 ? ["产品简称"] : [])] } }).ToArray();
        var clean = CrossSegmentDeduper.Clean(duplicate);
        LongSummaryPhaseChecks.Check(clean[1].Result.Events.Length == 0 && clean[1].Result.Entities[0].Name == "产品", "context event ownership and entity alias normalization");
        Console.WriteLine($"PASS Phase 5: 3h/{chapters.Count} continuous chapters; 1000 segments height {reducer.TreeHeight}, all requests/citations bounded");
    }
}
