using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

/// <summary>Task-owned llama.cpp process, authenticated loopback only, disposed before switching models.</summary>
public sealed class LocalSummaryModel : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly ModelLease _model, _runtime;
    private readonly Task _stdout, _stderr;
    private readonly string _id;
    public string Backend { get; private set; } = "CPU";
    private LocalSummaryModel(Process process, HttpClient client, ModelLease model, ModelLease runtime, string id)
    {
        _process = process; _client = client; _model = model; _runtime = runtime; _id = id;
        _stdout = DrainAsync(process.StandardOutput); _stderr = DrainAsync(process.StandardError);
    }

    public static async Task<LocalSummaryModel> StartAsync(ModelStore store, string id, bool preferGpu, CancellationToken ct,
        Action<string>? status = null)
    {
        try
        {
            try { return await StartCoreAsync(store, id, preferGpu, ct, status).ConfigureAwait(false); }
            catch (Exception error) when (preferGpu && !ct.IsCancellationRequested
                && error is InvalidOperationException or HttpRequestException or OperationCanceledException)
            {
                status?.Invoke("GPU 启动未成功，切换 CPU");
                return await StartCoreAsync(store, id, false, ct, status).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("本地总结模型启动超时，请关闭其他模型任务或改用 CPU。"); }
    }

    private static async Task<LocalSummaryModel> StartCoreAsync(ModelStore store, string id, bool gpu, CancellationToken ct, Action<string>? status)
    {
        ModelLease? model = null, runtime = null; HttpClient? client = null; LocalSummaryModel? backend = null;
        try
        {
            runtime = await store.AcquireAsync(ModelCatalog.SummaryRuntimeId, ct).ConfigureAwait(false);
            model = await store.AcquireAsync(id, ct).ConfigureAwait(false);
            var executable = ModelStore.FindRuntime(runtime.Directory) ?? throw new InvalidDataException("缺少本地总结推理工具。");
            var definition = ModelCatalog.Find(id);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var arguments = new List<string> { "-m", Path.Combine(model.Directory, definition.Files[0].Path), "--alias", id,
                "--ctx-size", id == ModelCatalog.SummaryVisionId ? "4096" : "8192", "--parallel", "1", "--batch-size", "512", "--ubatch-size", "128",
                "--threads", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(), "--host", "127.0.0.1", "--port", port.ToString(),
                "--api-key", key, "--no-context-shift", "--log-colors", "off", "--gpu-layers", gpu ? "auto" : "0" };
            if (!gpu) arguments.AddRange(["--device", "none"]);
            if (id == ModelCatalog.SummaryVisionId)
                arguments.AddRange(["--mmproj", Path.Combine(model.Directory, definition.Files[1].Path), gpu ? "--mmproj-offload" : "--no-mmproj-offload"]);
            else arguments.AddRange(["--jinja", "--chat-template-kwargs", "{\"enable_thinking\":false}"]);
            status?.Invoke("加载本地模型");
            var process = await ProcessRunner.StartAsync(executable, arguments, ct).ConfigureAwait(false);
            backend = new(process, client, model, runtime, id);
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
        JsonElement? schema = null)
    {
        if (_process.HasExited) throw new InvalidOperationException("本地总结模型已退出。");
        object content = prompt;
        if (image is not null) content = new object[] {
            new { type = "text", text = prompt }, new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(image) } } };
        // SmolVLM's published template is a user/assistant conversation; keep its visual instruction in user content.
        object[] messages = _id == ModelCatalog.SummaryVisionId
            ? [new { role = "user", content }]
            : [new { role = "system", content = (object)system }, new { role = "user", content }];
        HttpResponseMessage response;
        // SmolVLM publishes greedy visual decoding. Text-model penalties must not
        // push its very small decoder towards unrelated objects or invented events.
        var visual = _id == ModelCatalog.SummaryVisionId;
        var request = new Dictionary<string, object> { ["model"] = _id, ["messages"] = messages, ["stream"] = false,
            ["max_tokens"] = tokens, ["temperature"] = visual ? 0 : .2, ["top_p"] = .8,
            ["top_k"] = 20, ["min_p"] = 0, ["presence_penalty"] = 0 };
        if (schema is { } shape) request["response_format"] = new { type = "json_object", schema = shape };
        try
        {
            response = await _client.PostAsJsonAsync("v1/chat/completions", request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("本地总结超时，请减小分段字符数后重试。"); }
        using var completedResponse = response;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"本地总结失败（HTTP {(int)response.StatusCode}），请减小分段字符数或修复模型。");
        await response.Content.LoadIntoBufferAsync(1024 * 1024).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var body)
            || body.ValueKind != JsonValueKind.String) throw new InvalidDataException("本地总结模型未返回有效文本。");
        var text = body.GetString()!.Trim();
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Trim();
        if (text.Length == 0) throw new InvalidDataException("本地总结模型返回了空内容。");
        if (choices[0].TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
            throw new InvalidDataException("总结超过输出长度，请缩小分段字符数或分析重点后重试。");
        return text;
    }

    private async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var match = Regex.Match(line, @"offloaded (\d+)/\d+ layers to GPU");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var layers) && layers > 0)
                Backend = OperatingSystem.IsMacOS() ? "Metal / CPU" : "GPU / CPU";
        }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        }
        finally { _client.Dispose(); _process.Dispose(); _model.Dispose(); _runtime.Dispose(); }
    }
}
