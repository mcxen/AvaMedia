namespace AvaMedia.Core.Compute;

public sealed class ComputeRouter(ComputePlatform platform, ComputeCapabilities? capabilities = null, string fingerprint = "")
{
    public ComputeRoute Plan(ComputeRequest request, Action<string>? log = null)
        => new(request, Candidates(platform, request.Capability, request.Preference), capabilities, fingerprint, log);
    public static IReadOnlyList<ComputeBackend> Candidates(ComputePlatform platform, ComputeCapability capability, ComputePreference preference = ComputePreference.Auto)
    {
        if (preference == ComputePreference.ForceCpu) return [ComputeBackend.Cpu];
        ComputeBackend[] gpu = (platform, capability) switch
        {
            (ComputePlatform.MacOS, ComputeCapability.VideoEncode or ComputeCapability.VideoDecode) => [ComputeBackend.VideoToolbox],
            (ComputePlatform.Windows, ComputeCapability.VideoEncode) => [ComputeBackend.Nvenc, ComputeBackend.Qsv, ComputeBackend.Amf],
            (ComputePlatform.Linux, ComputeCapability.VideoEncode) => [ComputeBackend.Nvenc, ComputeBackend.Vaapi, ComputeBackend.Qsv, ComputeBackend.Amf],
            (ComputePlatform.Windows, ComputeCapability.VideoDecode) => [ComputeBackend.Cuda, ComputeBackend.D3D11va, ComputeBackend.Qsv],
            (ComputePlatform.Linux, ComputeCapability.VideoDecode) => [ComputeBackend.Cuda, ComputeBackend.Vaapi],
            (ComputePlatform.MacOS, ComputeCapability.OnnxProvider) => [ComputeBackend.CoreML],
            (ComputePlatform.Windows, ComputeCapability.OnnxProvider) => [ComputeBackend.DirectML, ComputeBackend.CudaEp],
            (ComputePlatform.Linux, ComputeCapability.OnnxProvider) => [ComputeBackend.CudaEp, ComputeBackend.RocmEp, ComputeBackend.OpenVino],
            (ComputePlatform.MacOS, ComputeCapability.WhisperBackend or ComputeCapability.LlamaBackend) => [ComputeBackend.Metal],
            (_, ComputeCapability.WhisperBackend) => [ComputeBackend.Vulkan, ComputeBackend.Cuda],
            (_, ComputeCapability.LlamaBackend) => [ComputeBackend.Vulkan],
            _ => []
        };
        return [..gpu, ComputeBackend.Cpu];
    }
}

public sealed class ComputeRoute
{
    private readonly ComputeCapabilities? _capabilities;
    private readonly string _fingerprint;
    private readonly Action<string>? _log;
    private readonly List<ComputeFallback> _failures = [];
    private int _index;
    public ComputeRequest Requested { get; }
    public IReadOnlyList<ComputeBackend> OrderedCandidates { get; }
    public ComputeBackend Chosen => OrderedCandidates[_index];
    internal ComputeRoute(ComputeRequest request, IReadOnlyList<ComputeBackend> candidates, ComputeCapabilities? capabilities, string fingerprint, Action<string>? log)
    {
        Requested = request; OrderedCandidates = candidates; _capabilities = capabilities; _fingerprint = fingerprint; _log = log;
        Log("计算候选：" + string.Join('>', candidates.Select(ComputeRuntimeFormatter.Name)) + "；偏好 " + request.Preference);
    }
    public ComputeOutcome Outcome(string detail = "") => new(Requested, Chosen, _failures.ToArray(), detail);
    public void ReportSuccess(string detail = "") => Log("计算后端：" + ComputeRuntimeFormatter.Name(Chosen) + (detail.Length > 0 ? " " + detail : "")
        + (Chosen == ComputeBackend.Cpu ? "（CPU）" : "（GPU）") + "；候选 " + string.Join('>', OrderedCandidates.Select(ComputeRuntimeFormatter.Name)));
    public void ReportFailure(ComputeBackend backend, string reason, bool persist = true)
    {
        if (backend != Chosen) throw new InvalidOperationException("只能报告当前候选的失败。");
        _failures.Add(new(backend, ComputeRuntimeFormatter.OneLine(reason)));
        if (persist && backend != ComputeBackend.Cpu) _capabilities?.ReportFailure(Requested, backend, _fingerprint, reason);
        if (_index < OrderedCandidates.Count - 1) _index++;
        Log("回退 " + ComputeRuntimeFormatter.Name(Chosen) + "：" + ComputeRuntimeFormatter.Name(backend) + " " + reason);
    }
    private void Log(string message) => ComputeRuntimeFormatter.Log(message, _log);
}
