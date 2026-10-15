using AvaMedia.Core.Compute;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

/// <summary>Authenticated loopback llama.cpp process; task leases and idle retention are managed by the cache.</summary>
public sealed class LocalSummaryModel : ISummaryToolModel
{
    private readonly OwnedModelProcess _owner;
    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly ModelLease _model, _runtime;
    private readonly Task _stdout, _stderr;
    private readonly string _id;
    private readonly ModelStore _store;
    private readonly bool _gpu;
    private Action<string>? _status;
    private LocalSummaryModel? _cpuFallback;
    private bool _retriedGpu;
    private int _released;
    private string _backend = "CPU";
    private ComputeOutcome? _computeOutcome;
    private bool _visualWarmed;
    public string ModelId => _id;
    public string? FallbackReason { get; private set; }
    public string Backend => _cpuFallback?.Backend ?? _backend;
    internal bool IsAlive => _cpuFallback?.IsAlive ?? (Volatile.Read(ref _released) == 0 && _owner.IsAlive);
    internal bool VisualWarmed => _visualWarmed;
    internal void SetStatus(Action<string>? status) { _status = status; _cpuFallback?.SetStatus(status); }
    private LocalSummaryModel(OwnedModelProcess owner, HttpClient client, ModelLease model, ModelLease runtime, string id,
        ModelStore store, bool gpu, Action<string>? status)
    {
        _owner = owner; _process = owner.Process; _client = client; _model = model; _runtime = runtime; _id = id;
        _store = store; _gpu = gpu; _status = status;
        _stdout = DrainAsync(_process.StandardOutput); _stderr = DrainAsync(_process.StandardError);
    }

    public static void Shutdown() => OwnedModelProcess.Shutdown();

    public static async Task<LocalSummaryModel> StartAsync(ModelStore store, string id, CancellationToken ct,
        Action<string>? status = null, Action<ModelPreparationProgress>? preparation = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, OwnedModelProcess.Lifetime);
        ct = lifetime.Token;
        var preference = ComputeSettings.CurrentPreference;
        var preferGpu = preference != ComputePreference.ForceCpu;
        try
        {
            try { return await StartCoreAsync(store, id, preferGpu, ct, status, preparation).ConfigureAwait(false); }
            catch (LocalGpuStartupException error) when (preferGpu && !ct.IsCancellationRequested)
            {
                ComputeRuntimeFormatter.Log("回退 CPU：llama.cpp GPU 启动失败：" + error.Message, status);
                var cpu = await StartCoreAsync(store, id, false, ct, status, preparation).ConfigureAwait(false);
                cpu.FallbackReason = error.Message;
                if (cpu._computeOutcome is { } outcome)
                    cpu._computeOutcome = outcome with { Requested = outcome.Requested with { Preference = preference }, FallbackChain = [new(OperatingSystem.IsMacOS() ? ComputeBackend.Metal : ComputeBackend.Vulkan, error.Message)] };
                if (cpu._computeOutcome is { } updated) ComputeJobContext.Report(updated);
                return cpu;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("本地总结模型启动超时，请关闭其他模型任务后重试。"); }
    }

