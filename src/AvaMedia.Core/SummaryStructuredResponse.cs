using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Repair malformed model output once, before any citation checks or semantic reviews run.</summary>
internal static class SummaryStructuredResponse
{
    internal static async Task<JsonElement> CompleteAsync(ISummaryModel model, string system, string prompt,
        JsonElement schema, CancellationToken ct, int tokens, string operation, Action<string>? onRepair = null)
    {
        // Transport, provider errors, output limits and cancellation are not format errors.
        ct.ThrowIfCancellationRequested();
        var reply = await model.CompleteAsync(system, prompt, ct, tokens: tokens, schema: schema).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        JsonException firstError;
        try { return Parse(reply, schema); }
        catch (JsonException error) { firstError = error; }

        ct.ThrowIfCancellationRequested();
        onRepair?.Invoke(operation);
        var correction = prompt + "\n\n上一次输出未通过 JSON 格式校验。错误：" + Describe(firstError) +
            "\n请根据上方原任务和原资料重新生成完整 JSON，严格符合下方 schema。只输出 JSON，不输出思考过程、解释或代码围栏。" +
            "不增加资料未支持的事实，不省略必需字段，不把缺少依据的结论填成事实；没有支持的表述时使用允许的空数组。" +
            "\nSchema：\n" + schema.GetRawText();
        reply = await model.CompleteAsync(system, correction, ct, tokens: tokens, schema: schema).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try { return Parse(reply, schema); }
        catch (JsonException error)
        {
            throw new InvalidDataException($"总结模型的{operation}在一次格式纠正后仍不符合 JSON 结构：{Describe(error)}", error);
        }
    }

    private static string Describe(JsonException error) => error.Message.Length > 300 ? error.Message[..300] : error.Message;

    private static JsonElement Parse(string reply, JsonElement schema)
    {
        var text = reply.Trim();
        if (!text.StartsWith('{') && MediaCaptionService.LooksLikeRefusal(text)) throw new VideoSummaryModelRefusedException();
        using var document = JsonDocument.Parse(reply);
        Validate(document.RootElement, schema, "$");
        return document.RootElement.Clone();
    }

    // This validates the complete schema used by summaries, including later chapter entries,
    // before any caller mutates rejection counters, expands notes or invokes a model review.
    private static void Validate(JsonElement value, JsonElement schema, string path)
    {
        var type = schema.GetProperty("type").GetString();
        switch (type)
        {
            case "object":
                Require(value.ValueKind == JsonValueKind.Object, path, "必须是对象");
                var properties = schema.GetProperty("properties");
                foreach (var required in schema.GetProperty("required").EnumerateArray())
                    Require(value.TryGetProperty(required.GetString()!, out _), path + "." + required.GetString(), "缺少必需字段");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    Require(names.Add(property.Name), path, "不能重复字段");
                    if (properties.TryGetProperty(property.Name, out var shape)) Validate(property.Value, shape, path + "." + property.Name);
                    else Require(!schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False,
                        path, "不允许额外字段");
                }
                break;
            case "array":
                Require(value.ValueKind == JsonValueKind.Array, path, "必须是数组");
                var count = value.GetArrayLength();
                if (schema.TryGetProperty("minItems", out var minimum)) Require(count >= minimum.GetInt32(), path, "数组项目过少");
                if (schema.TryGetProperty("maxItems", out var maximum)) Require(count <= maximum.GetInt32(), path, "数组项目过多");
                var itemShape = schema.GetProperty("items");
                var index = 0;
                foreach (var item in value.EnumerateArray()) Validate(item, itemShape, $"{path}[{index++}]");
                break;
            case "string":
                Require(value.ValueKind == JsonValueKind.String, path, "必须是字符串");
                if (schema.TryGetProperty("maxLength", out var length)) Require(value.GetString()!.Length <= length.GetInt32(), path, "字符串过长");
                break;
            case "boolean":
                Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, path, "必须是布尔值");
                break;
            default:
                throw new InvalidOperationException("总结结构使用了未支持的 schema 类型。");
        }
    }

    private static void Require(bool valid, string path, string message)
    {
        if (!valid) throw new JsonException(path + "：" + message);
    }
}
