using System.Text.Json;
using System.Text;

namespace AvaMedia.Core;

/// <summary>OpenAI-compatible Chat Completions; credentials never enter jobs or reports.</summary>
public sealed partial class OnlineSummaryModel : ISummaryModel
{
    private readonly OnlineAiClient _client;
    private readonly OnlineAiOptions _options;
    private readonly bool _vision;
    private readonly string _model;
    public string Backend => "线上 API";
    /// <summary>Model name sent in requests.</summary>
    public string Model => _model;

    /// <param name="model">Explicit model name; defaults to the provider's vision or text model.</param>
    public OnlineSummaryModel(OnlineAiOptions options, bool vision = false, string? model = null)
    {
        _options = options.Clone(); _vision = vision;
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (vision) _options.VisionModel = model.Trim(); else _options.TextModel = model.Trim();
        }
        // A vision-only provider (e.g. Ollama with moondream) may leave the text model empty.
        if (vision && string.IsNullOrWhiteSpace(_options.TextModel)) _options.TextModel = _options.EffectiveVisionModel;
        _options.Validate();
        _model = vision ? _options.EffectiveVisionModel : _options.TextModel;
        _client = new(_options);
    }

    public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
        int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    {
        if (image is not null && images is not null) throw new ArgumentException("不能同时传入单帧和多帧。");
        if ((image is not null || images is not null) && !_vision) throw new ArgumentException("请选择支持图像输入的视觉模型。");
        if (images is not null && images.Count is < 1 or > 32) throw new ArgumentException("线上画面联合分析每次需要 1–32 帧。");
        if (schema is { } shape) prompt += "\nReturn only JSON matching this schema:\n" + shape.GetRawText();
        object content = prompt;
        if (image is not null || images is not null)
            content = ImageContent(prompt, images ?? [new("", image!)]);
        var inputBytes = Encoding.UTF8.GetByteCount(system) + (long)Encoding.UTF8.GetByteCount(prompt)
            + (images?.Sum(frame => (long)Encoding.UTF8.GetByteCount(frame.Label)) ?? 0);
        var request = ConversationRequest([new { role = "system", content = system.Replace("/no_think", "", StringComparison.Ordinal) },
            new { role = "user", content }], inputBytes, images?.Count ?? (image is null ? 0 : 1));
        if (schema is { } jsonSchema && _options.ResponseFormat != OnlineAiResponseFormat.Prompt)
            request["response_format"] = _options.ResponseFormat == OnlineAiResponseFormat.JsonSchema
                ? new { type = "json_schema", json_schema = new { name = "video_summary", schema = jsonSchema, strict = true } }
                : new { type = "json_object" };
        using var json = await _client.CompleteAsync(request, ct).ConfigureAwait(false);
        try
        {
            var choice = json.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length")
                throw new InvalidDataException("线上 AI 输出达到长度限制，请选择输出更简洁的模型。");
            var result = choice.GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("线上 AI 未返回有效内容。");
            return result;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidDataException("线上 AI 响应格式无效，请使用 Chat Completions 兼容接口。");
        }
    }

    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }

    private Dictionary<string, object> ConversationRequest(IReadOnlyList<object> messages, long textBytes, int imageCount)
    {
        var request = new Dictionary<string, object> { ["model"] = _model, ["messages"] = messages };
        // Missing output metadata means the provider chooses its limit; do not impose a guessed small ceiling.
        if (_options.ModelInfo.FirstOrDefault(model => model.Id == _model)?.OutputBudget(textBytes, imageCount) is { } budget)
            request[_options.TokenLimit == OnlineAiTokenLimit.MaxTokens ? "max_tokens" : "max_completion_tokens"] = budget;
        return request;
    }

    private static List<object> ImageContent(string prompt, IReadOnlyList<SummaryModelImage> images)
    {
        var parts = new List<object> { new { type = "text", text = prompt } };
        foreach (var frame in images)
        {
            if (frame.Label.Length != 0) parts.Add(new { type = "text", text = frame.Label });
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(frame.Png) } });
        }
        return parts;
    }
}
