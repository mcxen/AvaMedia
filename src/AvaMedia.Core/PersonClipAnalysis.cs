using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public sealed record PersonClipOptions(double FramesPerSecond = 2, double Threshold = .35,
    double PaddingSeconds = .5, double MergeGapSeconds = 1, double MinimumSeconds = .5,
    bool KeepUncertain = true, bool UseEmbedding = false, bool PreferGpu = true, bool ReuseSimilarFrames = true)
{
    public void Validate()
    {
        if (!double.IsFinite(FramesPerSecond) || FramesPerSecond is < .25 or > 8 || !double.IsFinite(Threshold) || Threshold is < .1 or > .9
            || new[] { PaddingSeconds, MergeGapSeconds, MinimumSeconds }.Any(value => !double.IsFinite(value) || value < 0 || value > 30))
            throw new ArgumentException("人物检测参数超出范围。");
    }
}
public sealed record PersonClipProgress(double Seconds, double Duration, string Stage)
{
    public AiActivity? Activity { get; init; }
}
public sealed record PersonClipResult(string Path, MediaInfo Info, IReadOnlyList<ConversionOptions> Segments, int SampledFrames, int UncertainFrames,
    int InferredFrames, int BoundaryFrames, string Backend)
{
    public int ReusedFrames => SampledFrames - InferredFrames;
}
internal sealed record PersonFrame(double Seconds, bool Keep, bool Uncertain);

public sealed class PersonClipAnalysis(IMediaEngine engine, ModelStore? modelStore = null)
{
    private readonly ModelStore _store = modelStore ?? new();
    private const int Size = 640;

