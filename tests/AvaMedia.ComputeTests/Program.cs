using System.Text;
using System.Text.Json;
using AvaMedia.Core;
using AvaMedia.Core.Compute;
using Whisper.net.LibraryLoader;

// Deterministic backend tests. No FFmpeg, model inference, GPU, media regression or UI is run.
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
void Near(double actual, double expected, string name) => Check(Math.Abs(actual - expected) < .00001, name);
var clock = new FakeClock();
var files = new FakeFiles();
var caps = new ComputeCapabilities(files, clock, "capabilities");
ComputeEnvironment.Capabilities = caps;
var request = new ComputeRequest("转码", ComputeCapability.VideoEncode, "h264_nvenc");

foreach (var preference in Enum.GetValues<ComputePreference>())
{
    var linux = ComputeRouter.Candidates(ComputePlatform.Linux, ComputeCapability.VideoEncode, preference);
    Check(preference == ComputePreference.ForceCpu ? linux.SequenceEqual([ComputeBackend.Cpu])
        : linux.SequenceEqual([ComputeBackend.Nvenc, ComputeBackend.Vaapi, ComputeBackend.Qsv, ComputeBackend.Amf, ComputeBackend.Cpu]), "Linux 编码顺序 " + preference);
    foreach (var platform in Enum.GetValues<ComputePlatform>())
        foreach (var capability in Enum.GetValues<ComputeCapability>())
        {
            var order = ComputeRouter.Candidates(platform, capability, preference);
            Check(order[^1] == ComputeBackend.Cpu && (preference != ComputePreference.ForceCpu || order.Count == 1), "CPU 兜底 " + platform + "/" + capability + "/" + preference);
        }
}
Check(ComputeRouter.Candidates(ComputePlatform.Windows, ComputeCapability.VideoEncode).SequenceEqual([ComputeBackend.Nvenc, ComputeBackend.Qsv, ComputeBackend.Amf, ComputeBackend.Cpu]), "Windows 编码顺序");
Check(ComputeRouter.Candidates(ComputePlatform.MacOS, ComputeCapability.VideoEncode).SequenceEqual([ComputeBackend.VideoToolbox, ComputeBackend.Cpu]), "macOS 编码顺序");
Check(ComputeRouter.Candidates(ComputePlatform.Windows, ComputeCapability.VideoDecode).SequenceEqual([ComputeBackend.Cuda, ComputeBackend.D3D11va, ComputeBackend.Qsv, ComputeBackend.Cpu]), "Windows 解码顺序");
Check(ComputeRouter.Candidates(ComputePlatform.Linux, ComputeCapability.VideoDecode).SequenceEqual([ComputeBackend.Cuda, ComputeBackend.Vaapi, ComputeBackend.Cpu]), "Linux 解码顺序");
var messages = new List<string>();
var route = new ComputeRouter(ComputePlatform.Linux, caps, "machine-a").Plan(request, messages.Add);
route.ReportFailure(ComputeBackend.Nvenc, "驱动初始化失败\n详情");
Check(route.Chosen == ComputeBackend.Vaapi, "ReportFailure 选择下个后端");
route.ReportSuccess("h264_vaapi");
Check(route.Outcome().ActualBackend == ComputeBackend.Vaapi && route.Outcome().FallbackChain.Single().Reason == "驱动初始化失败 详情", "实际后端与回退链");
Check(messages.Any(m => m.StartsWith("计算后端：VAAPI")) && messages.All(m => !m.Contains('\n')), "中文单行决策日志");
Check(ComputeRuntimeFormatter.Backend(route.Outcome("h264_vaapi")).Contains("GPU") && ComputeRuntimeFormatter.Note(route.Outcome()).Contains("初始化"), "运行时格式化");

