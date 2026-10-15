using SkiaSharp;
using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public sealed record PersonClipOptions(double FramesPerSecond = 2, double Threshold = .35,
    double PaddingSeconds = .5, double MergeGapSeconds = 1, double MinimumSeconds = .5,
    bool KeepUncertain = false, bool UseEmbedding = false, bool ReuseSimilarFrames = true,
    string[]? DetectorIds = null, PersonDetectionMode DetectionMode = PersonDetectionMode.Balanced,
    PersonClipRange[]? ExcludedRanges = null, bool SkipDarkFrames = true, bool SkipBlankFrames = true, double DarkLumaThreshold = 8, bool IncludeHands = false)
{
    public IReadOnlyList<string> SelectedDetectors => DetectorIds ?? [ModelCatalog.NanoDetId];
    public IReadOnlyList<string> RequiredModels => IncludeHands ? SelectedDetectors.Append(ModelCatalog.MediaPipePalmId).ToArray() : SelectedDetectors;
    public void Validate()
    {
        if (!double.IsFinite(FramesPerSecond) || FramesPerSecond is < .25 or > 16 || !double.IsFinite(Threshold) || Threshold is < .1 or > .9
            || new[] { PaddingSeconds, MergeGapSeconds, MinimumSeconds }.Any(value => !double.IsFinite(value) || value < 0 || value > 30))
            throw new ArgumentException("人物检测参数超出范围。");
        if (!Enum.IsDefined(DetectionMode) || SelectedDetectors.Count == 0 || SelectedDetectors.Count > PersonDetectorCatalog.All.Count
            || SelectedDetectors.Distinct().Count() != SelectedDetectors.Count)
            throw new ArgumentException("请至少选择一种人物检测，且不要重复选择。");
        foreach (var id in SelectedDetectors)
            if (!PersonDetectorCatalog.All.Any(detector => detector.Id == id))
                throw new ArgumentException("请选择有效的人物检测模型。");
        _ = PersonClipExclusions.Normalize(ExcludedRanges);
        if (!double.IsFinite(DarkLumaThreshold) || DarkLumaThreshold is < 1 or > 32)
            throw new ArgumentException("黑灯亮度阈值须在 1–32 之间。");
    }
}
public sealed record PersonClipProgress(double Seconds, double Duration, string Stage)
{
    public PersonClipResult? PreviewResult { get; init; }
    public AiActivity? Activity { get; init; }
    public PersonClipLiveFrame? LiveFrame { get; init; }
    public bool OpenSegment { get; init; }
    public IReadOnlyList<PersonDetectionEvidence> Evidence { get; init; } = [];
}
public sealed record PersonClipResult(string Path, MediaInfo Info, IReadOnlyList<ConversionOptions> Segments, int SampledFrames, int UncertainFrames,
    int InferredFrames, int BoundaryFrames, string Backend)
{
    public bool IsPartial { get; init; }
    public double AnalyzedSeconds { get; init; }
    public bool FromCache { get; init; }
    public int DarkFrames { get; init; }
    public int BlankFrames { get; init; }
    public double ExcludedSeconds { get; init; }
    public int ReusedFrames => SampledFrames - InferredFrames - DarkFrames - BlankFrames;
    public IReadOnlyList<PersonDetectorStatistics> Detectors { get; init; } = [];
    public PersonDetectionMode DetectionMode { get; init; }
}
internal sealed record PersonFrame(double Seconds, bool Keep, bool Uncertain, IReadOnlyList<PersonDetectionEvidence> Evidence)
{
    public PersonFrameSkip Skip { get; init; }
}

public sealed class PersonClipAnalysis(IMediaEngine engine, ModelStore? modelStore = null)
{
    private readonly ModelStore _store = modelStore ?? new();

    public Task<PersonClipResult> AnalyzeAsync(string path, PersonClipOptions options,
        IProgress<PersonClipProgress>? progress = null, CancellationToken ct = default) => AnalyzeStagesAsync(path, options, progress, ct);

