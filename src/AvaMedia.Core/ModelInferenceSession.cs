using AvaMedia.Core.Compute;
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
    private ComputeOutcome _outcome;
    public string Backend { get; private set; } = "CPU";
    public string? FallbackReason { get; private set; }
    public string InputName => _session.InputMetadata.Keys.First();

    public ModelInferenceSession(string path, string modelHash, int? batchSize = null, byte[]? modelData = null, int? imageSize = null)
    {
        _path = path; _batchSize = batchSize; _modelData = modelData; _imageSize = imageSize;
        var cache = Path.Combine(ComputeEnvironment.Folder, "inference-cache", typeof(InferenceSession).Assembly.GetName().Version!.ToString(), modelHash,
            (batchSize is { } size ? $"batch-{size}" : "dynamic") + (imageSize is { } pixels ? $"-image-{pixels}" : ""));
        var selection = OnnxProviderSelector.Create(Options, CreateSession, modelHash, cache);
        _session = selection.Value; Backend = selection.Backend; FallbackReason = selection.FallbackReason;
        _outcome = selection.Outcome;
    }

    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(NamedOnnxValue input, CancellationToken ct)
        => RunWithFallback(run => _session.Run([input], _session.OutputNames, run), ct);

    public IDisposableReadOnlyCollection<OrtValue> Run(IReadOnlyDictionary<string, OrtValue> inputs,
        IReadOnlyCollection<string> outputNames, CancellationToken ct)
        => RunWithFallback(run => _session.Run(run, inputs, outputNames), ct);

    public void FallbackToCpu(string reason)
    {
        if (Backend == "CPU") return;
        ComputeEnvironment.Capabilities.ReportFailure(_outcome.Requested, _outcome.ActualBackend, ComputeEnvironment.Fingerprint(), reason);
        ComputeRuntimeFormatter.Log("回退 CPU：" + Backend + " 推理失败：" + reason);
        var replacement = CpuSession();
        var previous = _session;
        _session = replacement; Backend = "CPU"; FallbackReason = reason;
        previous.Dispose();
    }

    private T RunWithFallback<T>(Func<RunOptions, T> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Backend == "CPU") CpuResourcePolicy.Yield(ct);
        using var run = new RunOptions();
        using var cancellation = ct.Register(() => run.Terminate = true);
        try { return execute(run); }
        catch (OnnxRuntimeException error) when (Backend != "CPU" && !ct.IsCancellationRequested)
        {
            FallbackToCpu(error.Message);
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
            IntraOpNumThreads = CpuResourcePolicy.Threads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4)),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        if (_batchSize is { } batch) options.AddFreeDimensionOverrideByName("batch_size", batch);
        // Fixed sampling sizes let Core ML claim YuNet and DINOv2 graphs with dynamic input dimensions.
        if (_imageSize is { } size)
        {
            options.AddFreeDimensionOverrideByName("num_channels", 3);
            options.AddFreeDimensionOverrideByName("height", size);
            options.AddFreeDimensionOverrideByName("width", size);
        }
        return options;
    }
    public void Dispose() => _session.Dispose();
}
