namespace AvaMedia.Core.Compute;

/// <summary>Hardware decode always downloads frames before the existing software filters.</summary>
public static class FrameDecodeArguments
{
    public static IReadOnlyList<string> InputArguments(ComputeBackend backend, int stream = 0, string pixels = "nv12")
    {
        var method = backend switch
        {
            ComputeBackend.Cuda => "cuda", ComputeBackend.Vaapi => "vaapi", ComputeBackend.D3D11va => "d3d11va",
            ComputeBackend.Qsv => "qsv", ComputeBackend.VideoToolbox => "videotoolbox", _ => ""
        };
        if (method.Length == 0) return [];
        List<string> args = ["-hwaccel:v:" + stream, method, "-hwaccel_output_format:v:" + stream, pixels];
        if (backend == ComputeBackend.Vaapi) args.AddRange(["-hwaccel_device:v:" + stream, "/dev/dri/renderD128"]);
        return args;
    }
    public static async Task<ComputeRoute> ForAsync(string executable, MediaInfo? info, ComputePreference preference,
        CancellationToken ct = default, ComputeCapabilities? capabilities = null, IComputeProcessRunner? runner = null, Action<string>? log = null)
    {
        capabilities ??= ComputeEnvironment.Capabilities;
        var fingerprint = ComputeEnvironment.Fingerprint(executable);
        var request = new ComputeRequest("抽帧", ComputeCapability.VideoDecode, info?.VideoCodec ?? "h264", preference);
        var route = new ComputeRouter(ComputeEnvironment.Platform, capabilities, fingerprint).Plan(request, log);
        var probe = new FfmpegDecoderProbe(executable, runner);
        while (route.Chosen != ComputeBackend.Cpu)
        {
            var result = await capabilities.GetAsync(request, route.Chosen, fingerprint, probe, ct).ConfigureAwait(false);
            if (result.Available) break;
            route.ReportFailure(route.Chosen, result.Reason, persist: false);
        }
        return route;
    }
    public static IReadOnlyList<string> Apply(IReadOnlyList<string> args, ComputeBackend backend, int stream = 0, string pixels = "nv12")
    {
        var list = args.ToList(); var index = list.IndexOf("-i");
        if (index < 0) throw new ArgumentException("抽帧参数缺少输入。");
        list.InsertRange(index, InputArguments(backend, stream, pixels)); return list;
    }
    /// <summary>Discard the entire failed attempt before retrying; callers never observe partial GPU output.</summary>
    public static async Task<ProcessResult> ExecuteAsync(string executable, IReadOnlyList<string> args, Stream output,
        ComputeRoute route, CancellationToken ct, IComputeProcessRunner? runner = null, int stream = 0, string pixels = "nv12", int frameBytes = 0, int minimumFrames = 1)
    {
        runner ??= ComputeEnvironment.Processes;
        var backend = route.Chosen;
        ProcessResult result;
        try { result = await runner.RunToStreamAsync(executable, Apply(args, backend, stream, pixels), output, ct).ConfigureAwait(false); }
        catch (Exception e) when (backend != ComputeBackend.Cpu && !ct.IsCancellationRequested && e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { result = new(-1, "", e.Message); }
        if (result.ExitCode == 0 && (output.Length == 0 || frameBytes > 0 && (output.Length % frameBytes != 0 || output.Length < (long)frameBytes * minimumFrames)))
            result = new(-1, "", "硬件解码输出为空或帧数据不完整。");
        if (result.ExitCode != 0 && backend != ComputeBackend.Cpu)
        {
            route.ReportFailure(backend, "硬件解码失败，整段使用软件重跑：" + result.Error);
            // Software retry is deliberate: filters may be incompatible with all hardware download paths.
            while (route.Chosen != ComputeBackend.Cpu) route.ReportFailure(route.Chosen, "本段选择软件解码重跑。", persist: false);
            output.Position = 0; output.SetLength(0);
            result = await runner.RunToStreamAsync(executable, args, output, ct).ConfigureAwait(false);
        }
        if (result.ExitCode == 0) route.ReportSuccess();
        return result;
    }
    public static Stream CreateSpool() => new FileStream(Path.Combine(Path.GetTempPath(), "avamedia-frames-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    public static async Task<ProcessResult> RunAsync(IMediaEngine engine, MediaInfo? info, IReadOnlyList<string> args, Stream output, CancellationToken ct, int frameBytes = 0, int minimumFrames = 1)
    {
        var preference = ComputeSettings.Load(engine.Settings.AutoDetectGpu).Preference;
        var route = await ForAsync(engine.FFmpeg, info, preference, ct).ConfigureAwait(false);
        var pixels = info is null ? "nv12" : HardwareTranscodingBackend.TransferFormat(info);
        return await ExecuteAsync(engine.FFmpeg, args, output, route, ct, stream: info?.VideoStreamIndex ?? 0, pixels: pixels, frameBytes: frameBytes, minimumFrames: minimumFrames).ConfigureAwait(false);
    }
}