var probe = new FakeProbe(clock);
await caps.GetAsync(request, ComputeBackend.Nvenc, "machine-a", probe);
Check(probe.Timeouts.Count == 0, "运行期失败在当前进程阻止重复初始化");
clock.Advance(TimeSpan.FromHours(1));
await caps.GetAsync(request, ComputeBackend.Nvenc, "machine-a", probe);
Check(probe.Timeouts.Count == 0, "运行期失败保持本进程禁用");
var restarted = new ComputeCapabilities(files, clock, "capabilities");
Check((await restarted.GetAsync(request, ComputeBackend.Nvenc, "machine-a", probe)).Available, "新进程在短缓存过期后重试");
Check(probe.Timeouts.Count == 1, "运行期失败不会永久写死");
await restarted.GetAsync(request, ComputeBackend.Nvenc, "machine-a", probe);
Check(probe.Timeouts.Count == 1, "成功缓存复用");
var roundtrip = new ComputeCapabilities(files, clock, "capabilities");
await roundtrip.GetAsync(request, ComputeBackend.Nvenc, "machine-a", probe);
Check(probe.Timeouts.Count == 1, "能力 JSON 往返");
await roundtrip.GetAsync(request, ComputeBackend.Nvenc, "machine-b", probe);
Check(probe.Timeouts.Count == 2, "机器指纹失效");
clock.Advance(TimeSpan.FromDays(8));
await roundtrip.GetAsync(request, ComputeBackend.Nvenc, "machine-b", probe);
Check(probe.Timeouts.Count == 3, "成功缓存 7 天后过期");
await roundtrip.GetAsync(request, ComputeBackend.Nvenc, "machine-b", probe, refresh: true);
Check(probe.Timeouts.Count == 4, "显式重新探测");
var timeoutProbe = new FakeProbe(clock) { Fail = ProbeFailure.Timeout };
var timeoutCaps = new ComputeCapabilities(files, clock, "timeouts");
var failed = await timeoutCaps.GetAsync(request, ComputeBackend.Nvenc, "a", timeoutProbe);
Check(!failed.Available && timeoutProbe.Timeouts.SequenceEqual([TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(20)]), "超时以更长超时重测");
clock.Advance(TimeSpan.FromMinutes(59));
await timeoutCaps.GetAsync(request, ComputeBackend.Nvenc, "a", timeoutProbe);
Check(timeoutProbe.Timeouts.Count == 2, "超时失败一小时内缓存");
clock.Advance(TimeSpan.FromMinutes(2)); timeoutProbe.Fail = ProbeFailure.None;
Check((await timeoutCaps.GetAsync(request, ComputeBackend.Nvenc, "a", timeoutProbe)).Available, "超时缓存过期后恢复");
var unavailableProbe = new FakeProbe(clock) { Fail = ProbeFailure.Unavailable };
var unavailableCaps = new ComputeCapabilities(files, clock, "unavailable");
await unavailableCaps.GetAsync(request, ComputeBackend.Nvenc, "a", unavailableProbe);
clock.Advance(TimeSpan.FromHours(23));
await unavailableCaps.GetAsync(request, ComputeBackend.Nvenc, "a", unavailableProbe);
Check(unavailableProbe.Timeouts.Count == 1, "不可用失败短于成功有效期");
clock.Advance(TimeSpan.FromHours(2));
await unavailableCaps.GetAsync(request, ComputeBackend.Nvenc, "a", unavailableProbe);
Check(unavailableProbe.Timeouts.Count == 2, "不可用失败一天后重试");
roundtrip.Invalidate();
await roundtrip.GetAsync(request, ComputeBackend.Nvenc, "machine-b", probe);
Check(probe.Timeouts.Count == 5, "清空缓存重新探测");
foreach (var damaged in new[] { "{oops", "null", "[null]", "{}", "[{\"Backend\":\"Bogus\"}]" })
{
    files.Write("damaged", damaged);
    var damagedCaps = new ComputeCapabilities(files, clock, "damaged");
    Check((await damagedCaps.GetAsync(request, ComputeBackend.Nvenc, "a", probe)).Available, "缓存损坏容错 " + damaged);
}
files.Write("ffmpeg", "binary"); var fingerprint = ComputeEnvironment.Fingerprint("ffmpeg", files, "driver1");
Check(fingerprint == ComputeEnvironment.Fingerprint("ffmpeg", files, "driver1") && fingerprint != ComputeEnvironment.Fingerprint("ffmpeg", files, "driver2"), "驱动指纹改变使缓存失效");
files.Stamp = files.Stamp.AddDays(1);
Check(fingerprint != ComputeEnvironment.Fingerprint("ffmpeg", files, "driver1"), "FFmpeg mtime 使指纹失效");

