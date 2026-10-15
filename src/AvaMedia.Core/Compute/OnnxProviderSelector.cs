using Microsoft.ML.OnnxRuntime;

namespace AvaMedia.Core.Compute;

public sealed record OnnxSelection<T>(T Value, string Backend, string? FallbackReason, ComputeOutcome Outcome);

public static class OnnxProviderSelector
{
    public static string Provider(ComputeBackend backend) => backend switch
    {
        ComputeBackend.CoreML => "CoreMLExecutionProvider", ComputeBackend.DirectML => "DmlExecutionProvider",
        ComputeBackend.CudaEp => "CUDAExecutionProvider", ComputeBackend.RocmEp => "ROCMExecutionProvider",
        ComputeBackend.OpenVino => "OpenVINOExecutionProvider", _ => "CPUExecutionProvider"
    };
    public static string UnavailableReason(ComputePlatform platform, IReadOnlyCollection<string> providers)
        => platform == ComputePlatform.Linux && !new[] { "CUDAExecutionProvider", "ROCMExecutionProvider", "OpenVINOExecutionProvider" }.Any(providers.Contains)
            ? "当前打包的 ONNX Runtime 为 CPU 版本；未提供 CUDA / ROCm / OpenVINO EP。"
            : "当前 ONNX Runtime 没有此执行提供程序或所需驱动。";

    /// <summary>Pure routing entry point; the factory can be a fake, no native GPU is needed.</summary>
    public static OnnxSelection<T> Select<T>(ComputePlatform platform, ComputePreference preference, IReadOnlyCollection<string> providers,
        Func<ComputeBackend, T> initialize, string model = "", Action<string>? log = null, ComputeCapabilities? capabilities = null, string fingerprint = "")
    {
        var route = new ComputeRouter(platform, capabilities, fingerprint).Plan(new("ONNX", ComputeCapability.OnnxProvider, model, preference), log);
        while (true)
        {
            var backend = route.Chosen;
            if (backend != ComputeBackend.Cpu && !providers.Contains(Provider(backend)))
            { route.ReportFailure(backend, UnavailableReason(platform, providers), persist: false); continue; }
            try
            {
                var value = initialize(backend); route.ReportSuccess(); var outcome = route.Outcome();
                var reason = outcome.FallbackChain.Count == 0 ? preference == ComputePreference.ForceCpu ? "设置要求使用 CPU。" : null : ComputeRuntimeFormatter.Note(outcome);
                return new(value, backend == ComputeBackend.Cpu ? "CPU" : ComputeRuntimeFormatter.Name(backend) + " / CPU", reason, outcome);
            }
            catch (Exception e) when (backend != ComputeBackend.Cpu && IsProviderFailure(e))
            { route.ReportFailure(backend, "初始化失败：" + e.Message); }
        }
    }
    internal static bool IsProviderFailure(Exception e) => e is OnnxRuntimeException or NotSupportedException or DllNotFoundException
        or EntryPointNotFoundException or IOException or UnauthorizedAccessException or TypeInitializationException or InvalidOperationException;

    public static OnnxSelection<T> Create<T>(Func<SessionOptions> optionsFactory, Func<SessionOptions, T> create, string model,
        string? coreMlCache = null, ComputePreference? preference = null)
    {
        var platform = ComputeEnvironment.Platform;
        string[] providers;
        try { providers = OrtEnv.Instance().GetAvailableProviders(); }
        catch (Exception e) when (IsProviderFailure(e))
        { ComputeRuntimeFormatter.Log("ONNX 提供程序查询失败：" + e.Message); providers = ["CPUExecutionProvider"]; }
        var fingerprint = ComputeEnvironment.Fingerprint();
        var capabilities = ComputeEnvironment.Capabilities;
        return Select(platform, preference ?? ComputeSettings.CurrentPreference, providers, backend =>
        {
            if (backend != ComputeBackend.Cpu)
            {
                var request = new ComputeRequest("ONNX", ComputeCapability.OnnxProvider, model);
                var result = capabilities.GetAsync(request, backend, fingerprint, new OnnxProviderProbe(providers)).GetAwaiter().GetResult();
                if (!result.Available) throw new InvalidOperationException(result.Reason);
            }
            using var options = optionsFactory();
            if (backend != ComputeBackend.Cpu) Append(options, backend, coreMlCache);
            try { return create(options); }
            catch (Exception e) when (backend == ComputeBackend.CoreML && OperatingSystem.IsMacOSVersionAtLeast(12) && IsProviderFailure(e))
            {
                ComputeRuntimeFormatter.Log("Core ML MLProgram 初始化失败，尝试 NeuralNetwork：" + e.Message);
                using var retry = optionsFactory(); Append(retry, backend, coreMlCache, neuralNetwork: true);
                try { return create(retry); }
                catch (Exception second) when (IsProviderFailure(second))
                { throw new InvalidOperationException("MLProgram：" + e.Message + "；NeuralNetwork：" + second.Message, second); }
            }
        }, model, capabilities: capabilities, fingerprint: fingerprint);
    }
    public static void Append(SessionOptions options, ComputeBackend backend, string? cache = null, bool neuralNetwork = false)
    {
        switch (backend)
        {
            case ComputeBackend.DirectML:
                options.EnableMemoryPattern = false; options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(); break;
            case ComputeBackend.CudaEp: options.AppendExecutionProvider_CUDA(); break;
            case ComputeBackend.RocmEp: options.AppendExecutionProvider_ROCm(); break;
            // Require a GPU device; AUTO can silently choose OpenVINO's CPU plugin.
            case ComputeBackend.OpenVino: options.AppendExecutionProvider_OpenVINO("GPU"); break;
            case ComputeBackend.CoreML:
                var values = new Dictionary<string, string> { ["MLComputeUnits"] = "ALL", ["ModelFormat"] = neuralNetwork || !OperatingSystem.IsMacOSVersionAtLeast(12) ? "NeuralNetwork" : "MLProgram", ["RequireStaticInputShapes"] = "1" };
                if (cache is not null) { Directory.CreateDirectory(cache); values["ModelCacheDirectory"] = cache; }
                options.AppendExecutionProvider("CoreML", values); break;
        }
    }
}

public sealed class OnnxProviderProbe(IReadOnlyCollection<string>? providers = null, Action<ComputeBackend>? validate = null) : IComputeProbe
{
    // An opset-13 Identity graph, float32[1], no weights or external files.
    private static readonly byte[] Model = Convert.FromBase64String("CAgSCEF2YU1lZGlhOkQKEAoBeBIBeSIISWRlbnRpdHkSDmF2YW1lZGlhX3Byb2JlWg8KAXgSCgoICAESBAoCCAFiDwoBeRIKCggIARIECgIIAUICEA0=");
    public Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var available = providers ?? OrtEnv.Instance().GetAvailableProviders();
        if (!available.Contains(OnnxProviderSelector.Provider(backend)))
            return Task.FromResult(new CapabilityResult(request.Capability, backend, false, OnnxProviderSelector.UnavailableReason(ComputeEnvironment.Platform, available), default, fingerprint));
        if (validate is not null) validate(backend);
        else
        {
            using var options = new SessionOptions(); OnnxProviderSelector.Append(options, backend);
            using var session = new InferenceSession(Model, options);
        }
        return Task.FromResult(new CapabilityResult(request.Capability, backend, true, "执行提供程序已完成最小模型初始化。", default, fingerprint));
    }
}
