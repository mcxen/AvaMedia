using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace AvaMedia.Core.Compute;

public sealed class WhisperRuntimeProbe(Func<RuntimeLibrary, bool>? packaged = null, Func<RuntimeLibrary?>? loaded = null) : IComputeProbe
{
    public static bool IsPackaged(RuntimeLibrary library)
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var sub = library switch { RuntimeLibrary.Vulkan => "vulkan", RuntimeLibrary.Cuda => "cuda", RuntimeLibrary.Cuda12 => "cuda12", RuntimeLibrary.CoreML => "coreml", RuntimeLibrary.CpuNoAvx => "noavx", _ => "" };
        var native = platform == "win" ? "whisper.dll" : platform == "macos" ? "libwhisper.dylib" : "libwhisper.so";
        var roots = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(RuntimeOptions).Assembly.Location) ?? AppContext.BaseDirectory,
            Path.GetDirectoryName(RuntimeOptions.LibraryPath) ?? AppContext.BaseDirectory };
        return roots.Any(root => File.Exists(Path.Combine(root, "runtimes", sub, platform + "-" + architecture, native))
            || sub.Length == 0 && File.Exists(Path.Combine(root, native)));
    }
    public Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var current = (loaded ?? (() => RuntimeOptions.LoadedLibrary))();
        var libraries = WhisperCompute.Libraries(backend);
        var available = libraries.Any(packaged ?? IsPackaged);
        var failure = available ? ProbeFailure.None : ProbeFailure.Unavailable;
        var reason = available ? "已找到运行库；实际加载结果将在创建会话后核对。" : "当前架构未打包此 Whisper 运行库。";
        if (current is { } library && !libraries.Contains(library) && backend != ComputeBackend.Metal)
        { available = false; failure = ProbeFailure.Runtime; reason = "进程已固定加载 " + library + " 运行库，需重启后才能切换。"; }
        return Task.FromResult(new CapabilityResult(request.Capability, backend, available, reason, default, fingerprint,
            request.CodecOrModel, failure));
    }
}

public static class WhisperCompute
{
    private static readonly object Gate = new();
    public static IReadOnlyList<RuntimeLibrary> Libraries(ComputeBackend backend) => backend switch
    {
        ComputeBackend.Vulkan => [RuntimeLibrary.Vulkan], ComputeBackend.Cuda => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
        // The bundled macOS Cpu runtime contains ggml-metal; there is no RuntimeLibrary.Metal enum.
        ComputeBackend.Metal => [RuntimeLibrary.Cpu], _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
    };
    public static IReadOnlyList<RuntimeLibrary> RuntimeOrder(ComputePlatform platform, ComputePreference preference)
        => ComputeRouter.Candidates(platform, ComputeCapability.WhisperBackend, preference).SelectMany(Libraries).Distinct().ToArray();
    public static ComputeRoute Prepare(ComputePreference preference)
    {
        var fingerprint = ComputeEnvironment.Fingerprint();
        var request = new ComputeRequest("Whisper", ComputeCapability.WhisperBackend, RuntimeInformation.ProcessArchitecture.ToString(), preference);
        var route = new ComputeRouter(ComputeEnvironment.Platform, ComputeEnvironment.Capabilities, fingerprint).Plan(request);
        var probe = new WhisperRuntimeProbe();
        while (route.Chosen != ComputeBackend.Cpu)
        {
            var result = ComputeEnvironment.Capabilities.GetAsync(request, route.Chosen, fingerprint, probe).GetAwaiter().GetResult();
            if (result.Available) break;
            route.ReportFailure(route.Chosen, result.Reason, persist: false);
        }
        lock (Gate)
        {
            if (RuntimeOptions.LoadedLibrary is null)
                RuntimeOptions.RuntimeLibraryOrder = route.OrderedCandidates.SkipWhile(backend => backend != route.Chosen).SelectMany(Libraries).Distinct().ToList();
        }
        return route;
    }
    public static T WithRuntimeOrder<T>(ComputeRoute route, Func<T> createSession)
    {
        // Native loading is process-wide. Serialize the order + first factory creation together.
        lock (Gate)
        {
            if (RuntimeOptions.LoadedLibrary is null)
                RuntimeOptions.RuntimeLibraryOrder = route.OrderedCandidates.SkipWhile(backend => backend != route.Chosen).SelectMany(Libraries).Distinct().ToList();
            return createSession();
        }
    }
    public static ComputeBackend ActualBackend(RuntimeLibrary? library, bool useGpu, ComputePlatform platform, bool metalObserved = false)
        => !useGpu ? ComputeBackend.Cpu : library switch
        {
            RuntimeLibrary.Vulkan => ComputeBackend.Vulkan, RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => ComputeBackend.Cuda,
            RuntimeLibrary.CoreML => ComputeBackend.CoreML,
            RuntimeLibrary.Cpu when platform == ComputePlatform.MacOS && metalObserved => ComputeBackend.Metal,
            _ => ComputeBackend.Cpu
        };
    public static string? MissingGpuReason(ComputeRoute route, RuntimeLibrary? loaded, bool useGpu, bool metalObserved)
    {
        if (route.Requested.Preference == ComputePreference.ForceCpu) return "设置要求使用 CPU。";
        var actual = ActualBackend(loaded, useGpu, ComputeEnvironment.Platform, metalObserved);
        if (actual == ComputeBackend.Cpu)
            return "Whisper 实际加载 " + (loaded?.ToString() ?? "未知") + " 运行库；Vulkan / CUDA 未加载（运行库未打包、驱动不可用或进程已固定其他库）。"
                + (ComputeEnvironment.Platform == ComputePlatform.MacOS ? "未观察到 Metal 设备使用记录。" : "") + ComputeRuntimeFormatter.Note(route.Outcome());
        return route.Outcome().FallbackChain.Count > 0 ? ComputeRuntimeFormatter.Note(route.Outcome()) : null;
    }
}