Check(ComputeSettings.Load(false, files, "settings").Preference == ComputePreference.ForceCpu, "AutoDetectGpu=false 迁移");
Check(ComputeSettings.Load(true, files, "settings").Preference == ComputePreference.Auto, "默认 Auto");
var settings = new ComputeSettings { Preference = ComputePreference.PreferGpu }; settings.Save(files, "settings");
Check(ComputeSettings.Load(false, files, "settings").Preference == ComputePreference.PreferGpu, "独立设置显式值优先与 JSON 往返");
var copied = settings.Clone(); settings.Preference = ComputePreference.ForceCpu;
Check(copied.Preference == ComputePreference.PreferGpu, "设置 Clone 独立"); copied.CopyFrom(settings);
Check(copied.Preference == ComputePreference.ForceCpu, "设置 CopyFrom");
files.Write("settings", "bad");
Check(ComputeSettings.Load(false, files, "settings").Preference == ComputePreference.ForceCpu, "设置损坏保留迁移偏好");
files.Write("settings", "{\"Preference\":999}");
Check(ComputeSettings.Load(false, files, "settings").Preference == ComputePreference.ForceCpu, "设置非法枚举容错");

Check(FfmpegBackend.SoftwareEncoder("h264") == "libx264" && FfmpegBackend.SoftwareEncoder("hevc_vaapi") == "libx265"
    && FfmpegBackend.SoftwareEncoder("av1_nvenc") == "libaom-av1" && FfmpegBackend.SoftwareEncoder("vp9_qsv") == "libvpx-vp9"
    && FfmpegBackend.SoftwareEncoder("libx264") == "libx264", "ForceCpu 映射泛型/显式硬件编码器且保留显式软件编码器");
var fakeRunner = new FakeRunner();
foreach (var codec in new[] { "h264_vaapi", "hevc_vaapi", "av1_vaapi" })
{
    var test = await new FfmpegEncoderProbe("fake-ffmpeg", fakeRunner).ProbeAsync(new("转码", ComputeCapability.VideoEncode, codec), ComputeBackend.Vaapi, "a", TimeSpan.FromSeconds(6), default);
    var arguments = fakeRunner.Calls[^1];
    Check(test.Available && arguments.Contains("vaapi=avamedia_va:/dev/dri/renderD128") && arguments.Contains("format=nv12,hwupload") && arguments.Contains(codec) && arguments.Contains("-qp"), "VAAPI 编码探测参数 " + codec);
}
var deleted = false;
await new FfmpegDecoderProbe("fake-ffmpeg", fakeRunner, () => "fake-sample", _ => deleted = true)
    .ProbeAsync(new("抽帧", ComputeCapability.VideoDecode, "hevc"), ComputeBackend.Vaapi, "a", TimeSpan.FromSeconds(6), default);
Check(deleted && fakeRunner.Calls[^2].Contains("libx265") && fakeRunner.Calls[^1].Contains("vaapi") && fakeRunner.Calls[^1].Contains("fake-sample"), "独立解码探测使用压缩样片并清理");
var frameArgs = FrameDecodeArguments.Apply(["-ss", "1", "-i", "video", "-vf", "scale=64:64", "pipe:1"], ComputeBackend.Cuda, 2, "p010le").ToList();
Check(frameArgs.IndexOf("-hwaccel:v:2") < frameArgs.IndexOf("-i") && frameArgs.Contains("cuda") && frameArgs.Contains("p010le"), "GPU 抽帧参数在输入前并保留流与位深");
Check(FrameDecodeArguments.InputArguments(ComputeBackend.Vaapi).Contains("/dev/dri/renderD128") && FrameDecodeArguments.InputArguments(ComputeBackend.Cpu).Count == 0, "VAAPI 设备与 CPU 参数");
var raw = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"pix_fmt\":\"yuv420p\",\"width\":1920,\"height\":1080,\"r_frame_rate\":\"30/1\"}]}";
var info = new MediaInfo(60, 1920, 1080, false, true, raw, "h264");
var vaapiJob = new Job { FeatureId = "mp4", Inputs = ["fake-video"], Output = "fake-output", Duration = 60,
    Options = new() { Format = "mp4", VideoCodec = "h264_vaapi", VideoRateMode = VideoRateMode.Quality, Mute = true } };
