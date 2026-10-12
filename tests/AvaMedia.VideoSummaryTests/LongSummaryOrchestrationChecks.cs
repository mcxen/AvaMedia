using System.Text.Json;
using AvaMedia.Core;

internal static class LongSummaryOrchestrationChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aisum-e2e-" + Guid.NewGuid().ToString("N"));
        var budget = TokenBudget.Local8K; var fixture = SyntheticLongVideoFixture.Create(); var mapCalls = new Dictionary<string, int>();
        try
        {
            int extracted = 0; bool failReduce = false;
            var model = new LongSummaryMapChecks.FakeModel((prompt, _, _) =>
            {
                budget.Assert(prompt, new ConservativeTokenCounter()); using var json = JsonDocument.Parse(prompt);
                if (json.RootElement.TryGetProperty("segmentId", out var segment))
                { var id = segment.GetString()!; mapCalls[id] = mapCalls.GetValueOrDefault(id) + 1; var valid = LongSummaryMapChecks.Valid(prompt); return failReduce ? LongSummaryJson.Write(JsonSerializer.Deserialize<SegmentResult>(valid, LongSummaryJson.Options)! with { Summary = "new aggregation input" }) : valid; }
                if (json.RootElement.TryGetProperty("items", out _)) return LongSummaryVerifyChecks.Reply(prompt, budget);
                if (failReduce) throw new IOException("simulated reduce failure");
                return LongSummaryReduceChecks.Reply(prompt, budget);
            });
            var job = new Job { Options = new() { VideoSummary = new() { AnalyzeFrames = false } } };
            var orchestrator = new LongVideoSummaryOrchestrator(model, budget, new(model.ModelId, true), new(Path.Combine(root, "cache")));
            var history = new List<SummarySegmentProgress[]>(); using var cancellation = new CancellationTokenSource();
            Task<SummarySignals> Extract(SummaryCheckpointStore _, CancellationToken ct) { extracted++; ct.ThrowIfCancellationRequested(); return Task.FromResult(fixture); }
            try
            {
                await orchestrator.RunAsync(job, root, "synthetic-three-hour-v1", Extract, _ => { }, cancellation.Token, segments =>
                {
                    history.Add(segments); if (segments.Count(s => s.State == "done") == 5) cancellation.Cancel();
                });
            }
            catch (OperationCanceledException) { }
            LongSummaryPhaseChecks.Check(job.LongSummaryReport?.IsPartial == true && job.SummarySegments.Length == 5 && File.Exists(Path.Combine(root, "segments.md")), "cancelled report preserves independent segment export");
            var partial = await LongVideoSummaryOrchestrator.LoadAsync(root);
            LongSummaryPhaseChecks.Check(partial?.Segments.Length == 5 && partial.Segments.All(s => s.Result.Summary.Length > 0), "restart can read all published map results before Reduce");
            var completedIds = mapCalls.Keys.ToArray(); var stages = new List<string>();
            job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Job.Activity) && job.Activity is { } a) stages.Add(a.Stage); };
            var result = await orchestrator.RunAsync(job, root, "synthetic-three-hour-v1", Extract, _ => { }, default, segments => history.Add(segments));
            LongSummaryPhaseChecks.Check(extracted == 1 && completedIds.All(id => mapCalls[id] == 1) && mapCalls.Count == 45, "resume does not repeat signal extraction or finished segments");
            LongSummaryPhaseChecks.Check(!result.IsPartial && result.Coverage.Gaps.Length == 0 && new FileInfo(Path.Combine(root, "report.json")).Length < 4 * 1024 * 1024, "full report bounded and covered");
            var fullReportBytes = new FileInfo(Path.Combine(root, "report.json")).Length;
            CoverageValidator.Assert(result.Chapters.Select(c => (c.Start, c.End)), fixture.Duration);
            LongSummaryPhaseChecks.Check(history.Any(h => h.All(s => s.State == "pending")) && history.Any(h => h.Any(s => s.State == "running")) && history.Any(h => h.Any(s => s.State == "cached")) && history.Last().All(s => s.State is "done" or "cached" or "degraded"), "pending/running/cached/done progress sequence");
            LongSummaryPhaseChecks.Check(new[] { "提取信号", "分段", "逐段理解", "章节归纳", "核验", "输出" }.All(stage => stages.Contains(stage)), "six pipeline stages");
            var citation = job.SummarySegments[0].Result.Claims[0].Evidence[0]; var evidence = await new EvidenceStore(root).LoadAsync(citation, default);
            LongSummaryPhaseChecks.Check(evidence?.Text == fixture.Cues[0].Text, "lazy JSONL citation loading");
            LongSummaryPhaseChecks.Check(await LongVideoSummaryOrchestrator.LoadAsync(root) is { Segments.Length: 45, Report.Version: 2 }, "v2 result opens from disk");
            LongSummaryPhaseChecks.Check(!LongVideoSummaryOrchestrator.UsesLongPath(1200, job.Options.VideoSummary!) && !LongVideoSummaryOrchestrator.UsesLongPath(1199, job.Options.VideoSummary!) && LongVideoSummaryOrchestrator.UsesLongPath(1200.1, job.Options.VideoSummary!), "20 minute short path boundary");
            // A different focus reruns Map while still reusing the signal checkpoint.
            job.Options.VideoSummary!.Focus = "different focus";
            await orchestrator.RunAsync(job, root, "synthetic-three-hour-v1", Extract, _ => { }, default);
            LongSummaryPhaseChecks.Check(extracted == 1 && mapCalls.Values.All(n => n == 2), "focus invalidates only downstream stages");
            failReduce = true; job.Options.VideoSummary.Focus = "fail aggregation";
            try { await orchestrator.RunAsync(job, root, "synthetic-three-hour-v1", Extract, _ => { }, default); } catch (IOException) { }
            var failed = await LongVideoSummaryOrchestrator.LoadAsync(root);
            LongSummaryPhaseChecks.Check(failed is { Report.IsPartial: true, Segments.Length: 45 } && File.ReadAllText(Path.Combine(root, "segments.md")).Contains("s0044"), "Reduce failure preserves and exports every segment");
            var rollingModel = new LongSummaryMapChecks.FakeModel((prompt, _, _) =>
            {
                using var json = JsonDocument.Parse(prompt);
                if (json.RootElement.TryGetProperty("segmentId", out var segment))
                {
                    var input = json.RootElement.Deserialize<SegmentJob>(LongSummaryJson.Options)!;
                    if (input.Context.OpenThreads.Count > 0) return "{invalid repair";
                    var value = JsonSerializer.Deserialize<SegmentResult>(LongSummaryMapChecks.Valid(prompt), LongSummaryJson.Options)!;
                    return LongSummaryJson.Write(value with { Summary = "first-pass result", ThreadsOut = segment.GetString() == "s0000" ? ["未闭合线索"] : [] });
                }
                return json.RootElement.TryGetProperty("items", out _) ? LongSummaryVerifyChecks.Reply(prompt, budget) : LongSummaryReduceChecks.Reply(prompt, budget);
            });
            var rollingJob = new Job { Options = new() { VideoSummary = new() } };
            await new LongVideoSummaryOrchestrator(rollingModel, budget, new(rollingModel.ModelId, true), new(Path.Combine(root, "rolling-cache")))
                .RunAsync(rollingJob, Path.Combine(root, "rolling"), "fixture", (_, _) => Task.FromResult(fixture), _ => { }, default);
            LongSummaryPhaseChecks.Check(rollingJob.SummarySegments[1].Result is { Summary: "first-pass result", Status: "done" }, "failed rolling-context repair keeps published first pass usable");
            int opens = 0;
            Task<ISummaryModel> Open(CancellationToken _) { opens++; return Task.FromResult<ISummaryModel>(model); }
            var key = Guid.NewGuid().ToString();
            var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SharedSummaryModelLease.AcquireAsync(key, Open, default)));
            LongSummaryPhaseChecks.Check(opens == 1 && leases.All(l => l.ModelId == model.ModelId), "parallel local tasks share one model lease");
            foreach (var lease in leases) await lease.DisposeAsync();
            LongSummaryPhaseChecks.Check(model.Disposals == 1, "shared model released once after final reader");
            Console.WriteLine($"PASS Phase 7: synthetic 3h E2E, cancellation/resume, incremental publication, evidence loading, focus invalidation, Reduce failure; full report {fullReportBytes} bytes");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
