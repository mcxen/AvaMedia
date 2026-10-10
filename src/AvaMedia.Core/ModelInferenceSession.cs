using Microsoft.ML.OnnxRuntime;

namespace AvaMedia.Core;

/// <summary>Platform acceleration with a CPU retry if model compilation or execution fails.</summary>
internal sealed class ModelInferenceSession : IDisposable
{
    private readonly string _path;
    private readonly byte[]? _modelData;
    private readonly int? _batchSize;
    private readonly int? _imageSize;
    private InferenceSession _session;
    public string Backend { get; private set; } = "CPU";
    public string? FallbackReason { get; private set; }
    public string InputName => _session.InputMetadata.Keys.First();

    public ModelInferenceSession(string path, string modelHash, int? batchSize = null, byte[]? modelData = null, int? imageSize = null)
    {
        _path = path; _batchSize = batchSize; _modelData = modelData; _imageSize = imageSize;
        // Acceleration is always attempted. CPU is reserved for unavailable or failing providers.
        try
        {
            using var options = Options();
            var providers = OrtEnv.Instance().GetAvailableProviders();
            if (OperatingSystem.IsMacOS() && providers.Contains("CoreMLExecutionProvider"))
            {
                // Separate caches by model content and runtime version, not the mutable file path.
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AvaMedia", "inference-cache", typeof(InferenceSession).Assembly.GetName().Version!.ToString(), modelHash,
                    (batchSize is { } size ? $"batch-{size}" : "dynamic") + (imageSize is { } pixels ? $"-image-{pixels}" : ""));
                Directory.CreateDirectory(cache);
                options.AppendExecutionProvider("CoreML", new()
                {
                    ["MLComputeUnits"] = "ALL",
                    ["ModelFormat"] = OperatingSystem.IsMacOSVersionAtLeast(12) ? "MLProgram" : "NeuralNetwork",
                    ["RequireStaticInputShapes"] = "1",
                    ["ModelCacheDirectory"] = cache
                });
                _session = CreateSession(options); Backend = "Core ML / CPU"; return;
            }
            if (OperatingSystem.IsWindows() && providers.Contains("DmlExecutionProvider"))
            {
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML();
                _session = CreateSession(options); Backend = "DirectML / CPU"; return;
            }
            FallbackReason = OperatingSystem.IsMacOS() ? "Core ML execution provider is unavailable."
                : OperatingSystem.IsWindows() ? "DirectML execution provider is unavailable."
                : "No supported GPU execution provider on this platform.";
        }
        catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException or DllNotFoundException
            or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { FallbackReason = error.Message; }
        _session = CpuSession();
    }

    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(NamedOnnxValue input, CancellationToken ct)
        => RunWithFallback(run => _session.Run([input], _session.OutputNames, run), ct);

    public IDisposableReadOnlyCollection<OrtValue> Run(IReadOnlyDictionary<string, OrtValue> inputs,
        IReadOnlyCollection<string> outputNames, CancellationToken ct)
        => RunWithFallback(run => _session.Run(run, inputs, outputNames), ct);

    private T RunWithFallback<T>(Func<RunOptions, T> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var run = new RunOptions();
        using var cancellation = ct.Register(() => run.Terminate = true);
        try { return execute(run); }
        catch (OnnxRuntimeException error) when (Backend != "CPU" && !ct.IsCancellationRequested)
        {
            _session.Dispose(); Backend = "CPU"; FallbackReason = error.Message;
            _session = CpuSession();
            ct.ThrowIfCancellationRequested();
            try { return execute(run); }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }

    private InferenceSession CreateSession(SessionOptions options) => _modelData is null ? new(_path, options) : new(_modelData, options);
    private InferenceSession CpuSession() { using var options = Options(); return CreateSession(options); }
    private SessionOptions Options()
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (_batchSize is { } batch) options.AddFreeDimensionOverrideByName("batch_size", batch);
        // YuNet's dynamic height/width must be fixed for each sampling size so Core ML can claim the graph.
        if (_imageSize is { } size)
        {
            options.AddFreeDimensionOverrideByName("height", size);
            options.AddFreeDimensionOverrideByName("width", size);
        }
        return options;
    }
    public void Dispose() => _session.Dispose();
}