var encoding = MediaEngine.BuildArguments(vaapiJob, [info]);
Check(encoding.Contains("format=nv12,hwupload") && encoding.Contains("vaapi") && encoding.Contains("-qp"), "转码 VAAPI 上传与编码控制");
vaapiJob.Options.VideoCompression = new() { Mode = VideoCompressionMode.Quality };
encoding = MediaEngine.BuildArguments(vaapiJob, [info]);
Check(encoding.Contains("-qp"), "压缩 VAAPI 质量模式");
var rerunRoute = new ComputeRouter(ComputePlatform.Linux).Plan(new("抽帧", ComputeCapability.VideoDecode));
var rerun = new FakeRunner { FailFirstStream = true };
using var buffer = new MemoryStream();
var rerunResult = await FrameDecodeArguments.ExecuteAsync("fake", ["-i", "source", "pipe:1"], buffer, rerunRoute, default, rerun);
Check(rerunResult.ExitCode == 0 && rerun.StreamCalls.Count == 2 && rerun.StreamCalls[0].Contains("cuda") && !rerun.StreamCalls[1].Contains("cuda"), "GPU 抽帧失败整段软件重跑");
Check(Encoding.UTF8.GetString(buffer.ToArray()) == "complete-cpu" && rerunRoute.Chosen == ComputeBackend.Cpu, "失败输出被丢弃且实际后端为 CPU");
foreach (var incomplete in new[] { "", "x" })
{
    using var incompleteBuffer = new MemoryStream();
    var incompleteRunner = new FakeRunner { FirstStreamData = incomplete };
    var incompleteRoute = new ComputeRouter(ComputePlatform.Linux).Plan(new("抽帧", ComputeCapability.VideoDecode));
    var recovered = await FrameDecodeArguments.ExecuteAsync("fake", ["-i", "source"], incompleteBuffer, incompleteRoute, default, incompleteRunner, frameBytes: 4);
    Check(recovered.ExitCode == 0 && incompleteRunner.StreamCalls.Count == 2 && Encoding.UTF8.GetString(incompleteBuffer.ToArray()) == "complete-cpu", "零退出码但空/不完整 GPU 帧仍整段重跑 " + incomplete.Length);
}
using var canceled = new CancellationTokenSource(); canceled.Cancel();
var canceledRunner = new FakeRunner();
try { await FrameDecodeArguments.ExecuteAsync("fake", ["-i", "source"], new MemoryStream(), new ComputeRouter(ComputePlatform.Linux).Plan(new("抽帧", ComputeCapability.VideoDecode)), canceled.Token, canceledRunner); Check(false, "取消必须抛出"); }
catch (OperationCanceledException) { Check(canceledRunner.StreamCalls.Count == 0, "用户取消不重跑"); }

var attempts = new List<ComputeBackend>();
var selection = OnnxProviderSelector.Select(ComputePlatform.Linux, ComputePreference.PreferGpu,
    ["CPUExecutionProvider", "CUDAExecutionProvider", "ROCMExecutionProvider", "OpenVINOExecutionProvider"], backend =>
    { attempts.Add(backend); if (backend is ComputeBackend.CudaEp or ComputeBackend.RocmEp) throw new InvalidOperationException("EP 初始化失败"); return backend; });
