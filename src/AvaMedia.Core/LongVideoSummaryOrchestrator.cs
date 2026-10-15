using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record PublishedSummarySegment(string RunHash, SegmentOutput Output);
public sealed record SummaryRunIdentity(string RunHash, string PlanHash);
public sealed class LongVideoSummaryOrchestrator(ISummaryModel model, TokenBudget budget, ConcurrencyGate gate,
    ResponseCache? responseCache = null, string? modelIdentity = null)
{
    public static bool UsesLongPath(double duration, VideoSummaryOptions options) => options.NeedsAi && duration > options.LongVideoThresholdMinutes * 60;
    public async Task<LongSummaryReport> RunAsync(Job job, string folder, string signalHash,
        Func<SummaryCheckpointStore, CancellationToken, Task<SummarySignals>> extract, Action<double> progress,
        CancellationToken ct, Action<SummarySegmentProgress[]>? segmentProgress = null)
    {
        var stages = job.VideoSummaryStages;
        job.OutputIsPartial = true;
        var options = job.Options.VideoSummary!; var store = new SummaryCheckpointStore(Path.Combine(folder, "work"));
        var activity = new AiActivityReporter(a => job.Activity = a, model.Backend, "段", ["提取信号", "分段", "逐段理解", "章节归纳", "核验", "输出"]);
        var outputs = new Dictionary<string, SegmentOutput>(); var states = new Dictionary<string, SummarySegmentProgress>();
        var publishGate = new SemaphoreSlim(1, 1); SegmentPlan? plan = null; string runHash = ""; bool finished = false;
        LongSummaryReport Partial() => new(plan?.Duration ?? job.Duration, options.OutputLanguage, "", [], [],
            new(plan is null ? [] : CoverageValidator.Gaps(plan.Segments.Select(s => (s.Start, s.End)), plan.Duration), outputs.Values.Where(s => s.Result.Status == "degraded").Select(s => s.Range.Id).OrderBy(s => s).ToArray())) { IsPartial = true };
        void Notify()
        {
            var snapshot = states.Values.OrderBy(s => s.Start).ToArray(); SummaryProgressStore.Set(job.Id.ToString(), snapshot);
            stages?.Update(VideoSummaryStageIds.UnderstandSegments, outputs.Count, plan?.Segments.Count, "段", $"已理解 {outputs.Count} 段");
            segmentProgress?.Invoke(snapshot); job.SummarySegments = outputs.Values.OrderBy(s => s.Range.Start).ToArray();
        }
        async Task State(SegmentRange range, string state)
        {
            await publishGate.WaitAsync(CancellationToken.None);
            try { states[range.Id] = states[range.Id] with { State = state }; Notify(); }
            finally { publishGate.Release(); }
        }
        async Task Publish(SegmentJob input, SegmentResult result, bool cached, string hash)
        {
            var range = plan!.Segments.First(r => r.Id == input.SegmentId); var output = new SegmentOutput(range, result);
            // Once validated, preserve this result even if cancellation arrives between inference and publication.
            await store.WriteAsync($"map/{range.Id}.json", hash, new PublishedSummarySegment(runHash, output), CancellationToken.None);
            await publishGate.WaitAsync(CancellationToken.None);
            try
            {
                outputs[range.Id] = output; states[range.Id] = new(range.Id, range.Start, range.End, result.Status == "degraded" ? "degraded" : cached ? "cached" : "done", result.Summary);
                Notify(); job.LongSummaryReport = Partial(); await SaveReportAsync(folder, job.LongSummaryReport, CancellationToken.None);
                activity.Result(MediaTime.Format(range.Start) + " · " + result.Summary, outputs.Count); progress(30 + 40d * outputs.Count / plan.Segments.Count);
            }
            finally { publishGate.Release(); }
        }
        try
        {
            activity.Node("提取信号"); activity.Stage("提取信号"); progress(0);
            var signals = await store.GetOrCreateAsync("signals.json", signalHash, cancellation => extract(store, cancellation), ct);
            stages?.Start(VideoSummaryStageIds.PlanSegments);
            job.Duration = signals.Duration; progress(25); activity.Node("分段");
            var visualOnly = LongSummaryMediaPolicy.VisualOnly(signals);
            var planInput = SummaryCheckpointStore.Hash(new { signalHash, signals = SummaryCheckpointStore.Hash(signals), budget, options.Focus, visualOnly });
            plan = await store.GetOrCreateAsync("segments.json", planInput, _ => Task.FromResult(new SemanticSegmenter().Plan(signals, budget, options.Focus, visualOnly)), ct);
            CoverageValidator.Assert(plan.Segments.Select(s => (s.Start, s.End)), signals.Duration);
            runHash = SummaryCheckpointStore.Hash(new { model = modelIdentity ?? model.ModelId, plan.PlanHash, budget, options.Focus, options.OutputLanguage, options.StrictSequential });
            await store.WriteAsync("run.json", runHash, new SummaryRunIdentity(runHash, plan.PlanHash), ct);
            foreach (var range in plan.Segments) states[range.Id] = new(range.Id, range.Start, range.End, "pending", "");
            Notify(); job.LongSummaryReport = Partial(); await SaveReportAsync(folder, job.LongSummaryReport, ct);
            var evidence = new EvidenceStore(folder);
            var jobs = plan.Segments.Select(r => SemanticSegmenter.BuildJob(r, signals, budget, options.Focus, visualOnly) with { OutputLanguage = options.OutputLanguage }).ToArray();
            foreach (var input in jobs) await evidence.WriteSegmentAsync(input, ct);
            stages?.Update(VideoSummaryStageIds.PlanSegments, plan.Segments.Count, null, "段", $"{plan.Segments.Count} 段");
            stages?.Complete(VideoSummaryStageIds.PlanSegments, $"{plan.Segments.Count} 段");
            stages?.Start(VideoSummaryStageIds.UnderstandSegments);
            stages?.Update(VideoSummaryStageIds.UnderstandSegments, outputs.Count, plan.Segments.Count, "段", $"已理解 {outputs.Count} 段");
            activity.Node("逐段理解"); progress(30);
            var mapper = new SegmentMapper(model, budget, responseCache ?? new(), gate, modelIdentity);
            RollingContext Context(SegmentOutput? previous) => previous is null ? RollingContext.Empty : new(previous.Result.Summary,
                previous.Result.OpenQuestions.Concat(previous.Result.ThreadsOut).Take(5).ToArray(), outputs.Values.SelectMany(s => s.Result.Entities).GroupBy(e => e.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10).Select(g => g.Key).ToArray());
            async Task Map(SegmentJob input)
            {
                var hash = SummaryCheckpointStore.Hash(new { runHash, input, promptVersion = 1 });
                var saved = await store.ReadAsync<PublishedSummarySegment>($"map/{input.SegmentId}.json", hash, ct);
                if (saved is not null && SegmentResultValidator.Validate(input, saved.Output.Result).Length == 0)
                { await Publish(input, saved.Output.Result, true, hash); return; }
                await State(plan.Segments.First(r => r.Id == input.SegmentId), "running");
                var result = await mapper.MapAsync(input, ct); await Publish(input, result.Result, result.Cached, hash);
            }
            if (options.StrictSequential)
                foreach (var input in jobs) { ct.ThrowIfCancellationRequested(); await Map(input with { Context = Context(outputs.Values.OrderBy(s => s.Range.Start).LastOrDefault()) }); }
            else
            {
                using var work = new SemaphoreSlim(options.Provider == VideoSummaryProvider.Local ? 1 : options.LongSummaryConcurrency);
                await Task.WhenAll(jobs.Select(async input => { await work.WaitAsync(ct); try { await Map(input); } finally { work.Release(); } }));
                // Repair only cross-segment threads. Existing published results remain available while the repair runs.
                for (int i = 1; i < jobs.Length; i++)
                {
                    ct.ThrowIfCancellationRequested(); var previous = outputs[jobs[i - 1].SegmentId];
                    if (previous.Result.OpenQuestions.Length == 0 && previous.Result.ThreadsOut.Length == 0) continue;
                    var input = jobs[i] with { Context = Context(previous) }; var repairHash = SummaryCheckpointStore.Hash(new { runHash, input, promptVersion = 1 });
                    var repaired = await store.ReadAsync<PublishedSummarySegment>($"map/{input.SegmentId}-repair.json", repairHash, ct);
                    if (repaired is null)
                    {
                        var result = await mapper.MapAsync(input, ct); repaired = new(runHash, new(plan.Segments[i], result.Result));
                        if (repaired.Output.Result.Status == "degraded" && outputs[input.SegmentId].Result.Status == "done") continue;
                        await store.WriteAsync($"map/{input.SegmentId}-repair.json", repairHash, repaired, CancellationToken.None);
                    }
                    // Keep the first-pass checkpoint reusable; publish the repaired value separately.
                    await publishGate.WaitAsync(CancellationToken.None);
                    try { outputs[input.SegmentId] = repaired.Output; states[input.SegmentId] = states[input.SegmentId] with { Summary = repaired.Output.Result.Summary, State = repaired.Output.Result.Status == "degraded" ? "degraded" : "done" }; Notify(); }
                    finally { publishGate.Release(); }
                }
            }
            stages?.Complete(VideoSummaryStageIds.UnderstandSegments, $"{outputs.Count} 段 · {outputs.Values.Count(s => s.Result.Status == "degraded")} 段降级");
            stages?.Start(VideoSummaryStageIds.ReduceChapters);
            activity.Node("章节归纳"); progress(72);
            var cleaned = CrossSegmentDeduper.Clean(outputs.Values.OrderBy(s => s.Range.Start).ToArray());
            var groups = ChapterBoundaryPlanner.Plan(cleaned, signals.Duration); var chapters = new List<ChapterNode>();
            var reducer = new HierarchicalReducer(model, budget, gate, store);
            foreach (var group in groups)
            {
                chapters.Add(await reducer.ReduceAsync(group.Select(HierarchicalReducer.FromSegment).ToArray(), $"c{chapters.Count:000}", options.OutputLanguage, ct));
                stages?.Update(VideoSummaryStageIds.ReduceChapters, chapters.Count, groups.Count, "章", $"已归纳 {chapters.Count} 章");
            }
            stages?.Complete(VideoSummaryStageIds.ReduceChapters, $"{chapters.Count} 章");
            stages?.Start(VideoSummaryStageIds.VerifyChapters);
            activity.Node("核验"); progress(85); var verifier = new ChapterVerifier(model, budget, gate, store);
            for (int i = 0; i < chapters.Count; i++)
            {
                chapters[i] = await verifier.VerifyAsync(chapters[i], evidence.LoadAsync, options.OutputLanguage, ct);
                stages?.Update(VideoSummaryStageIds.VerifyChapters, i + 1, chapters.Count, "章", $"已核验 {i + 1} 章");
            }
            CoverageValidator.Assert(chapters.Select(c => (c.Start, c.End)), signals.Duration);
            stages?.Complete(VideoSummaryStageIds.VerifyChapters, $"{chapters.Count} 章");
            stages?.Start(VideoSummaryStageIds.Summarize);
            // The full summary sees only chapter claims that survived verification.
            var full = await reducer.ReduceAsync(chapters, "full", options.OutputLanguage, ct);
            stages?.Complete(VideoSummaryStageIds.Summarize, $"{full.KeyPoints.Length} 条要点");
            stages?.Start(VideoSummaryStageIds.SaveResults);
            activity.Node("输出"); progress(97);
            var report = new LongSummaryReport(signals.Duration, options.OutputLanguage, full.Summary, full.KeyPoints, chapters.ToArray(),
                new([], cleaned.Where(s => s.Result.Status == "degraded").Select(s => s.Range.Id).ToArray()))
                { Limitations = visualOnly && !signals.OcrAvailable ? ["无语音模式未配置 OCR 引擎；只描述可见内容，标题文字覆盖可能不完整。"] : [] };
            await SaveReportAsync(folder, report, ct); await File.WriteAllTextAsync(Path.Combine(folder, "summary.md"), Markdown(report, outputs.Values), new UTF8Encoding(false), ct);
            job.LongSummaryReport = report; if (stages is null) job.OutputIsPartial = false; finished = true; progress(100); return report;
        }
        catch (OperationCanceledException)
        {
            activity.Stage("已暂停，可继续"); job.ProgressDetail = "已暂停，可继续"; throw;
        }
        finally
        {
            if (!finished && plan is not null)
            {
                foreach (var id in states.Where(p => p.Value.State == "running").Select(p => p.Key).ToArray()) states[id] = states[id] with { State = ct.IsCancellationRequested ? "pending" : "failed" };
                Notify(); job.LongSummaryReport = Partial(); await SaveReportAsync(folder, job.LongSummaryReport, CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(folder, "segments.md"), Markdown(job.LongSummaryReport, outputs.Values), CancellationToken.None);
            }
            publishGate.Dispose();
        }
    }
    public static async Task SaveReportAsync(string folder, LongSummaryReport report, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, LongSummaryJson.Options);
        if (bytes.Length >= 4 * 1024 * 1024) throw new InvalidDataException("总结报告超过 4 MB。");
        await SummaryCheckpointStore.AtomicWriteAsync(Path.Combine(folder, "report.json"), bytes, ct);
    }
    public static async Task<LongSummaryTaskSnapshot?> LoadAsync(string folder, CancellationToken ct = default)
    {
        var reportPath = Path.Combine(folder, "report.json"); if (!File.Exists(reportPath)) return null;
        var bytes = await File.ReadAllBytesAsync(reportPath, ct); if (bytes.Length >= 4 * 1024 * 1024) return null;
        using var json = JsonDocument.Parse(bytes);
        if (!json.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != 2) return null;
        var report = json.RootElement.Deserialize<LongSummaryReport>(LongSummaryJson.Options)!;
        var store = new SummaryCheckpointStore(Path.Combine(folder, "work"));
        var runPath = store.PathFor("run.json");
        if (!File.Exists(runPath)) return new(report, [], 0);
        var run = await store.ReadAvailableAsync<SummaryRunIdentity>("run.json", ct);
        if (run is null) return new(report, [], 0);
        var mapFolder = store.PathFor("map"); var outputs = new Dictionary<string, SegmentOutput>();
        if (Directory.Exists(mapFolder)) foreach (var path in Directory.EnumerateFiles(mapFolder, "*.json").OrderBy(p => p.Contains("-repair.json") ? 1 : 0))
        {
            var published = await store.ReadAvailableAsync<PublishedSummarySegment>(Path.GetRelativePath(store.Root, path), ct);
            if (published?.RunHash == run.RunHash) outputs[published.Output.Range.Id] = published.Output;
        }
        return new(report, outputs.Values.OrderBy(s => s.Range.Start).ToArray(), outputs.Count);
    }
    public static string Markdown(LongSummaryReport report, IEnumerable<SegmentOutput> segments)
    {
        var text = new StringBuilder("# 视频总结\n\n"); if (report.IsPartial) text.Append("部分结果 · 已完成分段可独立使用\n\n");
        if (report.Summary.Length > 0) text.Append(report.Summary).Append("\n\n");
        foreach (var chapter in report.Chapters) text.Append("## ").Append(MediaTime.Format(chapter.Start)).Append(" · ").Append(chapter.Title).Append("\n\n").Append(chapter.Summary).Append("\n\n");
        foreach (var segment in segments.OrderBy(s => s.Range.Start))
        {
            text.Append("## ").Append(segment.Range.Id).Append(" · ").Append(MediaTime.Format(segment.Range.Start)).Append(" – ").Append(MediaTime.Format(segment.Range.End)).Append("\n\n").Append(segment.Result.Summary).Append("\n\n");
            foreach (var claim in segment.Result.Claims) text.Append("- ").Append(claim.Text).Append(" 〔").Append(string.Join("、", claim.Evidence)).Append("〕\n");
            foreach (var ev in segment.Result.Events) text.Append("- ").Append(MediaTime.Format(ev.Start)).Append(" – ").Append(MediaTime.Format(ev.End)).Append(" ").Append(ev.Text).Append("\n");
            text.Append('\n');
        }
        return text.ToString();
    }
}
