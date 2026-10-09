using Microsoft.ML.OnnxRuntime;
using System.Diagnostics;

namespace AvaMedia.Core;

/// <summary>Platform acceleration with a CPU retry if model compilation or execution fails.</summary>
internal sealed class ModelInferenceSession : IDisposable
{
    private readonly string _path;
    private readonly byte[]? _modelData;
    private readonly int? _batchSize;
    private InferenceSession _session;
    public string Backend { get; private set; } = "CPU";
    public string? FallbackReason { get; private set; }
    public string? BackendSelectionReason { get; private set; }
    public string InputName => _session.InputMetadata.Keys.First();

    public ModelInferenceSession(string path, string modelHash, bool preferGpu, int? batchSize = null, byte[]? modelData = null)
    {
        _path = path; _batchSize = batchSize; _modelData = modelData;
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

    /// <summary>Partially accelerated small detectors can run faster on CPU. Compare warmed sessions on a real input.</summary>
    public void SelectFastestBackend(NamedOnnxValue input, CancellationToken ct)
    {
        if (Backend == "CPU") return;
        using var options = Options();
        InferenceSession? cpu = null;
        try
        {
            cpu = CreateSession(options);
            using (Run(input, ct)) { }
            if (Backend == "CPU") return; // An execution failure already selected a CPU session.
            using (cpu.Run([input])) { }
            var accelerated = Measure(_session);
            var software = Measure(cpu);
            var acceleratedName = Backend;
            if (software < accelerated * .85)
            {
                _session.Dispose(); _session = cpu; cpu = null; Backend = "CPU";
            }
            BackendSelectionReason = FormattableString.Invariant(
                $"{acceleratedName} {accelerated:F2} ms/frame; CPU {software:F2} ms/frame; selected {Backend}.");
        }
        catch (OnnxRuntimeException) when (!ct.IsCancellationRequested)
        {
            // A failed optional CPU comparison leaves the working accelerated session available.
        }
        finally { cpu?.Dispose(); }

        double Measure(InferenceSession session)
        {
            Span<double> times = stackalloc double[3];
            for (var index = 0; index < times.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var start = Stopwatch.GetTimestamp();
                using (session.Run([input])) { }
                times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            times.Sort(); return times[1];
        }
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
        return options;
    }
    public void Dispose() => _session.Dispose();
}