Check(attempts.SequenceEqual([ComputeBackend.CudaEp, ComputeBackend.RocmEp, ComputeBackend.OpenVino]) && selection.Backend == "OpenVINO / CPU", "ONNX Linux 顺序与逐个失败切换");
Check(selection.FallbackReason!.Contains("CUDA") && selection.FallbackReason.Contains("ROCm"), "ONNX 保留所有失败原因");
attempts.Clear();
var linuxCpu = OnnxProviderSelector.Select(ComputePlatform.Linux, ComputePreference.Auto, ["CPUExecutionProvider"], backend => { attempts.Add(backend); return backend; });
Check(attempts.SequenceEqual([ComputeBackend.Cpu]) && linuxCpu.FallbackReason!.Contains("当前打包的 ONNX Runtime 为 CPU 版本"), "Linux CPU 包原因明确且不尝试不存在的 EP");
var forceOnnx = OnnxProviderSelector.Select(ComputePlatform.Windows, ComputePreference.ForceCpu, ["DmlExecutionProvider", "CPUExecutionProvider"], backend => backend);
Check(forceOnnx.Value == ComputeBackend.Cpu && forceOnnx.Outcome.FallbackChain.Count == 0, "ONNX ForceCpu");
var windowsOnnx = OnnxProviderSelector.Select(ComputePlatform.Windows, ComputePreference.Auto, ["DmlExecutionProvider", "CUDAExecutionProvider"], backend => backend);
Check(windowsOnnx.Value == ComputeBackend.DirectML, "DirectML 优先");
Check(OnnxProviderSelector.Select(ComputePlatform.MacOS, ComputePreference.Auto, ["CoreMLExecutionProvider"], backend => backend).Value == ComputeBackend.CoreML, "macOS CoreML 优先");
var validations = new List<ComputeBackend>();
var onnxProbe = new OnnxProviderProbe(["CUDAExecutionProvider"], validations.Add);
Check((await onnxProbe.ProbeAsync(new("ONNX", ComputeCapability.OnnxProvider), ComputeBackend.CudaEp, "a", TimeSpan.FromSeconds(6), default)).Available && validations.SequenceEqual([ComputeBackend.CudaEp]), "ONNX 探测验证器可注入");

Check(WhisperCompute.RuntimeOrder(ComputePlatform.Linux, ComputePreference.Auto).SequenceEqual([RuntimeLibrary.Vulkan, RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]), "Whisper Vulkan CUDA CPU 顺序");
Check(WhisperCompute.RuntimeOrder(ComputePlatform.Windows, ComputePreference.ForceCpu).SequenceEqual([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]), "Whisper ForceCpu 运行库顺序");
var cpuWhisperRoute = new ComputeRouter(ComputePlatform.Linux).Plan(new("Whisper", ComputeCapability.WhisperBackend, Preference: ComputePreference.ForceCpu));
var configuredWhisperOrder = WhisperCompute.WithRuntimeOrder(cpuWhisperRoute, () => RuntimeOptions.RuntimeLibraryOrder.ToArray());
Check(configuredWhisperOrder.SequenceEqual([RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]), "Whisper 首次会话创建前写入 ForceCpu 顺序（fake factory）");
Check(WhisperCompute.RuntimeOrder(ComputePlatform.MacOS, ComputePreference.Auto).First() == RuntimeLibrary.Cpu, "macOS Metal 位于默认运行库");
Check(WhisperCompute.ActualBackend(RuntimeLibrary.Cpu, true, ComputePlatform.Linux) == ComputeBackend.Cpu && WhisperCompute.ActualBackend(RuntimeLibrary.Vulkan, false, ComputePlatform.Windows) == ComputeBackend.Cpu, "Whisper CPU 静默回退与 ForceCpu 标签");
Check(WhisperCompute.ActualBackend(RuntimeLibrary.Vulkan, true, ComputePlatform.Linux) == ComputeBackend.Vulkan && WhisperCompute.ActualBackend(RuntimeLibrary.Cpu, true, ComputePlatform.MacOS, true) == ComputeBackend.Metal, "Whisper 实际库与 Metal 日志确认");
var missingWhisper = new WhisperRuntimeProbe(_ => false, () => null);
var whisperRequest = new ComputeRequest("Whisper", ComputeCapability.WhisperBackend, "Arm64");
Check(!(await missingWhisper.ProbeAsync(whisperRequest, ComputeBackend.Vulkan, "a", TimeSpan.FromSeconds(6), default)).Available, "Whisper arm64 未打包库回退");
var fixedWhisper = new WhisperRuntimeProbe(_ => true, () => RuntimeLibrary.Cpu);
Check((await fixedWhisper.ProbeAsync(whisperRequest, ComputeBackend.Vulkan, "a", TimeSpan.FromSeconds(6), default)).Reason.Contains("重启"), "Whisper 已加载库不能热切换");
Check(LlamaProbe.HasGpuDevice("Available devices:\n  Vulkan0: AMD Radeon (8192 MiB)") && LlamaProbe.HasGpuDevice("Metal: Apple M4") && !LlamaProbe.HasGpuDevice("ggml_vulkan: Found 0 Vulkan devices\nAvailable devices:\n"), "llama 设备列表解析不误判空设备");
var noDeviceRunner = new FakeRunner { RunOutput = "Available devices:\n" };
Check((await new LlamaProbe("fake", noDeviceRunner).ProbeAsync(new("总结", ComputeCapability.LlamaBackend), ComputeBackend.Vulkan, "a", TimeSpan.FromSeconds(6), default)).Reason.Contains("未发现"), "llama 无 GPU 原因明确");

