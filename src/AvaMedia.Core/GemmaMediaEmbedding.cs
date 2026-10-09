using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AvaMedia.Core;

/// <summary>
/// EmbeddingGemma 2 (onnx-community q4) in-process through ONNX Runtime. Images go through the vision encoder; its
/// image_features fill the &lt;|image|&gt; placeholders of the text model, whose sentence_embedding is the 768-d vector.
/// Text uses the classification task prefix. No remote uploads, helper processes or implicit downloads.
/// </summary>
public sealed class GemmaMediaEmbedding : IAsyncDisposable
{
    public const int Dimensions = 768;
    /// <summary>Vision soft-token budget (supported: 70, 140, 280, 560, 1120). See docs/MODELS.md for the measured trade-off.</summary>
    public const int VisionTokens = 140;
    private const string TaskPrefix = "task: classification | query: ";
    private const int FeatureSize = 512;
    private readonly ModelLease _model;
    private readonly bool _ownsModel;
    private readonly Dictionary<string, float[]> _labelCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _labelOrder = new();
    private readonly GemmaTokenizer _tokenizer;
    private readonly int _imageToken, _imageStart, _imageEnd;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession _vision, _text;
    private float[][] _labels = [];
    private string _backend;
    private bool _disposed;
    public string Backend => Volatile.Read(ref _backend);
    public string? FallbackReason { get; private set; }
    public string[] AccelerationDetails => [];

    private GemmaMediaEmbedding(ModelLease model, GemmaTokenizer tokenizer, InferenceSession vision, InferenceSession text, string backend, bool ownsModel)
    {
        _model = model; _ownsModel = ownsModel; _tokenizer = tokenizer; _vision = vision; _text = text; _backend = backend;
        _imageToken = tokenizer.TokenId("<|image|>"); _imageStart = tokenizer.TokenId("<|image>"); _imageEnd = tokenizer.TokenId("<image|>");
    }

    public static Task<GemmaMediaEmbedding> StartAsync(ModelStore store, CancellationToken ct, bool preferGpu = true, Action<string>? status = null)
        => LoadAsync(() => store.AcquireAsync(ModelCatalog.EmbeddingId, ct), ct, preferGpu, status, ownsModel: true);

    internal static Task<GemmaMediaEmbedding> StartCachedAsync(ModelLease model, CancellationToken ct, bool preferGpu, Action<string>? status)
        => LoadAsync(() => Task.FromResult(model), ct, preferGpu, status, ownsModel: false);

    private static Task<GemmaMediaEmbedding> LoadAsync(Func<Task<ModelLease>> acquire, CancellationToken ct, bool preferGpu,
        Action<string>? status, bool ownsModel)
        => Task.Run(async () =>
        {
            status?.Invoke("校验嵌入模型");
            var lease = await acquire().ConfigureAwait(false);
            InferenceSession? vision = null, text = null;
            try
            {
                status?.Invoke("加载分词器");
                var tokenizer = GemmaTokenizer.Load(Path.Combine(lease.Directory, ModelCatalog.GemmaTokenizerFile));
                ct.ThrowIfCancellationRequested();
                status?.Invoke("加载嵌入模型");
                string? fallback = null; var backend = "CPU";
                if (preferGpu && AcceleratedOptions() is { } accelerated)
                {
                    try
                    {
                        using (accelerated.Options)
                        {
                            vision = new(Path.Combine(lease.Directory, ModelCatalog.GemmaVisionFile), accelerated.Options);
                            text = new(Path.Combine(lease.Directory, ModelCatalog.GemmaTextFile), accelerated.Options);
                        }
                        backend = accelerated.Name;
                    }
                    catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
                    { vision?.Dispose(); text?.Dispose(); vision = text = null; fallback = error.Message; }
                }
                else if (preferGpu) fallback = OperatingSystem.IsWindows() ? "DirectML execution provider is unavailable."
                    : OperatingSystem.IsMacOS() ? "Core ML execution provider is unavailable." : "No supported GPU execution provider on this platform.";
                if (vision is null || text is null)
                {
                    using var options = CpuOptions();
                    vision = new(Path.Combine(lease.Directory, ModelCatalog.GemmaVisionFile), options);
                    text = new(Path.Combine(lease.Directory, ModelCatalog.GemmaTextFile), options);
                }
                var embedding = new GemmaMediaEmbedding(lease, tokenizer, vision, text, backend, ownsModel) { FallbackReason = fallback };
                vision = text = null; lease = null!;
                try
                {
                    // Warm up and validate: accelerated providers that cannot run the q4 contrib ops fall back to CPU here.
                    status?.Invoke("等待嵌入模型就绪");
                    await embedding.EmbedLabelsAsync(["A photo."], ct).ConfigureAwait(false);
                }
                catch { await embedding.DisposeAsync().ConfigureAwait(false); throw; }
                return embedding;
            }
            catch
            {
                vision?.Dispose(); text?.Dispose(); if (ownsModel) lease?.Dispose();
                throw;
            }
        }, ct);

