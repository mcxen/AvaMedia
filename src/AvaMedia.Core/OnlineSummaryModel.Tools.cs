using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record OnlineSummaryTool(string Name, string Description, JsonElement Parameters,
    Func<JsonElement, CancellationToken, Task<OnlineSummaryToolResult>> ExecuteAsync);
public sealed record OnlineSummaryToolResult(string Text, IReadOnlyList<SummaryModelImage>? Images = null);

public sealed partial class OnlineSummaryModel
{
    /// <summary>Bounded vision conversation. Tool images follow their matching tool result as a user image message.</summary>
    public async Task<string> CompleteWithToolsAsync(string system, string prompt, IReadOnlyList<SummaryModelImage> images,
        IReadOnlyList<OnlineSummaryTool> tools, CancellationToken ct)
    {
        if (!_vision) throw new ArgumentException("请选择支持图像输入的视觉模型。");
        return await RunToolsAsync(system, prompt, images, tools, ct, _options.TimeoutSeconds, ConversationRequest,
            _client.CompleteAsync).ConfigureAwait(false);
    }

    // Local llama.cpp and remote providers share the same bounded, validated frame-tool conversation.
    internal static async Task<string> RunToolsAsync(string system, string prompt, IReadOnlyList<SummaryModelImage> images,
        IReadOnlyList<OnlineSummaryTool> tools, CancellationToken ct, int timeoutSeconds,
        Func<IReadOnlyList<object>, long, int, Dictionary<string, object>> conversationRequest,
        Func<Dictionary<string, object>, CancellationToken, Task<JsonDocument>> complete)
    {
        if (images.Count is < 1 or > 32) throw new ArgumentException("画面联合分析每次需要 1–32 帧。");
        if (tools.Count is < 1 or > 8 || tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("画面描述工具配置无效。");
        var messages = new List<object> { new { role = "system", content = system.Replace("/no_think", "", StringComparison.Ordinal) },
            new { role = "user", content = ImageContent(prompt, images) } };
        var definitions = tools.Select(tool => new { type = "function", function = new
            { name = tool.Name, description = tool.Description, parameters = tool.Parameters } }).ToArray();
        long textBytes = Encoding.UTF8.GetByteCount(system) + (long)Encoding.UTF8.GetByteCount(prompt)
            + images.Sum(frame => (long)Encoding.UTF8.GetByteCount(frame.Label))
            + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(definitions));
        var imageCount = images.Count;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            for (var round = 0; round <= 2; round++)
            {
                var request = conversationRequest(messages, textBytes, imageCount);
                request["tools"] = definitions;
                request["tool_choice"] = round < 2 ? "auto" : "none";
                using var response = await complete(request, deadline.Token).ConfigureAwait(false);
                var message = ToolMessage(response.RootElement);
                if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null
                    || calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 0)
                {
                    if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(content.GetString())) throw new InvalidDataException("画面描述模型未返回有效内容。");
                    return content.GetString()!.Trim();
                }
                if (round == 2 || calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 8)
                    throw new InvalidDataException("画面描述工具调用超过限制或格式无效。");
                // Validate the complete call batch before executing anything. Preserve provider reasoning fields in history.
                var parsed = calls.EnumerateArray().Select(ReadToolCall).ToArray();
                if (parsed.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count() != parsed.Length)
                    throw new InvalidDataException("画面描述工具响应格式无效。");
                messages.Add(message);
                textBytes += Encoding.UTF8.GetByteCount(message.GetRawText());
                var returnedImages = new List<SummaryModelImage>();
                foreach (var call in parsed)
                {
                    var tool = tools.FirstOrDefault(tool => tool.Name == call.Name);
                    OnlineSummaryToolResult result;
                    if (tool is null) result = new("工具不可用。请仅使用已提供的工具。");
                    else
                    {
                        try
                        {
                            using var arguments = JsonDocument.Parse(call.Arguments);
                            result = await tool.ExecuteAsync(arguments.RootElement, deadline.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is JsonException or ArgumentException)
                        { result = new("工具参数无效：" + (error is JsonException ? "请使用有效的 JSON 对象。" : error.Message)); }
                    }
                    messages.Add(new { role = "tool", tool_call_id = call.Id, content = result.Text });
                    textBytes += Encoding.UTF8.GetByteCount(result.Text);
                    if (result.Images is { } frames)
                    {
                        if (imageCount + frames.Count > 40) throw new InvalidDataException("画面描述工具调用超过限制或格式无效。");
                        returnedImages.AddRange(frames); imageCount += frames.Count;
                        textBytes += frames.Sum(frame => (long)Encoding.UTF8.GetByteCount(frame.Label));
                    }
                }
                // Keep all tool replies adjacent to the assistant call batch before attaching actual returned pixels.
                if (returnedImages.Count > 0)
                {
                    const string label = "以下是取帧工具实际返回的画面，请结合时间和局部区域核对；请求参数不能证明画面内容。";
                    messages.Add(new { role = "user", content = ImageContent(label, returnedImages) });
                    textBytes += Encoding.UTF8.GetByteCount(label);
                }
                if (round == 1)
                {
                    const string final = "补帧已结束。请仅依据已看到的画面给出最终描述，省略仍不能确认的细节。";
                    messages.Add(new { role = "user", content = final });
                    textBytes += Encoding.UTF8.GetByteCount(final);
                }
            }
            throw new InvalidDataException("画面描述模型未返回有效内容。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("画面描述工具调用超时。"); }
    }

    private static JsonElement ToolMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 || choices[0].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("画面描述工具响应格式无效。");
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length")
            throw new InvalidDataException("画面描述达到输出长度限制，请减少采样帧数或缩短描述要求。");
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")
            throw new InvalidDataException("画面描述工具响应格式无效。");
        return message.Clone();
    }

    private static (string Id, string Name, string Arguments) ReadToolCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256
            || !call.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "function"
            || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object
            || !function.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
            || !function.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.String
            || arguments.GetString()!.Length > 8192)
            throw new InvalidDataException("画面描述工具响应格式无效。");
        return (id.GetString()!, name.GetString()!, arguments.GetString()!);
    }
}