var throughput = new ThroughputModel(files, clock, "throughput");
var gpuKey = new ThroughputKey("转码", ComputeBackend.Nvenc, "1080p", "h264");
var cpuKey = gpuKey with { Backend = ComputeBackend.Cpu };
Near(throughput.Rate(gpuKey), 4, "GPU 保守先验"); Near(throughput.Rate(cpuKey), .8, "CPU 保守先验");
throughput.Observe(gpuKey, 100, TimeSpan.FromSeconds(10)); Near(throughput.Rate(gpuKey), 5.8, "吞吐首个 EWMA 样本");
throughput.Observe(gpuKey, 100, TimeSpan.FromSeconds(10)); Near(throughput.Rate(gpuKey), 7.06, "吞吐后续 EWMA");
Near(new ThroughputModel(files, clock, "throughput").Rate(gpuKey), 7.06, "吞吐持久化往返");
throughput.Observe(gpuKey, double.NaN, TimeSpan.FromSeconds(1)); throughput.Observe(gpuKey, 100, TimeSpan.Zero);
Near(throughput.Rate(gpuKey), 7.06, "吞吐忽略无效实测"); Near(throughput.Rate(cpuKey), .8, "CPU 和 GPU 模型分离");
Check(ThroughputModel.ResolutionBand(1920, 1080) == "1080p" && ThroughputModel.ResolutionBand(3840, 2160) == "2160p", "分辨率档");
Check(ThroughputModel.Prior(gpuKey with { Codec = "av1" }) < ThroughputModel.Prior(gpuKey), "AV1 先验更保守");
files.Write("damaged-throughput", "oops"); Near(new ThroughputModel(files, clock, "damaged-throughput").Rate(cpuKey), .8, "吞吐损坏容错");
var etaModel = new ThroughputModel(files, clock, "eta");
var eta = new PriorAwareProgressEstimator(etaModel, gpuKey, 100);
Near(eta.Update(0, TimeSpan.Zero).Remaining!.Value.TotalSeconds, 25, "开始即有先验 ETA");
var mixed = eta.Update(20, TimeSpan.FromSeconds(2));
Check(mixed.Remaining!.Value.TotalSeconds < 20 && mixed.Remaining.Value.TotalSeconds > 8, "开始阶段混合先验与实测");
eta.SwitchBackend(cpuKey); Near(eta.Update(20, TimeSpan.FromSeconds(2)).Remaining!.Value.TotalSeconds, 100, "回退后切换 CPU 模型并清除旧速度");
eta.Update(30, TimeSpan.FromSeconds(7));
Check(eta.Update(30, TimeSpan.FromSeconds(13)).State == ProgressEstimateState.Stalled, "ETA 停滞");
Check(eta.Update(100, TimeSpan.FromSeconds(14)).State == ProgressEstimateState.Finalizing, "ETA 收尾");
var restartedEta = eta.Update(0, TimeSpan.Zero);
Check(restartedEta.State == ProgressEstimateState.Available, "ETA 时间与进度回退重置");
Check(new PriorAwareProgressEstimator(etaModel, gpuKey, 0).Update(0, TimeSpan.Zero).State == ProgressEstimateState.Estimating, "未知工作量不伪造 ETA");
var outcome = route.Outcome("h264_vaapi");
var marker = JobComputeStore.SerializeMarker(outcome);
var restored = JobComputeStore.ParseLog("ordinary log\n" + marker + "\nGPU · VAAPI");
Check(restored?.ActualBackend == ComputeBackend.Vaapi && restored.Requested == outcome.Requested && restored.FallbackChain.SequenceEqual(outcome.FallbackChain), "ComputeOutcome 日志 JSON 往返");
Check(JobComputeStore.ParseLog("[compute-outcome] invalid") is null, "ComputeOutcome 损坏容错");
var activeJob = new Job { State = JobState.Running };
var elapsed = TimeSpan.Zero;
using (var scope = new ComputeProgressScope(activeJob, new ThroughputModel(files, clock, "scope"), gpuKey, 100, () => elapsed))
{
    Near(activeJob.Estimate!.Remaining!.Value.TotalSeconds, 25, "任务已有 Estimate 接入新先验");
    activeJob.Estimate = new(ProgressEstimateState.Estimating);
    Check(activeJob.Estimate.State == ProgressEstimateState.Available, "队列计时器不会覆盖计算 ETA");
    elapsed = TimeSpan.FromSeconds(2); scope.Update(20);
    scope.SwitchBackend(cpuKey);
    Near(activeJob.Estimate!.Remaining!.Value.TotalSeconds, 100, "任务作用域回退后切换先验");
}
activeJob.Estimate = null;
Check(activeJob.Estimate is null, "编码作用域结束释放 ETA 所有权");
var published = new List<ComputeOutcome>();
using (ComputeJobContext.Enter(published.Add))
{
    await Task.Yield(); route.ReportSuccess("h264_vaapi");
    Check(published.Count == 1 && published[0].ActualBackend == ComputeBackend.Vaapi, "任务上下文跨 async 传播计算结果");
    var nested = new List<ComputeOutcome>();
    using (ComputeJobContext.Enter(nested.Add)) { ComputeJobContext.Report(outcome); }
    ComputeJobContext.Report(outcome);
    Check(nested.Count == 1 && published.Count == 2, "嵌套任务上下文恢复");
}
ComputeJobContext.Report(outcome);
Check(published.Count == 2, "任务上下文退出后不污染其他任务");
Console.WriteLine($"Compute tests passed: {checks} checks. No GPU or media processes executed.");

