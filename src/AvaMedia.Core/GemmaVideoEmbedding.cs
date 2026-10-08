using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Optional, owned loopback llama.cpp process. No remote video uploads or implicit downloads.</summary>
public sealed class GemmaVideoEmbedding : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _client;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly ModelLease _model;
    private float[][] _labels = [];
    private GemmaVideoEmbedding(Process process, HttpClient client, ModelLease model)
    {
        _process = process; _client = client; _model = model;
        _stdout = DrainAsync(process.StandardOutput); _stderr = DrainAsync(process.StandardError);
    }

    public static async Task<GemmaVideoEmbedding> StartAsync(ModelStore store, CancellationToken ct, bool preferGpu = true)
    {
        try { return await StartCoreAsync(store, ct, preferGpu).ConfigureAwait(false); }
        catch (Exception error) when (preferGpu && !ct.IsCancellationRequested
            && error is InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            return await StartCoreAsync(store, ct, false).ConfigureAwait(false);
        }
    }

    private static async Task<GemmaVideoEmbedding> StartCoreAsync(ModelStore store, CancellationToken ct, bool preferGpu)
    {
        var lease = await store.AcquireAsync(ModelCatalog.EmbeddingId, ct).ConfigureAwait(false);
        GemmaVideoEmbedding? backend = null;
        HttpClient? client = null;
        try
        {
            var executable = ModelStore.FindRuntime(lease.Directory) ?? throw new InvalidDataException("缺少嵌入模型推理工具。");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            string[] arguments = ["-m", Path.Combine(lease.Directory, ModelCatalog.GemmaFile),
                "--mmproj", Path.Combine(lease.Directory, ModelCatalog.ProjectorFile), "--embedding", "--pooling", "mean",
                "--ctx-size", "8192", "--batch-size", "2048", "--ubatch-size", "2048", "--parallel", "1",
                "--threads", Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(),
                "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", key];
            arguments = arguments.Concat(preferGpu ? ["--gpu-layers", "auto", "--mmproj-offload"]
                : new[] { "--gpu-layers", "0", "--device", "none", "--no-mmproj-offload" }).ToArray();
            var process = await ProcessRunner.StartAsync(executable, arguments, ct).ConfigureAwait(false);
            backend = new(process, client, lease);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("嵌入模型启动失败，请在模型管理中重新下载。");
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await client.GetAsync("health", probe.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
            return backend;
        }
        catch
        {
            if (backend is not null) await backend.DisposeAsync();
            else { client?.Dispose(); lease.Dispose(); }
            throw;
        }
    }

    /// <returns>Similarity margin for person versus empty-scene descriptions; not a probability.</returns>
    public async Task<double> PersonMarginAsync(byte[] png, CancellationToken ct)
    {
        if (_labels.Length == 0)
            _labels = await EmbedLabelsAsync([
                "A person visible in the scene.", "People standing, sitting or walking.", "A person seen from behind or from the side.",
                "An empty room with nobody present.", "An outdoor scene without any people.", "A blank or black video frame."
            ], ct).ConfigureAwait(false);
        var vector = await EmbedImageAsync(png, ct).ConfigureAwait(false);
        var scores = _labels.Select(label => Cosine(vector, label)).ToArray();
        return scores.Take(3).Max() - scores.Skip(3).Max();
    }

    public Task<float[][]> EmbedLabelsAsync(IReadOnlyList<string> descriptions, CancellationToken ct)
    {
        if (descriptions.Count is < 1 or > 32 || descriptions.Any(text => string.IsNullOrWhiteSpace(text) || text.Length > 512))
            throw new ArgumentException("请输入 1–32 个关键词，描述不超过 512 字符。");
        return EmbedAsync(descriptions.Select(text => (object)("task: classification | query: " + text)).ToArray(), ct);
    }

    public async Task<float[]> EmbedImageAsync(byte[] png, CancellationToken ct)
        => (await EmbedImagesAsync([png], ct).ConfigureAwait(false))[0];

    public Task<float[][]> EmbedImagesAsync(IReadOnlyList<byte[]> images, CancellationToken ct)
    {
        if (images.Count is < 1 or > 4 || images.Any(image => image.Length == 0)) throw new ArgumentException("每批须包含 1–4 个有效画面。");
        return EmbedAsync(images.Select(png => (object)new
        {
            content = new[] { new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } } }
        }).ToArray(), ct);
    }

    private async Task<float[][]> EmbedAsync(object[] input, CancellationToken ct)
    {
        if (_process.HasExited) throw new InvalidOperationException("嵌入模型推理工具已退出。");
        using var response = await _client.PostAsJsonAsync("v1/embeddings", new { input, encoding_format = "float" }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var data = json.RootElement.GetProperty("data").EnumerateArray().OrderBy(item => item.GetProperty("index").GetInt32()).ToArray();
        if (data.Length != input.Length) throw new InvalidDataException("嵌入模型返回的向量数量不符。");
        var vectors = data.Select(item => item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray()).ToArray();
        if (vectors.Any(vector => vector.Length != 768 || vector.Any(value => !float.IsFinite(value)) || vector.All(value => value == 0)))
            throw new InvalidDataException("嵌入模型返回了无效向量。");
        return vectors;
    }

    public static double Cosine(float[] left, float[] right)
    {
        if (left.Length != 768 || right.Length != 768) throw new ArgumentException("嵌入向量维度不符。");
        double dot = 0, a = 0, b = 0;
        for (var index = 0; index < left.Length; index++) { dot += left[index] * right[index]; a += left[index] * left[index]; b += right[index] * right[index]; }
        return dot / Math.Sqrt(a * b);
    }
    private static async Task DrainAsync(StreamReader reader) { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
    public async ValueTask DisposeAsync()
    {
        try { if (!_process.HasExited) _process.Kill(true); await _process.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false); }
        finally { _client.Dispose(); _process.Dispose(); _model.Dispose(); }
    }
}
