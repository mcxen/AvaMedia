using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed class OnlineAiClient : IDisposable
{
    private readonly OnlineAiOptions _options;
    private readonly HttpClient _client;
    public OnlineAiClient(OnlineAiOptions options)
    {
        _options = options.Clone(); _options.ValidateConnection();
        _client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (_options.ApiKey.Length != 0)
        {
            if (_options.Preset == "dots") _client.DefaultRequestHeaders.Add("api-key", _options.ApiKey);
            else _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<OnlineAiModelInfo[]> GetModelsAsync(CancellationToken ct)
    {
        using var json = await SendAsync(HttpMethod.Get, _options.ModelsUri(), null, ct, Math.Min(30, _options.TimeoutSeconds)).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("供应商模型列表格式无效，可手动填写模型。");
        return data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(OnlineAiModelInfo.Read)
            .Where(model => !string.IsNullOrWhiteSpace(model.Id) && model.Id.Length <= 128 && !model.Id.Any(char.IsControl))
            .DistinctBy(model => model.Id, StringComparer.Ordinal).OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase).Take(2048).ToArray();
    }

    public async Task<OnlineAiModelInfo?> GetModelInfoAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) throw new ArgumentException("线上 AI 模型名称无效。");
        try
        {
            var uri = new Uri(_options.ModelsUri().AbsoluteUri + "/" + Uri.EscapeDataString(id));
            using var json = await SendAsync(HttpMethod.Get, uri, null, ct, Math.Min(30, _options.TimeoutSeconds)).ConfigureAwait(false);
            var model = json.RootElement;
            if (model.ValueKind == JsonValueKind.Object && model.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) model = data;
            return model.ValueKind == JsonValueKind.Object && model.TryGetProperty("id", out var modelId)
                && modelId.ValueKind == JsonValueKind.String && modelId.GetString() == id ? OnlineAiModelInfo.Read(model) : null;
        }
        catch (ProviderHttpException error) when (error.StatusCode is 404 or 405) { return null; }
    }

    private sealed class ProviderHttpException(int statusCode, string message) : IOException(message)
    { public int StatusCode { get; } = statusCode; }

    internal async Task<JsonDocument> CompleteAsync(object request, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, _options.CompletionUri(), request, ct, _options.TimeoutSeconds).ConfigureAwait(false);

    private async Task<JsonDocument> SendAsync(HttpMethod method, Uri uri, object? payload, CancellationToken ct, int timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            using var message = new HttpRequestMessage(method, uri);
            if (payload is not null) message.Content = JsonContent.Create(payload);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderHttpException((int)response.StatusCode, (int)response.StatusCode switch
                {
                    401 or 403 => "供应商拒绝授权，请检查 API Key 或权限。",
                    404 or 405 when method == HttpMethod.Get => "供应商未提供模型列表，可手动填写模型。",
                    404 => "接口或模型不存在，请检查地址与模型名称。",
                    429 => "供应商限流或额度不足，请稍后重试。",
                    400 => "供应商拒绝请求，请检查模型和高级参数。",
                    _ => AppLanguage.IsChinese ? $"供应商请求失败（HTTP {(int)response.StatusCode}）。"
                        : $"Provider request failed (HTTP {(int)response.StatusCode})."
                });
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            var maximum = method == HttpMethod.Get ? 8 * 1024 * 1024 : 1024 * 1024;
            while ((count = await body.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > maximum) throw new InvalidDataException("供应商响应超过大小限制。");
                buffer.Write(bytes, 0, count);
            }
            return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("供应商请求超时。"); }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接供应商，请检查地址和网络。"); }
        catch (JsonException) { throw new InvalidDataException("供应商响应格式无效，请使用 Chat Completions 兼容接口。"); }
    }
    public void Dispose() => _client.Dispose();
}
