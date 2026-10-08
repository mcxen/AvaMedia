using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace AvaMedia.Core;

public sealed record MediaTagOptions(int VideoFrames = 8, bool PreferGpu = false, bool ReuseSimilarFrames = true, int BatchSize = 4)
{
    public void Validate()
    {
        if (VideoFrames is < 1 or > 32 || BatchSize is < 1 or > 8) throw new ArgumentException("采样帧数须为 1–32，批次大小须为 1–8。");
    }
}
public sealed record MediaTagScore(string Tag, double Score, double Maximum);
public sealed record MediaTagResult(string Path, IReadOnlyList<MediaTagScore> Scores, int SampledFrames, int InferredFrames,
    string Backend, long Length, DateTime LastWriteUtc, string? FallbackReason = null);
public sealed record MediaTagProgress(string Path, MediaTagResult? Result, string? Error, int Completed, int Total);
public sealed record MediaTagQuery(string Label, string[] Tags);

/// <summary>Local multi-label inference, batched images and bounded video samples. Never writes source media.</summary>
public sealed class MediaTagService(IMediaEngine engine, ModelStore? modelStore = null)
{
    private const int Size = 448;
    private readonly ModelStore _store = modelStore ?? new();
    public static bool Supports(string path) => new MediaFileRouter().Classify(path) is MediaFileKind.Image or MediaFileKind.Video;
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["黑发"] = ["black_hair"], ["长发"] = ["long_hair"], ["黑长发"] = ["black_hair", "long_hair"],
        ["短发"] = ["short_hair"], ["棕发"] = ["brown_hair"], ["金发"] = ["blonde_hair"],
        ["大胸"] = ["large_breasts"], ["巨乳"] = ["huge_breasts"], ["阴毛"] = ["pubic_hair"],
        ["阴毛可见"] = ["pubic_hair"], ["毛逼"] = ["pubic_hair"], ["裸露"] = ["nude"], ["眼镜"] = ["glasses"]
    };

    // JoyTag has a fixed vocabulary. Compound expressions require every tag; separate lines are alternatives.
    public static MediaTagQuery[] ParseQueries(string text, IEnumerable<string> vocabulary)
    {
        var known = vocabulary.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queries = new List<MediaTagQuery>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = entry.IndexOf('=');
            var label = (split < 0 ? entry : entry[..split]).Trim();
            var expression = split < 0 ? entry : entry[(split + 1)..];
            BatchVideoTools.ValidateRenameKeyword(label);
            var terms = expression.Split(['+', '＋'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) throw new ArgumentException("请输入标签：" + label);
            var tags = terms.SelectMany(term => Aliases.TryGetValue(term, out var alias) ? alias : [term.Replace(' ', '_').ToLowerInvariant()]).Distinct().ToArray();
            var unknown = tags.FirstOrDefault(tag => !known.Contains(tag));
            if (unknown is not null) throw new ArgumentException("模型不支持此标签：" + unknown);
            if (!labels.Add(label)) throw new ArgumentException("关键词重复：" + label);
            queries.Add(new(label, tags));
        }
        if (queries.Count > WordLibraryCatalog.MaximumCandidates) throw new ArgumentException("最多选择 20000 个关键词。");
        return queries.ToArray();
    }

    public static string MatchLabel(MediaTagResult result, IReadOnlyList<MediaTagQuery> queries, double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentException("标签阈值须为 0–1。");
        if (queries.Count == 0) return JoinLabels(result.Scores.Where(score => score.Score >= threshold)
            .OrderByDescending(score => score.Score).Select(score => score.Tag));
        var scores = result.Scores.ToDictionary(score => score.Tag, score => score.Score);
        return JoinLabels(queries.Where(query => query.Tags.Length > 0 && query.Tags.All(tag => scores.GetValueOrDefault(tag) >= threshold))
            .OrderByDescending(query => query.Tags.Min(tag => scores.GetValueOrDefault(tag))).Select(query => query.Label));
    }

    private static string JoinLabels(IEnumerable<string> labels)
    {
        var selected = new List<string>();
        foreach (var label in labels)
        {
            try { BatchVideoTools.ValidateRenameKeyword(label); } catch (ArgumentException) { continue; }
            if (selected.Sum(item => item.Length) + selected.Count + label.Length > 100) continue;
            selected.Add(label); if (selected.Count == 3) break;
        }
        return string.Join('_', selected);
    }

    public Task<IReadOnlyList<MediaTagResult>> AnalyzeAsync(IEnumerable<string> paths, MediaTagOptions options,
        IProgress<MediaTagProgress>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        options.Validate();
        var files = paths.Select(Path.GetFullPath).Distinct(BatchVideoTools.PathComparer).ToArray();
        using var lease = await _store.AcquireAsync(ModelCatalog.JoyTagId, ct).ConfigureAwait(false);
        var tags = (await File.ReadAllLinesAsync(Path.Combine(lease.Directory, ModelCatalog.JoyTagLabels), ct).ConfigureAwait(false))
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
        if (tags.Length != 5813) throw new InvalidDataException("模型标签文件无效，请重新下载 JoyTag。");
        using var session = new ModelInferenceSession(Path.Combine(lease.Directory, ModelCatalog.JoyTagFile),
            ModelCatalog.Find(ModelCatalog.JoyTagId).Files[0].Sha256, options.PreferGpu, options.BatchSize);
        var completed = 0;
        var results = new List<MediaTagResult>();
        var pending = new List<(FileInfo File, long Length, DateTime Modified, byte[] Image)>();
        void Report(string path, MediaTagResult? result, string? error)
        {
            if (result is not null) results.Add(result);
            progress?.Report(new(path, result, error, ++completed, files.Length));
        }
        void FlushImages()
        {
            if (pending.Count == 0) return;
            float[][] vectors;
            try { vectors = Predict(session, pending.Select(item => item.Image).ToArray(), options.BatchSize, tags.Length, ct); }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
            { foreach (var item in pending) Report(item.File.FullName, null, error.Message); pending.Clear(); return; }
            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                try
                {
                    CheckSource(item.File, item.Length, item.Modified);
                    Report(item.File.FullName, new(item.File.FullName, tags.Select((tag, j) => new MediaTagScore(tag, vectors[i][j], vectors[i][j])).ToArray(),
                        1, 1, session.Backend, item.Length, item.Modified, session.FallbackReason), null);
                }
                catch (IOException error) { Report(item.File.FullName, null, error.Message); }
            }
            pending.Clear();
        }
        foreach (var path in files.Where(path => !VideoFormats.IsVideo(path)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Supports(path)) throw new ArgumentException("请选择图片或视频。");
                var file = new FileInfo(path); var length = file.Length; var modified = file.LastWriteTimeUtc;
                var png = await engine.Thumbnail(path, 0, 768, 768, ct, pad: false).ConfigureAwait(false);
                pending.Add((file, length, modified, png));
                if (pending.Count == options.BatchSize) FlushImages();
            }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
            { Report(path, null, error.Message); }
        }
        FlushImages();
        foreach (var path in files.Where(VideoFormats.IsVideo))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path); var length = file.Length; var modified = file.LastWriteTimeUtc;
                var info = await engine.Probe(path, ct).ConfigureAwait(false);
                if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("视频时长无效。");
                var (duration, frameRate) = BatchVideoTools.VideoTiming(info);
                var count = (int)Math.Min(options.VideoFrames, Math.Max(1, Math.Ceiling(duration * 2)));
                var unique = new List<(byte[] Image, byte[] Signature)>();
                var samples = new int[count];
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var seconds = Math.Min(duration * (i + .5) / count, Math.Max(0, duration - Math.Max(.1, 1 / frameRate)));
                    var png = await engine.Thumbnail(path, seconds, 768, 768, ct, pad: false, videoStreamIndex: info.VideoStreamIndex).ConfigureAwait(false);
                    var signature = options.ReuseSimilarFrames ? VideoFrameSimilarity.FromEncoded(png) : [];
                    var reused = options.ReuseSimilarFrames ? unique.FindIndex(item => VideoFrameSimilarity.Similar(item.Signature, signature)) : -1;
                    samples[i] = reused >= 0 ? reused : unique.Count;
                    if (reused < 0) unique.Add((png, signature));
                }
                var mean = new double[tags.Length]; var maximum = new double[tags.Length];
                for (var offset = 0; offset < unique.Count; offset += options.BatchSize)
                {
                    var vectors = Predict(session, unique.Skip(offset).Take(options.BatchSize).Select(item => item.Image).ToArray(), options.BatchSize, tags.Length, ct);
                    for (var i = 0; i < vectors.Length; i++)
                    {
                        var weight = samples.Count(sample => sample == offset + i);
                        for (var j = 0; j < tags.Length; j++) { mean[j] += vectors[i][j] * weight / count; maximum[j] = Math.Max(maximum[j], vectors[i][j]); }
                    }
                }
                CheckSource(file, length, modified);
                Report(path, new(path, tags.Select((tag, j) => new MediaTagScore(tag, mean[j], maximum[j])).ToArray(), count, unique.Count, session.Backend, length, modified, session.FallbackReason), null);
            }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested) { Report(path, null, error.Message); }
        }
        return (IReadOnlyList<MediaTagResult>)results;
    }, ct);

    public static void ValidateSource(MediaTagResult result) => CheckSource(new FileInfo(result.Path), result.Length, result.LastWriteUtc);
    private static void CheckSource(FileInfo file, long length, DateTime modified)
    {
        file.Refresh();
        if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified) throw new IOException("分析后源文件已改变，请重新分析。");
    }
    private static float[][] Predict(ModelInferenceSession session, byte[][] images, int batch, int tagCount, CancellationToken ct)
    {
        var input = new DenseTensor<float>(new[] { batch, 3, Size, Size });
        var normalized = input.Buffer.Span;
        const int plane = Size * Size;
        for (var i = 0; i < images.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var source = SKBitmap.Decode(images[i]) ?? throw new InvalidDataException("图片解码失败。");
            using var square = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(square))
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
            {
                canvas.Clear(SKColors.White);
                var scale = Size / (float)Math.Max(source.Width, source.Height);
                var w = source.Width * scale; var h = source.Height * scale;
                canvas.DrawBitmap(source, SKRect.Create((Size - w) / 2, (Size - h) / 2, w, h), paint);
            }
            var pixels = square.GetPixelSpan();
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var pixel = y * square.RowBytes + x * 4; var target = i * 3 * plane + y * Size + x;
                    normalized[target] = (pixels[pixel] / 255f - .48145466f) / .26862954f;
                    normalized[target + plane] = (pixels[pixel + 1] / 255f - .4578275f) / .26130258f;
                    normalized[target + 2 * plane] = (pixels[pixel + 2] / 255f - .40821073f) / .27577711f;
                }
        }
        for (var i = images.Length; i < batch; i++)
            normalized.Slice((images.Length - 1) * 3 * plane, 3 * plane).CopyTo(normalized.Slice(i * 3 * plane, 3 * plane));
        using var output = session.Run(NamedOnnxValue.CreateFromTensor(session.InputName, input), ct);
        var logits = output.First().AsTensor<float>();
        if (logits.Rank != 2 || logits.Dimensions[0] != batch || logits.Dimensions[1] != tagCount) throw new InvalidDataException("模型输出维度不匹配。");
        var values = logits.ToArray();
        return Enumerable.Range(0, images.Length).Select(i => Enumerable.Range(0, tagCount).Select(j =>
        {
            var value = values[i * tagCount + j];
            if (!float.IsFinite(value)) throw new InvalidDataException("模型输出无效。");
            return 1f / (1f + MathF.Exp(-Math.Clamp(value, -80, 80)));
        }).ToArray()).ToArray();
    }
}
