using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core.Compute;

public enum ComputeCapability { VideoEncode, VideoDecode, OnnxProvider, WhisperBackend, LlamaBackend, FrameFilter }
public enum ComputeBackend { Cpu, Nvenc, Cuda, Qsv, Vaapi, Amf, D3D11va, VideoToolbox, DirectML, CoreML, CudaEp, RocmEp, OpenVino, Vulkan, Metal }
public enum ComputePreference { Auto, PreferGpu, ForceCpu }
public enum ComputePlatform { Windows, Linux, MacOS }
public enum ProbeFailure { None, Unavailable, Timeout, Runtime }
public sealed record ComputeRequest(string Tool, ComputeCapability Capability, string CodecOrModel = "", ComputePreference Preference = ComputePreference.Auto);
public sealed record CapabilityResult(ComputeCapability Capability, ComputeBackend Backend, bool Available, string Reason,
    DateTimeOffset ProbedAtUtc, string Fingerprint, string Variant = "", ProbeFailure Failure = ProbeFailure.None);
public sealed record ComputeFallback(ComputeBackend Backend, string Reason);
public sealed record ComputeOutcome(ComputeRequest Requested, ComputeBackend ActualBackend, IReadOnlyList<ComputeFallback> FallbackChain, string Detail = "");

public static class ComputeRuntimeFormatter
{
    public static string Name(ComputeBackend backend) => backend switch
    {
        ComputeBackend.Cpu => "CPU", ComputeBackend.Nvenc => "NVENC", ComputeBackend.Cuda or ComputeBackend.CudaEp => "CUDA",
        ComputeBackend.Qsv => "QSV", ComputeBackend.Vaapi => "VAAPI", ComputeBackend.Amf => "AMF", ComputeBackend.CoreML => "Core ML",
        ComputeBackend.RocmEp => "ROCm", ComputeBackend.OpenVino => "OpenVINO", _ => backend.ToString()
    };
    public static string Backend(ComputeOutcome result) => (result.ActualBackend == ComputeBackend.Cpu ? "CPU" : "GPU · " + Name(result.ActualBackend))
        + (result.Detail.Length == 0 ? "" : "（" + result.Detail + "）");
    public static string Note(ComputeOutcome result) => string.Join("；", result.FallbackChain.Select(f => Name(f.Backend) + "：" + OneLine(f.Reason)));
    public static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    public static void Log(string message, Action<string>? log = null)
    { var line = OneLine(message); Trace.TraceInformation(line); log?.Invoke(line); }
}

internal static class ComputeJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}
