namespace AvaMedia.Core.Compute;

public sealed class FfmpegEncoderProbe(string executable, IComputeProcessRunner? runner = null) : IComputeProbe
{
    public async Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        var encoder = HardwareTranscoding.Backends.SelectMany(b => b.Encoders).FirstOrDefault(e => e.Codec == request.CodecOrModel);
        var hardware = encoder is null ? null : HardwareTranscoding.Backend(encoder.Codec);
        if (encoder is null || hardware is null) return new(request.Capability, backend, false, "未配置此硬件编码器。", default, fingerprint);
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin", ..hardware.InitializationArguments,
            "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=25", "-frames:v", "3", "-an"];
        if (backend == ComputeBackend.Vaapi) args.AddRange(["-vf", "format=nv12,hwupload"]);
        args.AddRange(["-c:v", encoder.Codec, "-pix_fmt", backend == ComputeBackend.Vaapi ? "vaapi" : "nv12",
            ..hardware.EncodingArguments(encoder, new(23, 1280, 720, 25)), "-f", "null", "-"]);
        var result = await (runner ?? ComputeEnvironment.Processes).RunAsync(executable, args, timeout, ct).ConfigureAwait(false);
        return new(request.Capability, backend, result.ExitCode == 0, result.ExitCode == 0 ? "已成功编码 3 帧。" : result.Error.Trim(),
            default, fingerprint, request.CodecOrModel, result.ExitCode == 0 ? ProbeFailure.None : ProbeFailure.Unavailable);
    }
}

/// <summary>Decode a real compressed sample, independently of encoder availability.</summary>
public sealed class FfmpegDecoderProbe(string executable, IComputeProcessRunner? runner = null,
    Func<string>? temporaryPath = null, Action<string>? delete = null) : IComputeProbe
{
    public async Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        var path = (temporaryPath ?? (() => Path.Combine(Path.GetTempPath(), "avamedia-decode-" + Guid.NewGuid().ToString("N") + ".mkv")))();
        var process = runner ?? ComputeEnvironment.Processes;
        try
        {
            var codec = request.CodecOrModel switch { "hevc" or "h265" => "libx265", "av1" => "libaom-av1", "vp9" => "libvpx-vp9", _ => "libx264" };
            var sample = await process.RunAsync(executable, ["-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=black:s=128x128:r=25",
                "-frames:v", "3", "-an", "-c:v", codec, "-pix_fmt", "yuv420p", path], timeout, ct).ConfigureAwait(false);
            if (sample.ExitCode != 0) return new(request.Capability, backend, false, "无法准备解码探测样片：" + sample.Error, default, fingerprint);
            var test = await process.RunAsync(executable, ["-v", "error", "-nostdin", ..FrameDecodeArguments.InputArguments(backend),
                "-i", path, "-frames:v", "3", "-an", "-f", "null", "-"], timeout, ct).ConfigureAwait(false);
            return new(request.Capability, backend, test.ExitCode == 0, test.ExitCode == 0 ? "已成功硬件解码 3 帧压缩样片。" : test.Error,
                default, fingerprint, request.CodecOrModel, test.ExitCode == 0 ? ProbeFailure.None : ProbeFailure.Unavailable);
        }
        finally { if (delete is not null) delete(path); else if (File.Exists(path)) File.Delete(path); }
    }
}

public static class FfmpegBackend
{
    public static ComputeBackend ForEncoder(string codec) => HardwareTranscoding.Backend(codec)?.Kind switch
    {
        HardwareBackendKind.Nvenc => ComputeBackend.Nvenc, HardwareBackendKind.QuickSync => ComputeBackend.Qsv,
        HardwareBackendKind.Amf => ComputeBackend.Amf, HardwareBackendKind.Vaapi => ComputeBackend.Vaapi,
        HardwareBackendKind.VideoToolbox => ComputeBackend.VideoToolbox, _ => ComputeBackend.Cpu
    };
}