    public Task<PersonClipResult> AnalyzeAsync(string path, PersonClipOptions options,
        IProgress<PersonClipProgress>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        options.Validate();
        double observedSeconds = 0, duration = 0;
        var activity = new AiActivityReporter(value => progress?.Report(new(observedSeconds, duration, value.Stage) { Activity = value }), options.UseEmbedding ? "YOLOX + Gemma · 人物检测" : "YOLOX · 人物检测", "个片段");
        activity.Stage("校验模型");
        using var model = await _store.AcquireAsync(ModelCatalog.PersonId, ct);
        activity.Stage("加载人物检测模型");
        using var session = new ModelInferenceSession(Path.Combine(model.Directory, ModelCatalog.PersonFile),
            ModelCatalog.Find(ModelCatalog.PersonId).Files[0].Sha256, options.PreferGpu);
        activity.Backend(session.Backend);
        activity.Stage("读取视频", detail: Path.GetFileName(path));
        var info = await engine.Probe(path, ct);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("请选择有有效时长的视频。");
        duration = info.Duration;
        await using var embedding = options.UseEmbedding ? await GemmaVideoEmbedding.StartAsync(_store, ct, options.PreferGpu, stage => activity.Stage(stage)) : null;
        var samples = new List<PersonFrame>();
        byte[]? reference = null;
        PersonFrame? previous = null;
        var lastInference = double.NegativeInfinity;
        var inferredFrames = 0; var boundaryFrames = 0;
        var previewClock = System.Diagnostics.Stopwatch.StartNew();
        var nextPreview = TimeSpan.Zero;
        double candidateStart = -1;
        var candidates = 0;
        activity.Stage("扫描视频", 0, duration, "秒");
        string[] args = ["-v", "error", "-nostdin", "-i", path, "-map", $"0:v:{info.VideoStreamIndex}",
            "-vf", $"fps={MediaEngine.Number(options.FramesPerSecond)}:start_time=0:eof_action=pass,{FrameFilter}",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"];
        using (var process = await ProcessRunner.StartAsync(engine.FFmpeg, args, ct))
        {
            using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync();
            var frame = new byte[Size * Size * 3];
            try
            {
                while (await ReadFrameAsync(process.StandardOutput.BaseStream, frame, ct))
                {
                    var seconds = samples.Count / options.FramesPerSecond;
                    if (seconds >= info.Duration) break;
                    var signature = options.ReuseSimilarFrames ? VideoFrameSimilarity.FromRgb(frame, Size, Size) : null;
                    // Compare with the last inferred frame so gradual changes cannot drift indefinitely.
                    // Recheck at least once per second and never reuse an uncertain decision.
                    if (signature is not null && reference is not null && previous is { Uncertain: false }
                        && seconds - lastInference < 1 && VideoFrameSimilarity.Similar(signature, reference))
                        samples.Add(previous with { Seconds = seconds });
                    else
                    {
                        previous = await ClassifyAsync(session, frame, seconds, options, embedding, ct);
                        samples.Add(previous); reference = signature; lastInference = seconds; inferredFrames++;
                    }
                    observedSeconds = Math.Min(duration, seconds + 1 / options.FramesPerSecond);
                    var decision = samples[^1];
                    if (decision.Keep && candidateStart < 0) candidateStart = seconds;
                    if (!decision.Keep && candidateStart >= 0)
                    {
                        activity.Result($"候选片段 {++candidates} · {MediaTime.Format(candidateStart)} – {MediaTime.Format(seconds)}", candidates);
                        candidateStart = -1;
                    }
                    if (previewClock.Elapsed >= nextPreview)
                    {
                        nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                        var label = decision.Uncertain ? "待确认" : decision.Keep ? "保留" : "跳过";
                        activity.Frame(EncodePng(frame), $"{Path.GetFileName(path)} · {MediaTime.Format(seconds)} · {label}");
                    }
                    activity.Backend(session.Backend);
                    activity.Advance(observedSeconds, duration, "秒", $"模型计算 {inferredFrames} 帧 · 复用 {samples.Count - inferredFrames} 帧 · 候选 {candidates + (candidateStart >= 0 ? 1 : 0)} 段");
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
        if (candidateStart >= 0) activity.Result($"候选片段 {++candidates} · {MediaTime.Format(candidateStart)} – {MediaTime.Format(duration)}", candidates);
        var boundaries = samples.Skip(1).Zip(samples, (current, previousFrame) => current.Keep != previousFrame.Keep).Count(changed => changed);
        var refined = 0;
        activity.Stage("细化片段边界", 0, boundaries, "处");
        var intervals = new List<(double Start, double End)>();
        double start = samples[0].Keep ? 0 : -1;
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index].Keep == samples[index - 1].Keep) continue;
            double low = samples[index - 1].Seconds, high = samples[index].Seconds;
            while (high - low > .1)
            {
                ct.ThrowIfCancellationRequested();
                var middle = (low + high) / 2;
                activity.Advance(refined, boundaries, "处", $"正在定位 {MediaTime.Format(low)} – {MediaTime.Format(high)}");
                var frame = await ReadAtAsync(path, info.VideoStreamIndex, middle, ct);
                var result = await ClassifyAsync(session, frame, middle, options, embedding, ct);
                if (previewClock.Elapsed >= nextPreview)
                {
                    nextPreview = previewClock.Elapsed + TimeSpan.FromMilliseconds(500);
                    activity.Frame(EncodePng(frame), $"边界画面 · {MediaTime.Format(middle)} · {(result.Keep ? "保留" : "跳过")}");
                }
                boundaryFrames++;
                if (result.Keep == samples[index - 1].Keep) low = middle; else high = middle;
            }
            // Preserve the boundary uncertainty on the person side.
            if (samples[index].Keep) start = low;
            else if (start >= 0) { intervals.Add((start, high)); start = -1; }
            activity.Advance(++refined, boundaries, "处", $"边界计算 {boundaryFrames} 帧");
        }
        if (start >= 0) intervals.Add((start, info.Duration));
        var merged = new List<(double Start, double End)>();
        foreach (var interval in intervals)
        {
            var padded = (Start: Math.Max(0, interval.Start - options.PaddingSeconds), End: Math.Min(info.Duration, interval.End + options.PaddingSeconds));
            if (merged.Count > 0 && padded.Start - merged[^1].End <= options.MergeGapSeconds)
                merged[^1] = (merged[^1].Start, padded.End);
            else merged.Add(padded);
        }
        var segments = merged.Where(interval => interval.End - interval.Start >= options.MinimumSeconds)
            .Select(interval => new ConversionOptions { Start = interval.Start, End = interval.End }).ToArray();
        foreach (var segment in segments) activity.Result($"确认片段 · {MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}", segments.Length);
        activity.Result($"分析完成 · {segments.Length} 个片段 · 保留 {MediaTime.Format(segments.Sum(segment => segment.End - segment.Start))}", segments.Length);
        activity.Stage("分析完成", detail: $"保留 {MediaTime.Format(segments.Sum(segment => segment.End - segment.Start))}");
        activity.Finish("分析完成");
        return new PersonClipResult(path, info, segments, samples.Count, samples.Count(frame => frame.Uncertain),
            inferredFrames, boundaryFrames, session.Backend);
    }, ct);

