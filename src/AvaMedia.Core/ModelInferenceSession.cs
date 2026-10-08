using Microsoft.ML.OnnxRuntime;

namespace AvaMedia.Core;

/// <summary>Platform acceleration with a CPU retry if model compilation or execution fails.</summary>
internal sealed class ModelInferenceSession : IDisposable
{
    private readonly string _path;
    private readonly int? _batchSize;
    private InferenceSession _session;
    public string Backend { get; private set; } = "CPU";
    public string? FallbackReason { get; private set; }
    public string InputName => _session.InputMetadata.Keys.First();

    public ModelInferenceSession(string path, string modelHash, bool preferGpu, int? batchSize = null)
    {
        _path = path; _batchSize = batchSize;
        if (preferGpu)
        {
            try
            {
                using var options = Options();
                var providers = OrtEnv.Instance().GetAvailableProviders();
                if (OperatingSystem.IsMacOS() && providers.Contains("CoreMLExecutionProvider"))
                {
                    // Separate caches by model content and runtime version, not the mutable file path.
                    var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AvaMedia", "inference-cache", typeof(InferenceSession).Assembly.GetName().Version!.ToString(), modelHash,
                        batchSize is { } size ? $"batch-{size}" : "dynamic");
                    Directory.CreateDirectory(cache);
                    options.AppendExecutionProvider("CoreML", new()
                    {
                        ["MLComputeUnits"] = "ALL",
                        ["ModelFormat"] = OperatingSystem.IsMacOSVersionAtLeast(12) ? "MLProgram" : "NeuralNetwork",
                        ["RequireStaticInputShapes"] = "1",
                        ["ModelCacheDirectory"] = cache
                    });
                    _session = new(path, options); Backend = "Core ML / CPU"; return;
                }
                if (OperatingSystem.IsWindows() && providers.Contains("DmlExecutionProvider"))
                {
                    options.EnableMemoryPattern = false;
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.AppendExecutionProvider_DML();
                    _session = new(path, options); Backend = "DirectML / CPU"; return;
                }
                FallbackReason = OperatingSystem.IsMacOS() ? "Core ML execution provider is unavailable."
                    : OperatingSystem.IsWindows() ? "DirectML execution provider is unavailable."
                    : "No supported GPU execution provider on this platform.";
            }
            catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException or DllNotFoundException
                or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { FallbackReason = error.Message; }
        }
        _session = CpuSession();
    }

    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(NamedOnnxValue input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return _session.Run([input]); }
        catch (OnnxRuntimeException error) when (Backend != "CPU" && !ct.IsCancellationRequested)
        {
            _session.Dispose(); Backend = "CPU"; FallbackReason = error.Message;
            _session = CpuSession();
            ct.ThrowIfCancellationRequested();
            return _session.Run([input]);
        }
    }

    private InferenceSession CpuSession() { using var options = Options(); return new(_path, options); }
    private SessionOptions Options()
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (_batchSize is { } batch) options.AddFreeDimensionOverrideByName("batch_size", batch);
        return options;
    }
    public void Dispose() => _session.Dispose();
}
