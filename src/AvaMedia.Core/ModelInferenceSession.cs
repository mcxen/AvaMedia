using Microsoft.ML.OnnxRuntime;

namespace AvaMedia.Core;

/// <summary>Platform acceleration with a CPU retry if model compilation or execution fails.</summary>
internal sealed class ModelInferenceSession : IDisposable
{
    private readonly string _path;
    private InferenceSession _session;
    public string Backend { get; private set; } = "CPU";
    public string InputName => _session.InputMetadata.Keys.First();

    public ModelInferenceSession(string path, string modelHash, bool preferGpu)
    {
        _path = path;
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
                        "AvaMedia", "inference-cache", typeof(InferenceSession).Assembly.GetName().Version!.ToString(), modelHash);
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
            }
            catch (Exception error) when (error is OnnxRuntimeException or NotSupportedException or DllNotFoundException
                or EntryPointNotFoundException or IOException or UnauthorizedAccessException) { }
        }
        _session = CpuSession();
    }

    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(NamedOnnxValue input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return _session.Run([input]); }
        catch (OnnxRuntimeException) when (Backend != "CPU" && !ct.IsCancellationRequested)
        {
            _session.Dispose(); Backend = "CPU";
            _session = CpuSession();
            ct.ThrowIfCancellationRequested();
            return _session.Run([input]);
        }
    }

    private InferenceSession CpuSession() { using var options = Options(); return new(_path, options); }
    private static SessionOptions Options() => new()
    {
        IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
    };
    public void Dispose() => _session.Dispose();
}