    private static string FrameFilter => $"scale={Size}:{Size}:force_original_aspect_ratio=decrease,pad={Size}:{Size}:0:0:color=0x727272,setsar=1";
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
    private async Task<byte[]> ReadAtAsync(string path, int stream, double seconds, CancellationToken ct)
    {
        using var process = await ProcessRunner.StartAsync(engine.FFmpeg,
            ["-v", "error", "-nostdin", "-ss", MediaEngine.Number(seconds), "-i", path, "-map", $"0:v:{stream}", "-frames:v", "1",
                "-vf", FrameFilter, "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"], ct);
        using var cancellation = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync();
        var frame = new byte[Size * Size * 3];
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
    private static async Task<PersonFrame> ClassifyAsync(ModelInferenceSession session, byte[] rgb, double seconds,
        PersonClipOptions options, GemmaVideoEmbedding? embedding, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tensor = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var plane = Size * Size;
        for (var pixel = 0; pixel < plane; pixel++)
            for (var channel = 0; channel < 3; channel++) tensor.Buffer.Span[channel * plane + pixel] = rgb[pixel * 3 + channel];
        using var output = session.Run(NamedOnnxValue.CreateFromTensor(session.InputName, tensor), ct);
        ct.ThrowIfCancellationRequested();
        var detections = output.First().AsTensor<float>();
        if (detections.Rank != 3 || detections.Dimensions[2] != 85) throw new InvalidDataException("人体检测模型输出格式无效。");
        double score = 0;
        for (var index = 0; index < detections.Dimensions[1]; index++)
        {
            var person = detections[0, index, 5];
            var bestClass = true;
            for (var cls = 6; cls < 85; cls++) if (detections[0, index, cls] > person) { bestClass = false; break; }
            if (bestClass) score = Math.Max(score, person * detections[0, index, 4]);
        }
        if (!double.IsFinite(score)) throw new InvalidDataException("人体检测返回了无效分数。");
        var uncertain = score >= options.Threshold * .5 && score < options.Threshold;
        var keep = score >= options.Threshold || options.KeepUncertain && uncertain;
        if (embedding is not null && score < options.Threshold)
        {
            var margin = await embedding.PersonMarginAsync(EncodePng(rgb), ct);
            uncertain |= Math.Abs(margin) < .03;
            keep |= margin >= .03 || options.KeepUncertain && Math.Abs(margin) < .03;
        }
        return new(seconds, keep, uncertain);
    }
    private static byte[] EncodePng(byte[] rgb)
    {
        using var bitmap = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rgba = new byte[Size * Size * 4];
        for (var pixel = 0; pixel < Size * Size; pixel++)
        { rgba[pixel * 4] = rgb[pixel * 3]; rgba[pixel * 4 + 1] = rgb[pixel * 3 + 1]; rgba[pixel * 4 + 2] = rgb[pixel * 3 + 2]; rgba[pixel * 4 + 3] = 255; }
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        return data.ToArray();
    }
}