    private static async Task<LocalSummaryModel> StartCoreAsync(ModelStore store, string id, bool gpu, CancellationToken ct, Action<string>? status,
        Action<ModelPreparationProgress>? preparation = null)
    {
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        ModelLease? model = null, runtime = null; HttpClient? client = null; LocalSummaryModel? backend = null;
        OwnedModelProcess? owner = null;
        ComputeRoute? computeRoute = null;
        try
        {
            preparation?.Invoke(new("校验推理工具"));
            runtime = await store.AcquireAsync(ModelCatalog.SummaryRuntimeId, ct, verificationProgress: preparation is null ? null
                : (completed, total) => preparation(new("校验推理工具", completed, total))).ConfigureAwait(false);
            preparation?.Invoke(new("校验模型文件"));
            model = await store.AcquireAsync(id, ct, verificationProgress: preparation is null ? null
                : (completed, total) => preparation(new("校验模型文件", completed, total))).ConfigureAwait(false);
            var executable = ModelStore.FindRuntime(runtime.Directory) ?? throw new InvalidDataException("缺少本地总结推理工具。");
            var fingerprint = ComputeEnvironment.Fingerprint(executable);
            var route = computeRoute = new ComputeRouter(ComputeEnvironment.Platform, ComputeEnvironment.Capabilities, fingerprint)
                .Plan(new("本地总结", ComputeCapability.LlamaBackend, id, gpu ? ComputeSettings.CurrentPreference : ComputePreference.ForceCpu), status);
            string? fallbackReason = gpu ? null : "设置或 GPU 失败重试要求使用 CPU。";
            if (gpu && route.Chosen != ComputeBackend.Cpu)
            {
                var capability = await ComputeEnvironment.Capabilities.GetAsync(route.Requested, route.Chosen, fingerprint, new LlamaProbe(executable), ct).ConfigureAwait(false);
                if (!capability.Available)
                {
                    fallbackReason = capability.Reason; route.ReportFailure(route.Chosen, fallbackReason, persist: false); gpu = false;
                }
            }
            else gpu = false;
            var definition = ModelCatalog.Find(id);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var arguments = new List<string> { "-m", Path.Combine(model.Directory, definition.Files[0].Path), "--alias", id,
                "--ctx-size", "16384",
                "--parallel", "1", "--batch-size", "512", "--ubatch-size", "128",
                "--threads", CpuResourcePolicy.Threads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).ToString(),
                "--threads-batch", CpuResourcePolicy.Threads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)).ToString(),
                "--poll", "0", "--poll-batch", "0",
                "--host", "127.0.0.1", "--port", port.ToString(),
                "--api-key", key, "--no-context-shift", "--no-warmup", "--log-colors", "off", "--log-verbosity", "4", "--gpu-layers", gpu ? "auto" : "0" };
            if (!gpu) arguments.AddRange(["--device", "none"]);
            if (ModelCatalog.IsSummaryVision(id))
                arguments.AddRange(["--mmproj", Path.Combine(model.Directory, definition.Files[1].Path), gpu ? "--mmproj-offload" : "--no-mmproj-offload"]);
            arguments.AddRange(["--jinja", "--chat-template-kwargs", "{\"enable_thinking\":false}"]);
            status?.Invoke("加载本地模型");
            preparation?.Invoke(new("加载本地模型"));
            owner = await OwnedModelProcess.StartAsync(executable, arguments, ct).ConfigureAwait(false);
            var process = owner.Process;
            backend = new(owner, client, model, runtime, id, store, gpu, status) { FallbackReason = fallbackReason };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("本地总结模型启动失败，请在模型管理中修复。");
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); probe.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await client.GetAsync("health", probe.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        if (gpu && backend.Backend == "CPU")
                        {
                            backend.FallbackReason = "已发现 GPU，但启动日志未确认任何层卸载到 GPU；实际按 CPU 报告。";
                            if (route.Chosen != ComputeBackend.Cpu) route.ReportFailure(route.Chosen, backend.FallbackReason);
                            ComputeRuntimeFormatter.Log("回退 CPU：" + backend.FallbackReason, status);
                        }
                        else route.ReportSuccess(backend.Backend);
                        backend._computeOutcome = route.Outcome(backend.Backend) with { ActualBackend = backend.Backend == "CPU" ? ComputeBackend.Cpu : route.Chosen };
                        ComputeJobContext.Report(backend._computeOutcome);
                        return backend;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            var failedGpu = gpu && !ct.IsCancellationRequested && computeRoute is { Chosen: not ComputeBackend.Cpu };
            if (failedGpu) computeRoute!.ReportFailure(computeRoute.Chosen, "llama.cpp 启动失败：" + error.Message);
            if (backend is not null) await backend.DisposeAsync().ConfigureAwait(false);
            else
            {
                try { if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false); }
                finally { client?.Dispose(); model?.Dispose(); runtime?.Dispose(); }
            }
            if (failedGpu) throw new LocalGpuStartupException(error.Message, error);
            throw;
        }
    }

    public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null, int tokens = 1024,
        JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    {
        var content = SummaryChatProtocol.Content(prompt, image, images, ModelCatalog.IsSummaryVision(_id), 32);
        object[] messages = [new { role = "system", content = (object)system }, new { role = "user", content }];
        var request = Request(messages, tokens);
        if (schema is { } shape)
            request["response_format"] = new { type = "json_object", schema = shape };
        using var json = await SendAsync(request, ct).ConfigureAwait(false);
        var (message, limited) = SummaryChatProtocol.ReadResponse(json.RootElement, "本地总结模型响应格式无效。");
        var text = SummaryChatProtocol.ReadText(message, "本地总结模型未返回有效文本。");
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Trim();
        if (text.Length == 0) throw new InvalidDataException("本地总结模型返回了空内容。");
        if (limited) throw new InvalidDataException("总结超过输出长度，请缩小分段字符数或分析重点后重试。");
        return text;
    }

    public Task<string> CompleteWithToolsAsync(string system, string prompt, IReadOnlyList<SummaryModelImage> images,
        IReadOnlyList<SummaryModelTool> tools, CancellationToken ct, int tokens = 2048)
    {
        if (!ModelCatalog.IsSummaryVision(_id)) throw new ArgumentException("所选本地模型不支持画面工具。");
        return SummaryToolConversation.RunAsync(system, prompt, images, tools, ct, 600,
            (messages, _, _) => Request(messages, tokens), SendAsync);
    }

    private Dictionary<string, object> Request(IReadOnlyList<object> messages, int tokens)
    {
        return new Dictionary<string, object> { ["model"] = _id, ["messages"] = messages, ["stream"] = false,
            ["max_tokens"] = tokens, ["temperature"] = .2, ["top_p"] = .8,
            ["top_k"] = 20, ["min_p"] = 0, ["presence_penalty"] = 0,
            ["chat_template_kwargs"] = new { enable_thinking = false } };
    }

    private async Task<JsonDocument> SendAsync(Dictionary<string, object> request, CancellationToken ct)
    {
        if (_cpuFallback is { } fallback) return await fallback.SendAsync(request, ct).ConfigureAwait(false);
        try { return await SendCoreAsync(request, ct).ConfigureAwait(false); }
        catch (Exception error) when (_gpu && !_retriedGpu && !ct.IsCancellationRequested
            && error is LocalBackendException or HttpRequestException)
        {
            // With empty warm-up disabled, validate the GPU on useful input and retry that input once on CPU.
            _retriedGpu = true;
            ComputeRuntimeFormatter.Log("回退 CPU：llama.cpp GPU 推理失败：" + error.Message, _status);
            await ReleaseProcessAsync().ConfigureAwait(false);
            _cpuFallback = await StartCoreAsync(_store, _id, false, ct, _status).ConfigureAwait(false);
            _cpuFallback.FallbackReason = error.Message;
            if (_computeOutcome is { } outcome) _cpuFallback._computeOutcome = outcome with { ActualBackend = ComputeBackend.Cpu, FallbackChain = [..outcome.FallbackChain, new(outcome.ActualBackend, error.Message)] };
            return await _cpuFallback.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    internal async Task WarmAsync(CancellationToken ct, byte[]? image = null)
    {
        if (image is not null && _visualWarmed) return;
        var content = SummaryChatProtocol.Content("Describe briefly.", image, null, ModelCatalog.IsSummaryVision(_id), 1);
        // One token compiles the graph; no full caption or summary is generated speculatively.
        using var response = await SendAsync(Request([new { role = "user", content }], 1), ct).ConfigureAwait(false);
        if (image is not null) _visualWarmed = true;
    }
    internal void SetBackground(bool background)
    {
        if (_cpuFallback is { } fallback) { fallback.SetBackground(background); return; }
        if (!OperatingSystem.IsWindows()) { if (IsAlive) CpuResourcePolicy.LowerWorkerPriority(_process); return; }
        try { if (IsAlive) _process.PriorityClass = background ? ProcessPriorityClass.Idle
            : CpuResourcePolicy.Enabled ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { Trace.TraceInformation("Model process priority: {0}", error.Message); }
    }
    private sealed class LocalGpuStartupException(string message, Exception inner) : InvalidOperationException(message, inner);
    private sealed class LocalBackendException(string message) : InvalidOperationException(message);

    private async Task<JsonDocument> SendCoreAsync(Dictionary<string, object> request, CancellationToken ct)
    {
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        if (_computeOutcome is { } outcome) ComputeJobContext.Report(outcome);
        if (_process.HasExited) throw new LocalBackendException("本地总结模型已退出。");
        HttpResponseMessage response;
        try { response = await _client.PostAsJsonAsync("v1/chat/completions", request, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("本地总结超时，请减小分段字符数后重试。"); }
        using var completedResponse = response;
        if (!response.IsSuccessStatusCode)
        {
            var message = $"本地总结失败（HTTP {(int)response.StatusCode}），请减小采样画面数或修复模型。";
            if ((int)response.StatusCode >= 500) throw new LocalBackendException(message);
            throw new InvalidOperationException(message);
        }
        await response.Content.LoadIntoBufferAsync(1024 * 1024).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        try { await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false); return result; }
        catch { result.Dispose(); throw; }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var match = Regex.Match(line, @"offloaded (\d+)/\d+ layers to GPU");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var layers))
            {
                if (layers > 0) _backend = OperatingSystem.IsMacOS() ? "Metal / CPU" : "Vulkan / CPU";
                else FallbackReason = "llama.cpp 实际卸载 0 层到 GPU（可能显存不足），使用 CPU。";
                ComputeRuntimeFormatter.Log("计算后端：llama.cpp " + _backend + "；" + line, _status);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { if (_cpuFallback is { } fallback) await fallback.DisposeAsync().ConfigureAwait(false); }
        finally { await ReleaseProcessAsync().ConfigureAwait(false); }
    }

    private async ValueTask ReleaseProcessAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        try
        {
            await _owner.DisposeAsync(Task.WhenAll(_stdout, _stderr)).ConfigureAwait(false);
        }
        finally { _client.Dispose(); _model.Dispose(); _runtime.Dispose(); }
    }
}
