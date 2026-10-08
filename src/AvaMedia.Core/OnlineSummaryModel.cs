using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>OpenAI-compatible Chat Completions; credentials never enter jobs or reports.</summary>
public sealed class OnlineSummaryModel : ISummaryModel
{
    private readonly HttpClient _client;
    private readonly OnlineAiOptions _options;
    private readonly bool _vision;
    private readonly string _model;
    public string Backend => "线上 API";

    public OnlineSummaryModel(OnlineAiOptions options, bool vision = false)
    {
        _options = options.Clone(); _options.Validate(); _vision = vision;
        _model = vision ? _options.EffectiveVisionModel : _options.TextModel;
        _client = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (_options.ApiKey.Length != 0)
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
        int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    {
        if (image is not null && images is not null) throw new ArgumentException("不能同时传入单帧和多帧。");
        if ((image is not null || images is not null) && !_vision) throw new ArgumentException("请选择支持图像输入的视觉模型。");
        if (images is not null && images.Count is < 1 or > 3) throw new ArgumentException("画面联合分析每次需要 1–3 帧。");
        if (schema is { } shape) prompt += "\nReturn only JSON matching this schema:\n" + shape.GetRawText();
        object content = prompt;
        if (image is not null || images is not null)
        {
            var parts = new List<object> { new { type = "text", text = prompt } };
            foreach (var frame in images ?? [new("", image!)])
            {
                if (frame.Label.Length != 0) parts.Add(new { type = "text", text = frame.Label });
                parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(frame.Png) } });
            }
            content = parts;
        }
        var request = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = new object[] { new { role = "system", content = system.Replace("/no_think", "", StringComparison.Ordinal) },
                new { role = "user", content } },
            ["max_completion_tokens"] = tokens
        };
        if (schema is { } jsonSchema)
            request["response_format"] = _options.UseJsonSchema
                ? new { type = "json_schema", json_schema = new { name = "video_summary", schema = jsonSchema, strict = true } }
                : new { type = "json_object" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _options.CompletionUri()) { Content = JsonContent.Create(request) };
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"线上 AI 请求失败（HTTP {(int)response.StatusCode}），请检查接口地址、API Key、模型及额度。");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int count;
            while ((count = await body.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("线上 AI 响应超过大小限制。");
                buffer.Write(bytes, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            var choice = json.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
                throw new InvalidDataException("线上 AI 输出达到长度限制，请选择输出更简洁的模型。");
            var result = choice.GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("线上 AI 未返回有效内容。");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("线上 AI 请求超时。"); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("无法连接线上 AI 接口，请检查地址和网络。"); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            // Preserve our own actionable messages; never expose a provider response body.
            if (error is InvalidOperationException && error.Message.StartsWith("线上 AI", StringComparison.Ordinal)) throw;
            throw new InvalidDataException("线上 AI 响应格式无效，请使用 Chat Completions 兼容接口。");
        }
    }

    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
}