    private static (string Name, SessionOptions Options)? AcceleratedOptions()
    {
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            var options = CpuOptions();
            try
            {
                if (OperatingSystem.IsWindows() && providers.Contains("DmlExecutionProvider"))
                {
                    options.EnableMemoryPattern = false; options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML();
                    return ("DirectML / CPU", options);
                }
                if (OperatingSystem.IsMacOS() && providers.Contains("CoreMLExecutionProvider"))
                {
                    var model = ModelCatalog.Find(ModelCatalog.EmbeddingId);
                    var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "inference-cache",
                        typeof(InferenceSession).Assembly.GetName().Version!.ToString(), model.Files.First(file => file.Path == ModelCatalog.GemmaTextFile).Sha256);
                    Directory.CreateDirectory(cache);
                    options.AppendExecutionProvider("CoreML", new() { ["MLComputeUnits"] = "ALL", ["ModelFormat"] = "MLProgram", ["ModelCacheDirectory"] = cache });
                    return ("Core ML / CPU", options);
                }
            }
            catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException or EntryPointNotFoundException
                or IOException or UnauthorizedAccessException) { }
            options.Dispose();
        }
        catch (Exception error) when (error is OnnxRuntimeException or DllNotFoundException or TypeInitializationException) { }
        return null;
    }

    private static SessionOptions CpuOptions() => new()
    {
        IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
    };

    /// <returns>Similarity margin for person versus empty-scene descriptions; not a probability.</returns>
    public async Task<double> PersonMarginAsync(byte[] png, CancellationToken ct)
    {
        if (_labels.Length == 0)
            _labels = await EmbedLabelsAsync([
                "A person visible in the scene.", "People standing, sitting or walking.", "A person seen from behind or from the side.",
                "An empty room with nobody present.", "An outdoor scene without any people.", "A blank or black video frame."
            ], ct).ConfigureAwait(false);
        var vector = await EmbedImageAsync(png, ct).ConfigureAwait(false);
        var scores = _labels.Select(label => Cosine(vector, label)).ToArray();
        return scores.Take(3).Max() - scores.Skip(3).Max();
    }

    public Task<float[][]> EmbedLabelsAsync(IReadOnlyList<string> descriptions, CancellationToken ct)
    {
        if (descriptions.Count is < 1 or > 32 || descriptions.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > 512))
            throw new ArgumentException("请输入 1–32 个关键词，描述不超过 512 字符。");
        return RunAsync(() =>
        {
            var retained = descriptions.Select(text => _labelCache.GetValueOrDefault(text)).ToArray();
            var missing = descriptions.Where(text => !_labelCache.ContainsKey(text)).Distinct(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                var tokens = missing.Select(text => _tokenizer.Encode(TaskPrefix + text)).ToArray();
                var length = tokens.Max(item => item.Count);
                var ids = new long[tokens.Length * length]; var mask = new long[ids.Length];
                for (var row = 0; row < tokens.Length; row++)
                    for (var column = 0; column < tokens[row].Count; column++)
                    { ids[row * length + column] = tokens[row][column]; mask[row * length + column] = 1; }
                var vectors = Text(ids, mask, tokens.Length, length, null, 0);
                for (var index = 0; index < missing.Length; index++)
                {
                    while (_labelCache.Count >= 32768) _labelCache.Remove(_labelOrder.Dequeue());
                    _labelCache.Add(missing[index], vectors[index]); _labelOrder.Enqueue(missing[index]);
                }
            }
            return descriptions.Select((text, index) => (retained[index] ?? _labelCache[text]).ToArray()).ToArray();
        }, ct);
    }

    public async Task<float[]> EmbedImageAsync(byte[] png, CancellationToken ct)
        => (await EmbedImagesAsync([png], ct).ConfigureAwait(false))[0];

    public async Task<float[][]> EmbedImagesAsync(IReadOnlyList<byte[]> images, CancellationToken ct)
    {
        if (images.Count is < 1 or > 4 || images.Any(image => image.Length == 0)) throw new ArgumentException("每批须包含 1–4 个有效画面。");
        var vectors = new float[images.Count][];
        for (var index = 0; index < images.Count; index++)
        {
            var image = await Task.Run(() => GemmaImageProcessor.Process(images[index], VisionTokens), ct).ConfigureAwait(false);
            vectors[index] = (await RunAsync(() =>
            {
                float[] features;
                using (var pixels = OrtValue.CreateTensorValueFromMemory(image.Pixels, [1, image.MaxPatches, GemmaImageProcessor.PatchSize * GemmaImageProcessor.PatchSize * 3]))
                using (var positions = OrtValue.CreateTensorValueFromMemory(image.Positions, [1, image.MaxPatches, 2]))
                using (var run = new RunOptions())
                using (var outputs = _vision.Run(run, ["pixel_values", "pixel_position_ids"], [pixels, positions], ["image_features"]))
                    features = outputs[0].GetTensorDataAsSpan<float>().ToArray();
                var count = features.Length / FeatureSize;
                if (count != image.SoftTokens || count == 0) throw new InvalidDataException("视觉编码器输出的画面标记数量不符。");
                var ids = new long[count + 4]; var mask = new long[ids.Length];
                ids[0] = GemmaTokenizer.BosId; ids[1] = _imageStart; ids[^2] = _imageEnd; ids[^1] = GemmaTokenizer.EosId;
                for (var token = 0; token < count; token++) ids[token + 2] = _imageToken;
                Array.Fill(mask, 1L);
                return Text(ids, mask, 1, ids.Length, features, count);
            }, ct).ConfigureAwait(false))[0];
        }
        return vectors;
    }

    private float[][] Text(long[] ids, long[] mask, int batch, int length, float[]? features, int featureCount)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(ids, [batch, length]);
        using var attention = OrtValue.CreateTensorValueFromMemory(mask, [batch, length]);
        using var image = features is null ? Empty() : OrtValue.CreateTensorValueFromMemory(features, [featureCount, FeatureSize]);
        using var video = Empty();
        using var audio = Empty();
        using var run = new RunOptions();
        using var outputs = _text.Run(run, ["input_ids", "attention_mask", "image_features", "video_features", "audio_features"],
            [input, attention, image, video, audio], ["sentence_embedding"]);
        var data = outputs[0].GetTensorDataAsSpan<float>();
        if (data.Length != batch * Dimensions) throw new InvalidDataException("嵌入模型返回的向量数量不符。");
        var vectors = new float[batch][];
        for (var row = 0; row < batch; row++)
        {
            var vector = data.Slice(row * Dimensions, Dimensions).ToArray();
            double norm = 0; foreach (var value in vector) norm += value * value;
            if (vector.Any(value => !float.IsFinite(value)) || norm == 0) throw new InvalidDataException("嵌入模型返回了无效向量。");
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var index = 0; index < vector.Length; index++) vector[index] *= scale;
            vectors[row] = vector;
        }
        return vectors;
    }

    private static OrtValue Empty() => OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, [0, FeatureSize]);

    private async Task<float[][]> RunAsync(Func<float[][]> run, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                try { return run(); }
                catch (OnnxRuntimeException error) when (Backend != "CPU" && !ct.IsCancellationRequested)
                {
                    // Accelerated providers may reject quantized contrib ops at run time; continue on CPU.
                    using var options = CpuOptions();
                    var vision = new InferenceSession(Path.Combine(_model.Directory, ModelCatalog.GemmaVisionFile), options);
                    var text = new InferenceSession(Path.Combine(_model.Directory, ModelCatalog.GemmaTextFile), options);
                    _vision.Dispose(); _text.Dispose(); _vision = vision; _text = text;
                    Volatile.Write(ref _backend, "CPU"); FallbackReason = error.Message;
                    return run();
                }
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public static double Cosine(float[] left, float[] right)
    {
        if (left.Length != Dimensions || right.Length != Dimensions) throw new ArgumentException("嵌入向量维度不符。");
        double dot = 0, a = 0, b = 0;
        for (var index = 0; index < left.Length; index++) { dot += left[index] * right[index]; a += left[index] * left[index]; b += right[index] * right[index]; }
        return dot / Math.Sqrt(a * b);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _vision.Dispose(); _text.Dispose(); if (_ownsModel) _model.Dispose();
            _labelCache.Clear(); _labelOrder.Clear();
        }
        finally { _gate.Release(); }
    }
}
