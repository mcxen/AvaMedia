using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace AvaMedia.Core;

public sealed record MediaTagOptions(int VideoFrames = 8, bool PreferGpu = false, bool ReuseSimilarFrames = true, int BatchSize = 4, bool RecognizeScenes = false)
{
    public WordCandidate[] SemanticCandidates { get; init; } = [];
    public bool NeedsSemanticModel => RecognizeScenes || SemanticCandidates.Length > 0;
    public void Validate()
    {
        if (VideoFrames is < 1 or > 32 || BatchSize is < 1 or > 8) throw new ArgumentException("采样帧数须为 1–32，批次大小须为 1–8。");
        if (SemanticCandidates.Length > 0) WordLibraryCatalog.Validate(SemanticCandidates);
    }
}
public sealed record MediaTagScore(string Tag, double Score, double Maximum);
public sealed record MediaTagResult(string Path, IReadOnlyList<MediaTagScore> Scores, int SampledFrames, int InferredFrames,
    string Backend, long Length, DateTime LastWriteUtc, string? FallbackReason = null)
{
    public IReadOnlyList<MediaTagFrame> Frames { get; init; } = [];
    public MediaSceneResult? Scenes { get; init; }
    public string? SceneError { get; init; }
    public double DurationSeconds { get; init; }
}
public sealed record MediaTagFrame(double Seconds, IReadOnlyList<MediaTagScore> Scores)
{
    // Compact values follow MediaTagResult.Scores order, including scores below the display threshold.
    public float[] Values { get; init; } = [];
}
public sealed record MediaTagProgress(string Path, MediaTagResult? Result, string? Error, int Completed, int Total)
{
    public AiActivity? Activity { get; init; }
    public MediaTagResult? PreviewResult { get; init; }
}
public sealed record MediaTagQuery(string Label, string[] Tags);

