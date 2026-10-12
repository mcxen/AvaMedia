using System.Text.Json;
using AvaMedia.Core;

internal static class LongSummaryMapChecks
{
    internal sealed class FakeModel(Func<string, int, int, string> response) : ISummaryModel
    {
        private int _calls, _active, _peak;
        public int Calls => _calls; public int Peak => _peak; public int Disposals { get; private set; }
        public string ModelId { get; } = "fake-" + Guid.NewGuid().ToString("N");
        public string Backend => "fake";
        public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
            int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
        {
            var count = Interlocked.Increment(ref _calls); var active = Interlocked.Increment(ref _active);
            int old; do { old = _peak; if (old >= active) break; } while (Interlocked.CompareExchange(ref _peak, active, old) != old);
            try { await Task.Delay(2, ct); return response(prompt, count, tokens); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    internal static string Valid(string prompt)
    {
        var job = JsonSerializer.Deserialize<SegmentJob>(prompt, LongSummaryJson.Options)!;
        var cue = job.Transcript.FirstOrDefault(c => !c.ContextOnly);
        var frame = job.Frames.FirstOrDefault(); var id = cue?.Id ?? frame?.Id;
        return LongSummaryJson.Write(new SegmentResult("本段介绍步骤。", [], id is null ? [] : [new("c1", cue?.Text ?? frame!.Caption, [id], "high")], [], [], [], "步骤"));
    }
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aisum-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            var delays = new List<TimeSpan>();
            var transient = new FakeModel((p, i, _) => i switch { 1 => throw new OnlineAiClient.ProviderHttpException(429, "limited", TimeSpan.FromSeconds(3)), 2 => throw new TimeoutException(), 3 => throw new OnlineAiClient.ProviderHttpException(503, "retry"), _ => Valid(p) });
            var retry = new RetryingSummaryModel(transient, (d, _) => { delays.Add(d); return Task.CompletedTask; });
            var source = new SegmentJob("s0000", 0, 240, "zh", RollingContext.Empty, [new("t0000-000", 0, 5, "开始介绍步骤")], [], "");
            var mapper = new SegmentMapper(retry, TokenBudget.Local8K, new(root), new(transient.ModelId, false, 2));
            var first = await mapper.MapAsync(source, default);
            LongSummaryPhaseChecks.Check(first.Result.Status == "done" && transient.Calls == 4 && delays.Count == 3 && delays[0].TotalSeconds == 3, "429/timeout/503 retries and Retry-After");
            var before = transient.Calls; var second = await mapper.MapAsync(source, default);
            LongSummaryPhaseChecks.Check(second.Cached && transient.Calls == before, "second run zero model calls");
            var bad = new FakeModel((p, i, _) => i == 1 ? "{bad JSON" : Valid(p));
            var repaired = await new SegmentMapper(bad, TokenBudget.Local8K, new(root), new(bad.ModelId, false)).MapAsync(source, default);
            LongSummaryPhaseChecks.Check(repaired.Result.Status == "done" && bad.Calls == 2, "one JSON repair");
            var malformed = new FakeModel((p, i, _) => i == 1 ? throw new InvalidDataException("provider returned malformed JSON") : Valid(p));
            var restored = await new SegmentMapper(malformed, TokenBudget.Local8K, new(root), new(malformed.ModelId, true)).MapAsync(source, default);
            LongSummaryPhaseChecks.Check(restored.Result.Status == "done" && malformed.Calls == 2, "malformed model response repaired without aborting task");
            var broken = new FakeModel((_, _, _) => "{}");
            var degraded = await new SegmentMapper(broken, TokenBudget.Local8K, new(root), new(broken.ModelId, true)).MapAsync(source, default);
            LongSummaryPhaseChecks.Check(degraded.Result.Status == "degraded" && broken.Calls == 2, "invalid contract degraded without abort");
            var length = new FakeModel((p, _, tokens) => tokens < 2048 ? throw new SummaryOutputLimitException("length") : Valid(p));
            var completed = await new SegmentMapper(length, TokenBudget.Local8K, new(root), new(length.ModelId, true)).MapAsync(source, default);
            LongSummaryPhaseChecks.Check(completed.Result.Status == "done" && length.Calls == 2, "length output growth");
            var split = new FakeModel((p, _, _) => JsonSerializer.Deserialize<SegmentJob>(p, LongSummaryJson.Options)!.End - JsonSerializer.Deserialize<SegmentJob>(p, LongSummaryJson.Options)!.Start > 120 ? throw new SummaryOutputLimitException("length") : Valid(p));
            var splitResult = await new SegmentMapper(split, TokenBudget.Local8K, new(root), new(split.ModelId, true)).MapAsync(source, default);
            LongSummaryPhaseChecks.Check(splitResult.Result.Status == "done" && split.Calls == 4, "persistent length binary split");
            var parallel = new FakeModel((p, _, _) => Valid(p)); var parallelMapper = new SegmentMapper(parallel, TokenBudget.Local8K, new(root), new(parallel.ModelId, false, 2));
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => parallelMapper.MapAsync(source with { SegmentId = "s" + i }, default)));
            LongSummaryPhaseChecks.Check(parallel.Peak <= 2 && parallel.Calls == 12, "concurrency maximum");
            var forbidden = new FakeModel((_, _, _) => throw new OnlineAiClient.ProviderHttpException(400, "content"));
            try { await new RetryingSummaryModel(forbidden).CompleteAsync("", "", default); } catch (OnlineAiClient.ProviderHttpException) { }
            LongSummaryPhaseChecks.Check(forbidden.Calls == 1, "400 not retried");
            var invalid = new SegmentResult("ok", [new(-1, 5, "bad", ["context-id"])], [], [], [], [], "topic");
            LongSummaryPhaseChecks.Check(SegmentResultValidator.Validate(source, invalid).Length > 0, "context citation and ownership rejected");
            Console.WriteLine("PASS Phase 4: transient retries, cache, JSON repair/degradation, length growth/split, concurrency, content errors");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
