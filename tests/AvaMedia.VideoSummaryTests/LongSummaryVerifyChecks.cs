using System.Text.Json;
using AvaMedia.Core;

internal static class LongSummaryVerifyChecks
{
    internal static string Reply(string prompt, TokenBudget budget)
    {
        LongSummaryPhaseChecks.Check(new ConservativeTokenCounter().Count(prompt) <= budget.Input, "verify input budget");
        using var json = JsonDocument.Parse(prompt);
        return LongSummaryJson.Write(new { results = json.RootElement.GetProperty("items").EnumerateArray().Select(e => new { id = e.GetProperty("id").GetString(), status = "supported" }).ToArray() });
    }
    public static async Task RunAsync()
    {
        var budget = TokenBudget.Local8K; var silent = SyntheticLongVideoFixture.Create(silent: true);
        LongSummaryPhaseChecks.Check(LongSummaryMediaPolicy.VisualOnly(silent), "no speech visual-only");
        var visualPlan = new SemanticSegmenter().Plan(silent, budget, visualOnly: true);
        LongSummaryPhaseChecks.Check(visualPlan.Segments.Count == 45, "silent clip coverage");
        var mixed = new[] { new SummaryCue("a", 0, 4, "中文", Language: "zh"), new("b", 5, 9, "English", Language: "en"), new("c", 10, 14, "日本語", Language: "ja"), new("d", 15, 19, "不确定", Language: "auto", LanguageConfidence: .1), new("e", 20, 24, "日本語", Language: "ja") };
        var repaired = await LongSummaryMediaPolicy.RepairLanguagesAsync(mixed, (c, l, _) => Task.FromResult(c with { Language = l, LanguageConfidence = .9 }), default);
        LongSummaryPhaseChecks.Check(repaired[3].Language == "ja" && repaired.Take(3).Select(c => c.Language).SequenceEqual(new[] { "zh", "en", "ja" }), "neighbor language retry preserves mixed languages");
        var tracks = LongSummaryMediaPolicy.Tracks("""{"streams":[{"codec_type":"audio","tags":{"language":"eng"},"disposition":{"default":1}},{"codec_type":"audio","tags":{"language":"jpn"},"disposition":{"default":0}}]}""", "audio");
        LongSummaryPhaseChecks.Check(LongSummaryMediaPolicy.SelectTrack(tracks, "ja") == 1 && LongSummaryMediaPolicy.SelectTrack(tracks, "zh") == 0, "preferred and default audio tracks");
        var merged = LongSummaryMediaPolicy.MergeTracks([mixed, mixed.Select(c => c with { Track = 1 })]);
        LongSummaryPhaseChecks.Check(merged.Count == 10 && merged[0].Track == 0 && merged[1].Track == 1, "both audio tracks merged by time");
        var model = new LongSummaryMapChecks.FakeModel((p, _, _) => Reply(p, budget));
        var chapter = new ChapterNode("c0", 0, 480, "title", "unverified", Enumerable.Range(0, 5).Select(i => new SummaryKeyPoint("claim" + i, ["t" + i])).ToArray(), ["t0"]);
        var verified = await new ChapterVerifier(model, budget, new(model.ModelId, true)).VerifyAsync(chapter,
            (id, _) => Task.FromResult<SummaryEvidence?>(new(id, 0, 10, new string('中', 12000), "transcript")), "zh", default);
        LongSummaryPhaseChecks.Check(verified.Status == "verified" && model.Calls > 5, "large original evidence batched within budgets");
        var statuses = new LongSummaryMapChecks.FakeModel((_, _, _) => """{"results":[{"id":"k0","status":"unsupported"},{"id":"k1","status":"partial"}]}""");
        var filtered = await new ChapterVerifier(statuses, budget, new(statuses.ModelId, true)).VerifyAsync(chapter with { KeyPoints = chapter.KeyPoints.Take(2).ToArray() },
            (id, _) => Task.FromResult<SummaryEvidence?>(new(id, 0, 10, "source", "transcript")), "zh", default);
        LongSummaryPhaseChecks.Check(filtered.KeyPoints.Length == 1 && filtered.Summary.StartsWith("资料部分表明"), "unsupported removed and partial qualified");
        Console.WriteLine("PASS Phase 6: silent/multilingual/dual-track fixtures, bounded verify batches, supported/partial/unsupported");
    }
}
