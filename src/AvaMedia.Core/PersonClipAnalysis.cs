using SkiaSharp;
using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public sealed record PersonClipOptions(double FramesPerSecond = 4, double Threshold = .35,
    double PaddingSeconds = .5, double MergeGapSeconds = 1, double MinimumSeconds = .5,
    bool KeepUncertain = false, bool UseEmbedding = false, bool PreferGpu = true, bool ReuseSimilarFrames = true,
    string[]? DetectorIds = null, PersonDetectionMode DetectionMode = PersonDetectionMode.Balanced)
{
    public IReadOnlyList<string> SelectedDetectors => DetectorIds ?? [ModelCatalog.NanoDetId, ModelCatalog.MediaPipePersonId];
    public void Validate()
    {
        if (!double.IsFinite(FramesPerSecond) || FramesPerSecond is < .25 or > 16 || !double.IsFinite(Threshold) || Threshold is < .1 or > .9
            || new[] { PaddingSeconds, MergeGapSeconds, MinimumSeconds }.Any(value => !double.IsFinite(value) || value < 0 || value > 30))
            throw new ArgumentException("人物检测参数超出范围。");
        if (!Enum.IsDefined(DetectionMode) || SelectedDetectors.Count == 0 || SelectedDetectors.Count > PersonDetectorCatalog.All.Count
            || SelectedDetectors.Distinct().Count() != SelectedDetectors.Count)
            throw new ArgumentException("请至少选择一种人物检测，且不要重复选择。");
        foreach (var id in SelectedDetectors) _ = PersonDetectorCatalog.Find(id);
    }
}
public sealed record PersonClipProgress(double Seconds, double Duration, string Stage)
{
    public AiActivity? Activity { get; init; }
    public IReadOnlyList<PersonDetectionEvidence> Evidence { get; init; } = [];
}
public sealed record PersonClipResult(string Path, MediaInfo Info, IReadOnlyList<ConversionOptions> Segments, int SampledFrames, int UncertainFrames,
    int InferredFrames, int BoundaryFrames, string Backend)
{
    public int ReusedFrames => SampledFrames - InferredFrames;
    public IReadOnlyList<PersonDetectorStatistics> Detectors { get; init; } = [];
    public PersonDetectionMode DetectionMode { get; init; }
}
internal sealed record PersonFrame(double Seconds, bool Keep, bool Uncertain, IReadOnlyList<PersonDetectionEvidence> Evidence);

public sealed class PersonClipAnalysis(IMediaEngine engine, ModelStore? modelStore = null)
{
    private readonly ModelStore _store = modelStore ?? new();