/// <summary>Local multi-label inference, batched images and bounded video samples. Never writes source media.</summary>
public sealed class MediaTagService(IMediaEngine engine, ModelStore? modelStore = null)
{
    private const int Size = 448;
    private readonly ModelStore _store = modelStore ?? new();
    public static bool Supports(string path) => new MediaFileRouter().Classify(path) is MediaFileKind.Image or MediaFileKind.Video;
    private static readonly Dictionary<string, string[]> Aliases = CreateAliases();
    private static Dictionary<string, string[]> CreateAliases()
    {
        var aliases = WordLibraryCatalog.BuiltIns.Single(library => library.Id == "common").Entries.Where(entry => entry.Tags.Length > 0)
            .ToDictionary(entry => entry.Label, entry => entry.Tags, StringComparer.OrdinalIgnoreCase);
        aliases["阴毛可见"] = ["pubic_hair"]; aliases["毛逼"] = ["pubic_hair"];
        return aliases;
    }

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
            BatchRename.ValidateRenameKeyword(label);
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
        if (queries.Count == 0) return JoinLabels(result.Scores.Where(score => TagSignal(result, score) >= threshold)
            .OrderByDescending(score => TagSignal(result, score)).Select(score => score.Tag));
        var scores = result.Scores.ToDictionary(score => score.Tag, score => TagSignal(result, score));
        return JoinLabels(queries.Where(query => query.Tags.Length > 0 && query.Tags.All(tag => scores.GetValueOrDefault(tag) >= threshold))
            .OrderByDescending(query => query.Tags.Min(tag => scores.GetValueOrDefault(tag))).Select(query => query.Label));
    }

    public static double TagSignal(MediaTagResult result, MediaTagScore score) =>
        VideoFormats.IsVideo(result.Path) && WordLibraryCatalog.UsesSamplePeak(score.Tag) ? Math.Max(score.Score, score.Maximum) : score.Score;

    private static string JoinLabels(IEnumerable<string> labels)
    {
        var selected = new List<string>();
        foreach (var label in labels)
        {
            try { BatchRename.ValidateRenameKeyword(label); } catch (ArgumentException) { continue; }
            selected.Add(label);
        }
        return string.Join('_', selected);
    }

    public Task<IReadOnlyList<MediaTagResult>> AnalyzeAsync(IEnumerable<string> paths, MediaTagOptions options,
        IProgress<MediaTagProgress>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        options.Validate();
        var files = paths.Select(Path.GetFullPath).Distinct(BatchRename.PathComparer).ToArray();
        var completed = 0;
        var currentPath = files.FirstOrDefault() ?? "";
        var activity = new AiActivityReporter(value => progress?.Report(new(currentPath, null, null, completed, files.Length) { Activity = value }), "JoyTag", "次标签结果",
            options.NeedsSemanticModel ? ["准备标签模型", "准备场景模型", "识别媒体标签"] : ["准备标签模型", "识别媒体标签"]);
        activity.Stage("校验模型");
        using var lease = await _store.AcquireAsync(ModelCatalog.JoyTagId, ct).ConfigureAwait(false);
        var tags = (await File.ReadAllLinesAsync(Path.Combine(lease.Directory, ModelCatalog.JoyTagLabels), ct).ConfigureAwait(false))
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
        if (tags.Length != 5813) throw new InvalidDataException("模型标签文件无效，请重新下载 JoyTag。");
        activity.Stage("加载标签模型");
        using var session = new ModelInferenceSession(Path.Combine(lease.Directory, ModelCatalog.JoyTagFile),
            ModelCatalog.Find(ModelCatalog.JoyTagId).Files[0].Sha256, options.PreferGpu, options.BatchSize);
        string? sceneSetupError = null;
        async Task<MediaSceneClassifier?> PrepareScenesAsync()
        {
            if (!options.NeedsSemanticModel) return null;
            activity.Node("准备场景模型");
            try { return await MediaSceneClassifier.CreateAsync(_store, options, activity, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
            { sceneSetupError = error.Message; return null; }
        }
        await using var scenes = await PrepareScenesAsync().ConfigureAwait(false);
        async Task<(MediaSceneResult? Result, string? Error)> SceneResultAsync(byte[][] images, double[] seconds, int[] samples, Action<MediaSceneResult>? updated = null)
        {
            if (scenes is null) return (null, sceneSetupError);
            try { return (await scenes.AnalyzeAsync(images, seconds, samples, activity, ct, updated).ConfigureAwait(false), null); }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
            { return (null, error.Message); }
        }
        activity.Node("识别媒体标签");
        activity.Backend(session.Backend);
        var results = new List<MediaTagResult>();
        var pending = new List<(FileInfo File, long Length, DateTime Modified, byte[] Image)>();
        void Report(string path, MediaTagResult? result, string? error)
        {
            if (result is not null) results.Add(result);
            progress?.Report(new(path, result, error, ++completed, files.Length));
        }
        async Task FlushImagesAsync()
        {
            if (pending.Count == 0) return;
            activity.Stage("识别图片标签", detail: $"当前批次 {pending.Count} 张图片");
            activity.Backend(session.Backend);
            activity.Frame(pending[^1].Image, pending[^1].File.Name);
            float[][] vectors;
            try { vectors = Predict(session, pending.Select(item => item.Image).ToArray(), options.BatchSize, tags.Length, ct); }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
            { foreach (var item in pending) Report(item.File.FullName, null, error.Message); pending.Clear(); return; }
            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                currentPath = item.File.FullName;
                try
                {
                    CheckSource(item.File, item.Length, item.Modified);
                    activity.Backend(session.Backend);
                    activity.Frame(item.Image, item.File.Name);
                    activity.Result(item.File.Name + " · " + string.Join(" · ", tags.Select((tag, j) => new MediaTagScore(tag, vectors[i][j], vectors[i][j]))
                        .OrderByDescending(score => score.Score).Take(5).Select(score => $"{WordLibraryCatalog.TagLabel(score.Tag)} {score.Score:0.00}")));
                    var scene = await SceneResultAsync([item.Image], [0], [0]).ConfigureAwait(false);
                    CheckSource(item.File, item.Length, item.Modified);
                    Report(item.File.FullName, new(item.File.FullName, tags.Select((tag, j) => new MediaTagScore(tag, vectors[i][j], vectors[i][j])).ToArray(),
                        1, 1, session.Backend, item.Length, item.Modified, session.FallbackReason) { Scenes = scene.Result, SceneError = scene.Error, Frames = [new MediaTagFrame(0, []) { Values = vectors[i] }] }, null);
                }
                catch (IOException error) { Report(item.File.FullName, null, error.Message); }
            }
            pending.Clear();
        }
        foreach (var path in files.Where(path => !VideoFormats.IsVideo(path)))
        {
            ct.ThrowIfCancellationRequested();
            currentPath = path;
            activity.Stage("读取图片", detail: Path.GetFileName(path));
            try
            {
                if (!Supports(path)) throw new ArgumentException("请选择图片或视频。");
                var file = new FileInfo(path); var length = file.Length; var modified = file.LastWriteTimeUtc;
                var png = await engine.Thumbnail(path, 0, 768, 768, ct, pad: false).ConfigureAwait(false);
                activity.Frame(png, file.Name);
                pending.Add((file, length, modified, png));
                if (pending.Count == options.BatchSize) await FlushImagesAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
            { Report(path, null, error.Message); }
        }
        await FlushImagesAsync().ConfigureAwait(false);
        foreach (var path in files.Where(VideoFormats.IsVideo))
        {
            ct.ThrowIfCancellationRequested();
            currentPath = path;
            activity.Stage("读取视频", detail: Path.GetFileName(path));
            try
            {
                var file = new FileInfo(path); var length = file.Length; var modified = file.LastWriteTimeUtc;
                var info = await engine.Probe(path, ct).ConfigureAwait(false);
                if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("视频时长无效。");
                var (duration, frameRate) = BatchVideoTools.VideoTiming(info);
                var count = (int)Math.Min(options.VideoFrames, Math.Max(1, Math.Ceiling(duration * 2)));
                var unique = new List<(byte[] Image, byte[] Signature, double Seconds)>();
                var samples = new int[count];
                var sampleSeconds = new double[count];
                var evidence = new IReadOnlyList<MediaTagScore>[count];
                var frameValues = new float[count][];
                activity.Stage("采样画面", 0, count, "帧");
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var seconds = Math.Min(duration * (i + .5) / count, Math.Max(0, duration - Math.Max(.1, 1 / frameRate)));
                    sampleSeconds[i] = seconds;
                    var png = await engine.Thumbnail(path, seconds, 768, 768, ct, pad: false, videoStreamIndex: info.VideoStreamIndex).ConfigureAwait(false);
                    var signature = options.ReuseSimilarFrames ? VideoFrameSimilarity.FromEncoded(png) : [];
                    var reused = options.ReuseSimilarFrames ? unique.FindIndex(item => VideoFrameSimilarity.Similar(item.Signature, signature)) : -1;
                    samples[i] = reused >= 0 ? reused : unique.Count;
                    if (reused < 0) unique.Add((png, signature, seconds));
                    activity.Frame(png, $"{file.Name} · {MediaTime.Format(seconds)}");
                    activity.Advance(i + 1, count, "帧", $"待推理 {unique.Count} 帧 · 复用 {i + 1 - unique.Count} 帧");
                }
                var mean = new double[tags.Length]; var maximum = new double[tags.Length];
                var scored = 0;
                MediaTagResult Snapshot() => new(path, tags.Select((tag, j) => new MediaTagScore(tag, mean[j] * count / Math.Max(1, scored), maximum[j])).ToArray(),
                    count, samples.Where((_, index) => frameValues[index] is not null).Distinct().Count(), session.Backend, length, modified, session.FallbackReason)
                {
                    DurationSeconds = duration,
                    Frames = sampleSeconds.Select((seconds, index) => (seconds, index)).Where(item => frameValues[item.index] is not null)
                        .Select(item => new MediaTagFrame(item.seconds, evidence[item.index]) { Values = frameValues[item.index] }).ToArray()
                };
                void PublishPreview(MediaTagResult value) => progress?.Report(new(path, null, null, completed, files.Length) { PreviewResult = value });
                activity.Stage("识别视频标签", 0, count, "帧");
                activity.Backend(session.Backend);
                for (var offset = 0; offset < unique.Count; offset += options.BatchSize)
                {
                    var vectors = Predict(session, unique.Skip(offset).Take(options.BatchSize).Select(item => item.Image).ToArray(), options.BatchSize, tags.Length, ct);
                    for (var i = 0; i < vectors.Length; i++)
                    {
                        var frameScores=tags.Select((tag,j)=>new MediaTagScore(tag,vectors[i][j],vectors[i][j])).Where(score=>score.Score>=.1).OrderByDescending(score=>score.Score).Take(128).ToArray();
                        for(var sample=0;sample<count;sample++)if(samples[sample]==offset+i) { evidence[sample]=frameScores; frameValues[sample]=vectors[i]; }
                        var weight = samples.Count(sample => sample == offset + i);
                        scored += weight;
                        for (var j = 0; j < tags.Length; j++) { mean[j] += vectors[i][j] * weight / count; maximum[j] = Math.Max(maximum[j], vectors[i][j]); }
                    }
                    var latest = unique[Math.Min(offset + vectors.Length, unique.Count) - 1];
                    activity.Backend(session.Backend);
                    activity.Frame(latest.Image, $"{file.Name} · {MediaTime.Format(latest.Seconds)}");
                    activity.Result("当前标签 · " + string.Join(" · ", tags.Select((tag, j) => new MediaTagScore(tag, mean[j] * count / scored, maximum[j]))
                        .OrderByDescending(score => score.Score).Take(5).Select(score => $"{WordLibraryCatalog.TagLabel(score.Tag)} {score.Score:0.00}")));
                    activity.Advance(scored, count, "帧", session.FallbackReason is null ? "采样平均分，阶段候选" : "已切换 CPU · 采样平均分，阶段候选");
                    PublishPreview(Snapshot());
                }
                var tagResult = Snapshot();
                var scene = await SceneResultAsync(unique.Select(frame => frame.Image).ToArray(), sampleSeconds, samples,
                    value => PublishPreview(tagResult with { Scenes = value })).ConfigureAwait(false);
                CheckSource(file, length, modified);
                Report(path, tagResult with { Scenes = scene.Result, SceneError = scene.Error }, null);
            }
            catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested) { Report(path, null, error.Message); }
        }
        activity.Finish("标签分析完成");
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