    internal Task<PersonClipResult> AnalyzeStagesAsync(string path, PersonClipOptions options,
        IProgress<PersonClipProgress>? progress, CancellationToken ct, PersonClipTaskState? stages = null, Job? job = null,
        PersonClipRetryPlan? retry = null) => Task.Run(async () =>
    {
        options.Validate();
        var cache = new PersonClipCache(path, options);
        double observedSeconds = 0, duration = 0;
        IReadOnlyList<PersonDetectionEvidence> evidence = [];
        var modelName = string.Join(" + ", options.RequiredModels.Select(id => PersonDetectorCatalog.Find(id).Name));
        if (options.UseEmbedding) modelName += " + Gemma";
        var activity = new AiActivityReporter(value => progress?.Report(new(observedSeconds, duration, value.Stage)
            { Activity = value, Evidence = evidence }), modelName + " · 人物检测", "个片段",
            ["读取视频", "准备检测模型", "人物检测", "细化片段边界"]);
        activity.Node("读取视频");
        activity.Stage("读取视频", detail: Path.GetFileName(path));
        var reused = retry?.Summary;
        if (reused is null) stages?.Start(PersonClipStageIds.ReadVideo);
        else stages?.Reuse(PersonClipStageIds.ReadVideo, reused);
        var info = reused?.Info ?? await engine.Probe(path, ct);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0 || info.Width < 1 || info.Height < 1)
            throw new ArgumentException("请选择有有效时长的视频。");
        duration = info.Duration;
        if (job is not null)
        {
            var source = new FileInfo(path);
            stages?.Source(info, source.Length, source.LastWriteTimeUtc);
            job.Duration = info.Duration;
        }
        var excluded = PersonClipExclusions.Normalize(options.ExcludedRanges);
        var excludedSeconds = excluded.Sum(range => range.End - range.Start);
        if (excluded.Any(range => range.End > duration)) throw new ArgumentException("免检测区间超出视频时长。");
        options = options with { ExcludedRanges = excluded };
        if (reused is null) stages?.Complete(PersonClipStageIds.ReadVideo,
            $"{info.Width}×{info.Height} · {info.VideoCodec} · {MediaTime.Format(info.Duration)}");
        if (PersonClipExclusions.Subtract([new(0, duration)], excluded).Length == 0)
        {
            observedSeconds = duration;
            stages?.Complete(PersonClipStageIds.PrepareModel, "已跳过 · 全部视频位于免检测区间", StageState.Skipped);
            stages?.Complete(PersonClipStageIds.Detect, "已跳过 · 全部视频位于免检测区间", StageState.Skipped);
            stages?.Complete(PersonClipStageIds.Refine, "0 段 · 全部视频位于免检测区间", StageState.Skipped);
            activity.Finish("没有可保留的片段");
            return new PersonClipResult(path, info, [], 0, 0, 0, 0, "") { AnalyzedSeconds = duration, ExcludedSeconds = duration, DetectionMode = options.DetectionMode };
        }
        if (retry?.StageId is null && string.IsNullOrEmpty(retry?.FallbackReason) && await cache.ReadAsync(info, options, ct).ConfigureAwait(false) is { } cached)
        {
            observedSeconds = duration;
            activity.Node("细化片段边界"); activity.Backend("检测缓存");
            stages?.Complete(PersonClipStageIds.PrepareModel, "已跳过 · 复用检测缓存，无需加载模型", StageState.Skipped);
            stages?.Complete(PersonClipStageIds.Detect, $"复用检测缓存 · {cached.SampledFrames} 帧 · {cached.Segments.Count} 段", StageState.Skipped);
            stages?.Complete(PersonClipStageIds.Refine, $"复用检测缓存 · {cached.Segments.Count} 段", StageState.Skipped);
            foreach (var segment in cached.Segments)
                activity.Result($"确认片段 · {MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}", cached.Segments.Count);
            activity.Finish("复用检测结果");
            return cached;
        }
        activity.Node("准备检测模型");
        if (reused is null) stages?.Start(PersonClipStageIds.PrepareModel);
        else stages?.Reuse(PersonClipStageIds.PrepareModel, reused);
        if (reused is not null)
        {
            if (retry?.Detection is not null) stages?.Reuse(PersonClipStageIds.Detect, reused);
            stages?.Start(retry?.Detection is not null ? PersonClipStageIds.Refine : PersonClipStageIds.Detect);
        }
        activity.Stage("校验模型");
        if (job is not null)
            foreach (var id in PersonClipTaskState.RequiredModels(job))
            {
                await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                if (await _store.IsInstalledAsync(id, ct: ct).ConfigureAwait(false)) continue;
                var label = "下载模型 · " + ModelCatalog.Find(id).Name;
                stages?.Log(PersonClipStageIds.PrepareModel, label);
                await _store.DownloadAsync(id, new ModelProgress(value =>
                {
                    activity.Stage(label, value.Received, value.Total, "字节");
                    stages?.Update(value.Received, value.Total, "字节", label);
                }), ct).ConfigureAwait(false);
            }
        using var permit = stages is null ? null : await PersonClipService.EnterDetectionAsync(ct).ConfigureAwait(false);
        using var detectors = await PersonDetectorSet.CreateAsync(_store, options, ct);
        var size = detectors.FrameSize;
        activity.Stage("加载人物检测模型");
        activity.Backend(detectors.Backend);
        var bounds = PersonFrameBounds.From(info, size);
        detectors.SetFrameGeometry(info);
        await using var embedding = options.UseEmbedding ? await GemmaMediaEmbedding.StartAsync(_store, ct, stage => activity.Stage(stage)) : null;
        stages?.Model(_store, detectors.Backend);
        if (reused is null) stages?.Complete(PersonClipStageIds.PrepareModel, detectors.Backend);
        if (reused is null) stages?.Start(PersonClipStageIds.Detect);
        activity.Node("人物检测");
        activity.Backend(detectors.Backend);
        var samples = retry?.Detection?.Samples.ToList() ?? new List<PersonFrame>();
        byte[]? reference = null;
        PersonFrame? previous = null;
        var lastInference = double.NegativeInfinity;
        var inferredFrames = retry?.Detection?.Result.InferredFrames ?? 0; var boundaryFrames = 0;
        var darkFrames = retry?.Detection?.Result.DarkFrames ?? 0; var blankFrames = retry?.Detection?.Result.BlankFrames ?? 0;
        var previewClock = System.Diagnostics.Stopwatch.StartNew();
        var nextPreview = TimeSpan.Zero;
        var candidates = retry?.Detection?.Candidates.Select(c => (c.Start, c.End)).ToList() ?? new List<(int Start, int End)>();
        var candidateStart = -1;
        var candidateEnd = -1;
        var sampledFrames = samples.Count(sample => sample.Skip != PersonFrameSkip.Excluded);
        var planned = PersonClipTaskState.PlannedFrames(info, options);
        var connectionGap = Math.Max(options.MergeGapSeconds, 2 * options.PaddingSeconds);
        void CompleteCandidate()
        {
            var end = candidateEnd >= 0 ? candidateEnd : samples.Count;
            candidates.Add((candidateStart, end));
            activity.Result($"候选片段 {candidates.Count} · {MediaTime.Format(samples[candidateStart].Seconds)} – {MediaTime.Format(end < samples.Count ? samples[end].Seconds : duration)}", candidates.Count);
            candidateStart = candidateEnd = -1;
            PublishPartial();
        }
        void PublishPartial()
        {
            if (samples.Count == 0) return;
            var ranges = candidates.Select(candidate => new PersonClipRange(samples[candidate.Start].Seconds,
                candidate.End < samples.Count ? samples[candidate.End].Seconds : observedSeconds)).ToList();
            if (candidateStart >= 0) ranges.Add(new(samples[candidateStart].Seconds,
                candidateEnd >= 0 ? samples[candidateEnd].Seconds : observedSeconds));
            var merged = new List<PersonClipRange>();
            foreach (var range in ranges)
            {
                var padded = new PersonClipRange(Math.Max(0, range.Start - options.PaddingSeconds),
                    Math.Min(observedSeconds, range.End + options.PaddingSeconds));
                if (merged.Count > 0 && padded.Start <= merged[^1].End)
                    merged[^1] = new(merged[^1].Start, Math.Max(merged[^1].End, padded.End));
                else merged.Add(padded);
            }
            var retained = PersonClipExclusions.Subtract(merged, excluded)
                .Where(range => range.End - range.Start >= options.MinimumSeconds)
                .Select(range => new ConversionOptions { Start = range.Start, End = range.End }).ToArray();
            var partial = new PersonClipResult(path, info, retained,
                samples.Count(frame => frame.Skip != PersonFrameSkip.Excluded), samples.Count(frame => frame.Uncertain),
                inferredFrames, boundaryFrames, detectors.Backend)
            { IsPartial = observedSeconds < duration, AnalyzedSeconds = observedSeconds, DetectionMode = options.DetectionMode,
                DarkFrames = darkFrames, BlankFrames = blankFrames, ExcludedSeconds = excludedSeconds, Detectors = detectors.Statistics.ToArray() };
            progress?.Report(new(observedSeconds, duration, "人物检测") { PreviewResult = partial,
                OpenSegment = candidateStart >= 0 && candidateEnd < 0 && retained.Length > 0 && retained[^1].End >= samples[candidateStart].Seconds });
        }
        try
        {
            if (retry?.Detection is null)
            {
                activity.Stage("扫描视频", 0, duration, "秒");
                await foreach (var decoded in PersonClipFrameSource.ReadAsync(engine, path, info, options, size, ct))
                {
                    await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                    var seconds = decoded.Seconds; var frame = decoded.Rgb; var inferred = false;
                    var skip = frame is null ? PersonFrameSkip.Excluded : PersonFrameVisibility.Exclude(frame, size, bounds, options);
                    if (skip != PersonFrameSkip.None)
                    {
                        previous = new(seconds, false, false, []) { Skip = skip };
                        samples.Add(previous); reference = null;
                        if (skip == PersonFrameSkip.Dark) darkFrames++;
                        else if (skip == PersonFrameSkip.Blank) blankFrames++;
                    }
                    else
                    {
                        var signature = options.ReuseSimilarFrames ? VideoFrameSimilarity.FromRgb(frame!, size, size) : null;
                        // Reuse only a confident positive, and recheck at least once a second.
                        if (signature is not null && reference is not null && previous is { Keep: true, Uncertain: false }
                            && seconds - lastInference < 1 && VideoFrameSimilarity.Similar(signature, reference))
                            samples.Add(previous with { Seconds = seconds });
                        else
                        {
                            previous = await ClassifyAsync(detectors, frame!, seconds, options, bounds, embedding, ct, checkVisibility: false);
                            samples.Add(previous); inferredFrames++; inferred = true;
                            reference = signature; lastInference = seconds;
                        }
                    }
                    if (skip != PersonFrameSkip.Excluded) sampledFrames++;
                    observedSeconds = Math.Min(duration, seconds + 1 / options.FramesPerSecond);
                    var decision = samples[^1];
                    evidence = decision.Evidence;
                    // Keep short detection gaps inside one source range; only its outer edges need refinement.
                    if (candidateEnd >= 0 && seconds - samples[candidateEnd].Seconds > connectionGap) CompleteCandidate();
                    if (decision.Keep)
                    {
                        if (candidateStart < 0) candidateStart = samples.Count - 1;
                        candidateEnd = -1;
                    }
                    else if (candidateStart >= 0 && candidateEnd < 0) candidateEnd = samples.Count - 1;
                    if (frame is not null && inferred && previewClock.Elapsed >= nextPreview)
                    {
                        PublishPartial();
                        nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                        var label = decision.Skip switch { PersonFrameSkip.Dark => "黑灯", PersonFrameSkip.Blank => "无画面",
                            _ => decision.Uncertain ? "待确认" : decision.Keep ? "保留" : "跳过" };
                        var png = EncodePng(frame, size);
                        progress?.Report(new(seconds, duration, "人物检测") { LiveFrame = new(seconds,
                            sampledFrames, png)
                            { Boxes = decision.Evidence.Where(item => item.Id != ModelCatalog.MediaPipePalmId)
                                .OrderByDescending(item => item.Boxes.Count).FirstOrDefault()?.Boxes ?? [],
                              PersonCount = decision.Evidence.Where(item => item.Id != ModelCatalog.MediaPipePalmId).Select(item => item.Boxes.Count).DefaultIfEmpty(0).Max() } });
                        activity.Frame(png, $"{Path.GetFileName(path)} · {MediaTime.Format(seconds)} · {label}");
                    }
                    if (previewClock.Elapsed >= nextPreview)
                    {
                        PublishPartial(); nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                    }
                    var sampled = sampledFrames;
                    stages?.Update(sampled, planned, "帧", (planned is null ? $"{sampled} 帧" : $"{sampled} / {planned} 帧") + $" · 候选 {candidates.Count + (candidateStart >= 0 ? 1 : 0)} 段");
                    stages?.Backend(detectors.Backend, string.Join(" · ", detectors.Statistics.Where(d => d.FallbackReason is not null).Select(d => d.Name + ": " + d.FallbackReason)));
                    activity.Backend(detectors.Backend);
                    activity.Advance(observedSeconds, duration, "秒", $"模型计算 {inferredFrames} 帧 · 黑灯排除 {darkFrames} 帧 · 无画面排除 {blankFrames} 帧 · 免检测 {MediaTime.Format(excludedSeconds)} · 候选 {candidates.Count + (candidateStart >= 0 ? 1 : 0)} 段");
                }
                if (samples.Count == 0) throw new InvalidDataException("视频未解码出可分析的画面。");
                if (candidateStart >= 0) CompleteCandidate();
                PublishPartial();
                if (job is not null)
                {
                    var rough = new PersonClipResult(path, info, [], samples.Count(f => f.Skip != PersonFrameSkip.Excluded),
                        samples.Count(f => f.Uncertain), inferredFrames, 0, detectors.Backend)
                    { DarkFrames = darkFrames, BlankFrames = blankFrames, ExcludedSeconds = excludedSeconds, AnalyzedSeconds = duration,
                        DetectionMode = options.DetectionMode, Detectors = detectors.Statistics };
                    await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "people-detection"),
                        new PersonClipDetectionCheckpoint(rough, samples.Select(sample => sample with { Evidence = [] }).ToArray(), candidates.Select(c => new PersonClipCandidate(c.Start, c.End)).ToArray()), ct).ConfigureAwait(false);
                }
                stages?.Complete(PersonClipStageIds.Detect, $"{samples.Count(f => f.Skip != PersonFrameSkip.Excluded)} 帧 · {candidates.Count} 段");
            }
            else { observedSeconds = duration; PublishPartial(); }
            stages?.Open(false);
            stages?.Start(PersonClipStageIds.Refine);
            var boundaries = candidates.Sum(candidate => (candidate.Start > 0 ? 1 : 0) + (candidate.End < samples.Count ? 1 : 0));
            var refined = 0;
            activity.Node("细化片段边界");
            activity.Backend(detectors.Backend);
            stages?.Update(0, boundaries, "处", $"0 / {boundaries} 处边界");
            activity.Stage("细化片段边界", 0, boundaries, "处");
            var intervals = new List<(double Start, double End)>();
            foreach (var candidate in candidates)
            {
                await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                var start = candidate.Start == 0 ? 0 : await RefineBoundaryAsync(candidate.Start);
                var end = candidate.End == samples.Count ? info.Duration : await RefineBoundaryAsync(candidate.End);
                intervals.Add((start, end));
            }
            async Task<double> RefineBoundaryAsync(int index)
            {
                // User masks are exact fences, never boundary-refined with a model.
                if (samples[index].Skip == PersonFrameSkip.Excluded || samples[index - 1].Skip == PersonFrameSkip.Excluded)
                {
                    activity.Advance(++refined, boundaries, "处");
                    stages?.Update(refined, boundaries, "处", $"{refined} / {boundaries} 处边界");
                    return samples[index].Seconds;
                }
                double low = samples[index - 1].Seconds, high = samples[index].Seconds;
                var decoded = high - low > .1 ? await PersonBoundaryFrames.ReadAsync(engine, path, info.VideoStreamIndex, low, high, size, ct) : null;
                while (high - low > .1)
                {
                    await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                    var middle = (low + high) / 2;
                    activity.Advance(refined, boundaries, "处", $"正在定位 {MediaTime.Format(low)} – {MediaTime.Format(high)}");
                    var frame = decoded!.At(middle);
                    var result = await ClassifyAsync(detectors, frame, middle, options, bounds, embedding, ct);
                    evidence = result.Evidence;
                    stages?.Backend(detectors.Backend, string.Join(" · ", detectors.Statistics.Where(d => d.FallbackReason is not null).Select(d => d.Name + ": " + d.FallbackReason)));
                    if (result.Skip == PersonFrameSkip.None && previewClock.Elapsed >= nextPreview)
                    {
                        nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                        var png = EncodePng(frame, size);
                        var boxes = result.Evidence.Where(item => item.Id != ModelCatalog.MediaPipePalmId)
                            .OrderByDescending(item => item.Boxes.Count).FirstOrDefault()?.Boxes ?? [];
                        progress?.Report(new(middle, duration, "细化片段边界") { LiveFrame = new(middle,
                            sampledFrames + boundaryFrames + 1, png) { Boxes = boxes, PersonCount = boxes.Count } });
                        activity.Frame(png, $"边界画面 · {MediaTime.Format(middle)} · {(result.Keep ? "保留" : "跳过")}");
                    }
                    if (result.Skip == PersonFrameSkip.None) boundaryFrames++;
                    if (result.Keep == samples[index - 1].Keep) low = middle; else high = middle;
                }
                stages?.Update(refined + 1, boundaries, "处", $"{refined + 1} / {boundaries} 处边界");
                activity.Advance(++refined, boundaries, "处", $"边界计算 {boundaryFrames} 帧");
                // Preserve the boundary uncertainty on the person side.
                return samples[index].Keep ? low : high;
            }
            var merged = new List<(double Start, double End)>();
            foreach (var interval in intervals)
            {
                // Compare actual absence before adding padding, so margins cannot bridge long empty gaps.
                if (merged.Count > 0 && interval.Start - merged[^1].End <= options.MergeGapSeconds)
                    merged[^1] = (merged[^1].Start, interval.End);
                else merged.Add(interval);
            }
            var padded = new List<(double Start, double End)>();
            foreach (var interval in merged)
            {
                var item = (Start: Math.Max(0, interval.Start - options.PaddingSeconds), End: Math.Min(info.Duration, interval.End + options.PaddingSeconds));
                if (padded.Count > 0 && item.Start <= padded[^1].End)
                    padded[^1] = (padded[^1].Start, Math.Max(padded[^1].End, item.End));
                else padded.Add(item);
            }
            var retained = PersonClipExclusions.Subtract(padded.Select(interval => new PersonClipRange(interval.Start, interval.End)), excluded);
            var segments = retained.Where(interval => interval.End - interval.Start >= options.MinimumSeconds)
                .Select(interval => new ConversionOptions { Start = interval.Start, End = interval.End }).ToArray();
            // Retained ranges belong to the refinement stage.
            foreach (var segment in segments) activity.Result($"确认片段 · {MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}", segments.Length);
            activity.Result($"分析完成 · {segments.Length} 个片段 · 保留 {MediaTime.Format(segments.Sum(segment => segment.End - segment.Start))}", segments.Length);
            activity.Stage("分析完成", detail: $"保留 {MediaTime.Format(segments.Sum(segment => segment.End - segment.Start))}");
            activity.Finish("分析完成");
            var result = new PersonClipResult(path, info, segments, samples.Count(frame => frame.Skip != PersonFrameSkip.Excluded), samples.Count(frame => frame.Uncertain),
                inferredFrames, boundaryFrames, detectors.Backend)
            { Detectors = detectors.Statistics, DetectionMode = options.DetectionMode, DarkFrames = darkFrames, BlankFrames = blankFrames,
                ExcludedSeconds = excludedSeconds, AnalyzedSeconds = duration };
            await cache.WriteAsync(result, ct).ConfigureAwait(false);
            return result;
        }
        catch { PublishPartial(); stages?.Open(false); throw; }
    }, ct);

    private sealed class ModelProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => report(value); }

    private static async Task<PersonFrame> ClassifyAsync(PersonDetectorSet detectors, byte[] rgb, double seconds,
        PersonClipOptions options, PersonFrameBounds bounds, GemmaMediaEmbedding? embedding, CancellationToken ct, bool checkVisibility = true)
    {
        var skip = checkVisibility ? PersonFrameVisibility.Exclude(rgb, detectors.FrameSize, bounds, options) : PersonFrameSkip.None;
        if (skip != PersonFrameSkip.None) return new(seconds, false, false, []) { Skip = skip };
        var evidence = detectors.Detect(rgb, options.Threshold, ct);
        var decision = PersonDetectionPolicy.Decide(evidence, options.DetectionMode, options.KeepUncertain);
        // Hand close-ups are visible human presence, but a palm is not a second body-detector vote.
        // Run the small supplement only when the selected body detectors did not retain the frame.
        if (!decision.Keep && options.IncludeHands)
        {
            evidence = [.. evidence, detectors.DetectHands(rgb, ct)];
            decision = PersonDetectionPolicy.Decide(evidence, options.DetectionMode, options.KeepUncertain);
        }
        var keep = decision.Keep; var uncertain = decision.Uncertain;
        // Semantics only resolves weak evidence; it cannot bypass an explicit cross-confirmation rule.
        if (embedding is not null && uncertain && options.DetectionMode != PersonDetectionMode.Consensus)
        {
            var margin = await embedding.PersonMarginAsync(EncodePng(rgb, detectors.FrameSize), ct);
            keep |= margin >= .03 || options.KeepUncertain && Math.Abs(margin) < .03;
        }
        return new(seconds, keep, uncertain, evidence);
    }
    private static byte[] EncodePng(byte[] rgb, int size)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rgba = new byte[size * size * 4];
        for (var pixel = 0; pixel < size * size; pixel++)
        { rgba[pixel * 4] = rgb[pixel * 3]; rgba[pixel * 4 + 1] = rgb[pixel * 3 + 1]; rgba[pixel * 4 + 2] = rgb[pixel * 3 + 2]; rgba[pixel * 4 + 3] = 255; }
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        return data.ToArray();
    }
}