    public Task<PersonClipResult> AnalyzeAsync(string path, PersonClipOptions options,
        IProgress<PersonClipProgress>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        options.Validate();
        double observedSeconds = 0, duration = 0;
        IReadOnlyList<PersonDetectionEvidence> evidence = [];
        var modelName = string.Join(" + ", options.SelectedDetectors.Select(id => PersonDetectorCatalog.Find(id).Name));
        if (options.UseEmbedding) modelName += " + Gemma";
        var activity = new AiActivityReporter(value => progress?.Report(new(observedSeconds, duration, value.Stage)
            { Activity = value, Evidence = evidence }), modelName + " · 人物检测", "个片段",
            ["准备检测模型", "读取视频", "人物检测", "细化片段边界", "保留片段"]);
        activity.Stage("校验模型");
        using var detectors = await PersonDetectorSet.CreateAsync(_store, options, ct);
        var size = detectors.FrameSize;
        activity.Stage("加载人物检测模型");
        activity.Backend(detectors.Backend);
        activity.Node("读取视频");
        activity.Stage("读取视频", detail: Path.GetFileName(path));
        var info = await engine.Probe(path, ct);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("请选择有有效时长的视频。");
        duration = info.Duration;
        activity.Node("人物检测");
        activity.Backend(detectors.Backend);
        await using var embedding = options.UseEmbedding ? await GemmaMediaEmbedding.StartAsync(_store, ct, options.PreferGpu, stage => activity.Stage(stage)) : null;
        var samples = new List<PersonFrame>();
        byte[]? reference = null;
        PersonFrame? previous = null;
        var lastInference = double.NegativeInfinity;
        var inferredFrames = 0; var boundaryFrames = 0;
        var previewClock = System.Diagnostics.Stopwatch.StartNew();
        var nextPreview = TimeSpan.Zero;
        var candidates = new List<(int Start, int End)>();
        var candidateStart = -1;
        var candidateEnd = -1;
        var connectionGap = Math.Max(options.MergeGapSeconds, 2 * options.PaddingSeconds);
        void CompleteCandidate()
        {
            var end = candidateEnd >= 0 ? candidateEnd : samples.Count;
            candidates.Add((candidateStart, end));
            activity.Result($"候选片段 {candidates.Count} · {MediaTime.Format(samples[candidateStart].Seconds)} – {MediaTime.Format(end < samples.Count ? samples[end].Seconds : duration)}", candidates.Count);
            candidateStart = candidateEnd = -1;
        }
        activity.Stage("扫描视频", 0, duration, "秒");
        string[] args = ["-v", "error", "-nostdin", "-i", path, "-map", $"0:v:{info.VideoStreamIndex}",
            "-vf", $"fps={MediaEngine.Number(options.FramesPerSecond)}:start_time=0:eof_action=pass,{FrameFilter(size)}",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"];
        using (var process = await ProcessRunner.StartAsync(engine.FFmpeg, args, ct))
        {
            using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync();
            var frame = new byte[size * size * 3];
            try
            {
                while (await ReadFrameAsync(process.StandardOutput.BaseStream, frame, ct))
                {
                    var seconds = samples.Count / options.FramesPerSecond;
                    if (seconds >= info.Duration) break;
                    var signature = options.ReuseSimilarFrames ? VideoFrameSimilarity.FromRgb(frame, size, size) : null;
                    // Compare with the last inferred frame so gradual changes cannot drift indefinitely.
                    // Recheck at least once per second and never reuse an uncertain decision.
                    if (signature is not null && reference is not null && previous is { Keep: true, Uncertain: false }
                        && seconds - lastInference < 1 && VideoFrameSimilarity.Similar(signature, reference))
                        samples.Add(previous with { Seconds = seconds });
                    else
                    {
                        previous = await ClassifyAsync(detectors, frame, seconds, options, embedding, ct);
                        samples.Add(previous); reference = signature; lastInference = seconds; inferredFrames++;
                    }
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
                    if (previewClock.Elapsed >= nextPreview)
                    {
                        nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                        var label = decision.Uncertain ? "待确认" : decision.Keep ? "保留" : "跳过";
                        activity.Frame(EncodePng(frame, size), $"{Path.GetFileName(path)} · {MediaTime.Format(seconds)} · {label}");
                    }
                    activity.Backend(detectors.Backend);
                    activity.Advance(observedSeconds, duration, "秒", $"模型计算 {inferredFrames} 帧 · 复用 {samples.Count - inferredFrames} 帧 · 候选 {candidates.Count + (candidateStart >= 0 ? 1 : 0)} 段");
                }
                // Drain the pipe before waiting, including any terminal frame produced by fps rounding.
                await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) throw new InvalidDataException("视频分析解码失败：" + await error);
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
                await process.WaitForExitAsync();
                await error;
            }
        }
        if (samples.Count == 0) throw new InvalidDataException("视频未解码出可分析的画面。");
        if (candidateStart >= 0) CompleteCandidate();
        var boundaries = candidates.Sum(candidate => (candidate.Start > 0 ? 1 : 0) + (candidate.End < samples.Count ? 1 : 0));
        var refined = 0;
        activity.Node("细化片段边界");
        activity.Backend(detectors.Backend);
        activity.Stage("细化片段边界", 0, boundaries, "处");
        var intervals = new List<(double Start, double End)>();
        foreach (var candidate in candidates)
        {
            var start = candidate.Start == 0 ? 0 : await RefineBoundaryAsync(candidate.Start);
            var end = candidate.End == samples.Count ? info.Duration : await RefineBoundaryAsync(candidate.End);
            intervals.Add((start, end));
        }
        async Task<double> RefineBoundaryAsync(int index)
        {
            double low = samples[index - 1].Seconds, high = samples[index].Seconds;
            while (high - low > .1)
            {
                ct.ThrowIfCancellationRequested();
                var middle = (low + high) / 2;
                activity.Advance(refined, boundaries, "处", $"正在定位 {MediaTime.Format(low)} – {MediaTime.Format(high)}");
                var frame = await ReadAtAsync(path, info.VideoStreamIndex, middle, size, ct);
                var result = await ClassifyAsync(detectors, frame, middle, options, embedding, ct);
                evidence = result.Evidence;
                if (previewClock.Elapsed >= nextPreview)
                {
                    nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                    activity.Frame(EncodePng(frame, size), $"边界画面 · {MediaTime.Format(middle)} · {(result.Keep ? "保留" : "跳过")}");
                }
                boundaryFrames++;
                if (result.Keep == samples[index - 1].Keep) low = middle; else high = middle;
            }
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
        var segments = padded.Where(interval => interval.End - interval.Start >= options.MinimumSeconds)
            .Select(interval => new ConversionOptions { Start = interval.Start, End = interval.End }).ToArray();
        activity.Node("保留片段");
        foreach (var segment in segments) activity.Result($"确认片段 · {MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}", segments.Length);
        activity.Result($"分析完成 · {segments.Length} 个片段 · 保留 {MediaTime.Format(segments.Sum(segment => segment.End - segment.Start))}", segments.Length);
        activity.Stage("分析完成", detail: $"保留 {MediaTime.Format(segments.Sum(segment => segment.End - segment.Start))}");
        activity.Finish("分析完成");
        return new PersonClipResult(path, info, segments, samples.Count, samples.Count(frame => frame.Uncertain),
            inferredFrames, boundaryFrames, detectors.Backend) { Detectors = detectors.Statistics, DetectionMode = options.DetectionMode };
    }, ct);

