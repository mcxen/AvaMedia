using System.Text.RegularExpressions;

namespace AvaMedia.Core.Compute;

public sealed class LlamaProbe(string executable, IComputeProcessRunner? runner = null) : IComputeProbe
{
    public static bool HasGpuDevice(string output) => Regex.IsMatch(output,
        @"(?im)^\s*(?:Vulkan\d*|Metal\d*|CUDA\d*|ROCm\d*|SYCL\d*)\s*:");
    public async Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        var result = await (runner ?? ComputeEnvironment.Processes).RunAsync(executable, ["--list-devices"], timeout, ct).ConfigureAwait(false);
        var devices = result.Output + "\n" + result.Error;
        var available = result.ExitCode == 0 && HasGpuDevice(devices);
        return new(request.Capability, backend, available, available ? "llama.cpp 已枚举 GPU 设备。" : result.ExitCode == 0
            ? "llama.cpp 未发现 Vulkan / Metal GPU 设备。" : "llama.cpp 设备探测失败：" + result.Error,
            default, fingerprint, request.CodecOrModel, available ? ProbeFailure.None : ProbeFailure.Unavailable);
    }
}