sealed class FakeClock : IComputeClock
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-10-15T00:00:00Z");
    public void Advance(TimeSpan duration) => UtcNow += duration;
}
sealed class FakeFiles : IComputeFiles
{
    public readonly Dictionary<string, string> Content = [];
    public DateTimeOffset Stamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    public string? Read(string path) => Content.GetValueOrDefault(path);
    public void Write(string path, string content) => Content[path] = content;
    public DateTimeOffset LastWrite(string path) => Stamp;
}
sealed class FakeProbe(FakeClock clock) : IComputeProbe
{
    public readonly List<TimeSpan> Timeouts = [];
    public ProbeFailure Fail;
    public Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Timeouts.Add(timeout);
        if (Fail == ProbeFailure.Timeout) throw new TimeoutException("fake cold start");
        return Task.FromResult(new CapabilityResult(request.Capability, backend, Fail == ProbeFailure.None, "fake result", clock.UtcNow, fingerprint, request.CodecOrModel, Fail));
    }
}
sealed class FakeRunner : IComputeProcessRunner
{
    public readonly List<IReadOnlyList<string>> Calls = [], StreamCalls = [];
    public bool FailFirstStream;
    public string? FirstStreamData;
    public string RunOutput = "";
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Calls.Add(arguments.ToArray()); return Task.FromResult(new ProcessResult(0, RunOutput, "")); }
    public async Task<ProcessResult> RunToStreamAsync(string executable, IReadOnlyList<string> arguments, Stream output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); StreamCalls.Add(arguments.ToArray());
        var fail = FailFirstStream && StreamCalls.Count == 1;
        await output.WriteAsync(Encoding.UTF8.GetBytes(StreamCalls.Count == 1 && FirstStreamData is not null ? FirstStreamData : fail ? "partial-gpu" : "complete-cpu"), ct);
        return new(fail ? 1 : 0, "", fail ? "fake decoder failed" : "");
    }
}
