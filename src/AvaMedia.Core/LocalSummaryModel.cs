using System.Diagnostics;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<LocalSummaryModel, byte> Live = new();
    static LocalSummaryModel()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var model in Live.Keys) model.StopProcess(); };
    }
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
    private bool _visualWarmed;
    public string ModelId => _id;
    public string Backend => _cpuFallback?.Backend ?? _backend;
    internal bool IsAlive => _cpuFallback?.IsAlive ?? (Volatile.Read(ref _released) == 0 && !_process.HasExited);
    internal bool VisualWarmed => _visualWarmed;
    internal void SetStatus(Action<string>? status) { _status = status; _cpuFallback?.SetStatus(status); }
    private LocalSummaryModel(Process process, HttpClient client, ModelLease model, ModelLease runtime, string id,
        ModelStore store, bool gpu, Action<string>? status)
    {
        _process = process; _client = client; _model = model; _runtime = runtime; _id = id;
        _store = store; _gpu = gpu; _status = status;
        _stdout = DrainAsync(process.StandardOutput); _stderr = DrainAsync(process.StandardError);
        Live.TryAdd(this, 0);
    }

    public static async Task<LocalSummaryModel> StartAsync(ModelStore store, string id, bool preferGpu, CancellationToken ct,
        Action<string>? status = null, Action<ModelPreparationProgress>? preparation = null)
    {
        try
        {
            try { return await StartCoreAsync(store, id, preferGpu, ct, status, preparation).ConfigureAwait(false); }
            catch (Exception error) when (preferGpu && !ct.IsCancellationRequested
                && error is InvalidOperationException or HttpRequestException or OperationCanceledException)
            {
                status?.Invoke("GPU 启动未成功，切换 CPU");
                return await StartCoreAsync(store, id, false, ct, status, preparation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("本地总结模型启动超时，请关闭其他模型任务或改用 CPU。"); }
    }

    private static async Task<LocalSummaryModel> StartCoreAsync(ModelStore store, string id, bool gpu, CancellationToken ct, Action<string>? status,
        Action<ModelPreparationProgress>? preparation = null)
    {
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        ModelLease? model = null, runtime = null; HttpClient? client = null; LocalSummaryModel? backend = null;
        try
        {
            preparation?.Invoke(new("校验推理工具"));
            runtime = await store.AcquireAsync(ModelCatalog.SummaryRuntimeId, ct, verificationProgress: preparation is null ? null
                : (completed, total) => preparation(new("校验推理工具", completed, total))).ConfigureAwait(false);
            preparation?.Invoke(new("校验模型文件"));
            model = await store.AcquireAsync(id, ct, verificationProgress: preparation is null ? null
                : (completed, total) => preparation(new("校验模型文件", completed, total))).ConfigureAwait(false);
            var executable = ModelStore.FindRuntime(runtime.Directory) ?? throw new InvalidDataException("缺少本地总结推理工具。");
            var definition = ModelCatalog.Find(id);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var arguments = new List<string> { "-m", Path.Combine(model.Directory, definition.Files[0].Path), "--alias", id,
                "--ctx-size", id == ModelCatalog.SummaryVisionId ? "4096" : id == ModelCatalog.SummaryQwen35Id ? "16384" : "8192",
                "--parallel", "1", "--batch-size", "512", "--ubatch-size", "128",
                "--threads", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(), "--host", "127.0.0.1", "--port", port.ToString(),
                "--api-key", key, "--no-context-shift", "--no-warmup", "--log-colors", "off", "--log-verbosity", "4", "--gpu-layers", gpu ? "auto" : "0" };
            if (!gpu) arguments.AddRange(["--device", "none"]);
            if (ModelCatalog.IsSummaryVision(id))
                arguments.AddRange(["--mmproj", Path.Combine(model.Directory, definition.Files[1].Path), gpu ? "--mmproj-offload" : "--no-mmproj-offload"]);
            if (id != ModelCatalog.SummaryVisionId)
                arguments.AddRange(["--jinja", "--chat-template-kwargs", "{\"enable_thinking\":false}"]);
            status?.Invoke("加载本地模型");
            preparation?.Invoke(new("加载本地模型"));
            var process = await ProcessRunner.StartAsync(executable, arguments, ct).ConfigureAwait(false);
            backend = new(process, client, model, runtime, id, store, gpu, status);
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
                    if (response.IsSuccessStatusCode) return backend;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (backend is not null) await backend.DisposeAsync().ConfigureAwait(false);
            else { client?.Dispose(); model?.Dispose(); runtime?.Dispose(); }
            throw;
        }
    }

    public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null, int tokens = 1024,
        JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    {
        var content = SummaryChatProtocol.Content(prompt, image, images, ModelCatalog.IsSummaryVision(_id),
            _id == ModelCatalog.SummaryVisionId ? 3 : 32);
        // SmolVLM's published template is a user/assistant conversation; keep its visual instruction in user content.
        object[] messages = _id == ModelCatalog.SummaryVisionId
            ? [new { role = "user", content }]
            : [new { role = "system", content = (object)system }, new { role = "user", content }];
        // SmolVLM publishes greedy visual decoding. Text-model penalties must not
        // push its very small decoder towards unrelated objects or invented events.
        var visual = _id == ModelCatalog.SummaryVisionId;
        var request = Request(messages, tokens);
        if (schema is { } shape) request["response_format"] = new { type = "json_object", schema = shape };
        using var json = await SendAsync(request, ct).ConfigureAwait(false);
        var (message, limited) = SummaryChatProtocol.ReadResponse(json.RootElement, "本地总结模型响应格式无效。");
        var text = SummaryChatProtocol.ReadText(message, "本地总结模型未返回有效文本。");
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Trim();
        if (text.Length == 0) throw new InvalidDataException("本地总结模型返回了空内容。");
        if (visual)
        {
            // Small visual decoders can repeat after a complete caption. Keep whole sentences only;
            // an incomplete prefix is never turned into visual evidence or a partially parsed JSON answer.
            var sentences = Regex.Matches(text, """\G\s*.*?[.!?](?:["”’])?(?=\s|$)""", RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                .Cast<Match>().Take(images is null ? 1 : 2).ToArray();
            if (sentences.Length > 0) text = string.Join(" ", sentences.Select(sentence => sentence.Value.Trim()));
            else if (limited) throw new InvalidDataException("画面模型未返回完整观察，请减小采样画面数后重试。");
        }
        else if (limited) throw new InvalidDataException("总结超过输出长度，请缩小分段字符数或分析重点后重试。");
        return text;
    }

    public Task<string> CompleteWithToolsAsync(string system, string prompt, IReadOnlyList<SummaryModelImage> images,
        IReadOnlyList<SummaryModelTool> tools, CancellationToken ct, int tokens = 2048)
    {
        if (_id != ModelCatalog.SummaryQwen35Id) throw new ArgumentException("所选本地模型不支持画面工具。");
        return SummaryToolConversation.RunAsync(system, prompt, images, tools, ct, 600,
            (messages, _, _) => Request(messages, tokens), SendAsync);
    }

    private Dictionary<string, object> Request(IReadOnlyList<object> messages, int tokens)
    {
        var smol = _id == ModelCatalog.SummaryVisionId;
        var request = new Dictionary<string, object> { ["model"] = _id, ["messages"] = messages, ["stream"] = false,
            ["max_tokens"] = tokens, ["temperature"] = smol ? 0 : .2, ["top_p"] = .8,
            ["top_k"] = 20, ["min_p"] = 0, ["presence_penalty"] = 0 };
        if (smol) { request["repeat_penalty"] = 1.1; request["stop"] = new[] { "\n" }; }
        else request["chat_template_kwargs"] = new { enable_thinking = false };
        return request;
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
            _status?.Invoke("GPU 推理未成功，切换 CPU");
            await ReleaseProcessAsync().ConfigureAwait(false);
            _cpuFallback = await StartCoreAsync(_store, _id, false, ct, _status).ConfigureAwait(false);
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
        if (!OperatingSystem.IsWindows()) return; // Unix nice reductions cannot reliably be reversed by an unprivileged process.
        try { if (IsAlive) _process.PriorityClass = background ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { Trace.TraceInformation("Model process priority: {0}", error.Message); }
    }
    internal void StopProcess()
    {
        _cpuFallback?.StopProcess();
        try { if (Volatile.Read(ref _released) == 0 && !_process.HasExited) _process.Kill(true); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        { Trace.TraceInformation("Model process exit: {0}", error.Message); }
    }

    private sealed class LocalBackendException(string message) : InvalidOperationException(message);

    private async Task<JsonDocument> SendCoreAsync(Dictionary<string, object> request, CancellationToken ct)
    {
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
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
            if (match.Success && int.TryParse(match.Groups[1].Value, out var layers) && layers > 0)
                _backend = OperatingSystem.IsMacOS() ? "Metal / CPU" : "GPU / CPU";
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
            if (!_process.HasExited) _process.Kill(true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        }
        finally { _client.Dispose(); _process.Dispose(); _model.Dispose(); _runtime.Dispose(); Live.TryRemove(this, out _); }
    }
}