    private static string FrameFilter(int size) => $"scale={size}:{size}:force_original_aspect_ratio=decrease,pad={size}:{size}:(ow-iw)/2:(oh-ih)/2:color=0x727272,setsar=1";
    private static async Task<bool> ReadFrameAsync(Stream input, byte[] frame, CancellationToken ct)
    {
        var offset = 0;
        while (offset < frame.Length)
        {
            var read = await input.ReadAsync(frame.AsMemory(offset), ct);
            if (read == 0) { if (offset == 0) return false; throw new InvalidDataException("视频帧数据不完整。"); }
            offset += read;
        }
        return true;
    }
    private async Task<byte[]> ReadAtAsync(string path, int stream, double seconds, int size, CancellationToken ct)
    {
        using var process = await ProcessRunner.StartAsync(engine.FFmpeg,
            ["-v", "error", "-nostdin", "-ss", MediaEngine.Number(seconds), "-i", path, "-map", $"0:v:{stream}", "-frames:v", "1",
                "-vf", FrameFilter(size), "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], ct);
        using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync();
        var frame = new byte[size * size * 3];
        try
        {
            if (!await ReadFrameAsync(process.StandardOutput.BaseStream, frame, ct)) throw new InvalidDataException("无法读取片段边界画面。");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new InvalidDataException("边界画面解码失败：" + await error);
            return frame;
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync();
            await error;
        }
    }
    private static async Task<PersonFrame> ClassifyAsync(PersonDetectorSet detectors, byte[] rgb, double seconds,
        PersonClipOptions options, GemmaMediaEmbedding? embedding, CancellationToken ct)
    {
        var evidence = detectors.Detect(rgb, options.Threshold, ct);
        var decision = PersonDetectionPolicy.Decide(evidence, options.DetectionMode, options.KeepUncertain);
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
