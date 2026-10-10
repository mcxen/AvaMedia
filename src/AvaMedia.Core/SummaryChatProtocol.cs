using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Multimodal content and response parsing shared by local and remote Chat Completions.</summary>
internal static class SummaryChatProtocol
{
    internal static object Content(string prompt, byte[]? image, IReadOnlyList<SummaryModelImage>? images,
        bool vision, int maximumImages = 32)
    {
        if (image is not null && images is not null) throw new ArgumentException("不能同时传入单帧和多帧。");
        if (image is null && images is null) return prompt;
        if (!vision) throw new ArgumentException("请选择支持图像输入的视觉模型。");
        if (images is not null && (images.Count < 1 || images.Count > maximumImages))
            throw new ArgumentException($"画面联合分析每次需要 1–{maximumImages} 帧。");
        return ImageContent(prompt, images ?? [new("", image!)]);
    }

    internal static List<object> ImageContent(string prompt, IReadOnlyList<SummaryModelImage> images)
    {
        var parts = new List<object> { new { type = "text", text = prompt } };
        foreach (var frame in images)
        {
            if (frame.Label.Length != 0) parts.Add(new { type = "text", text = frame.Label });
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(frame.Png) } });
        }
        return parts;
    }

    internal static (JsonElement Message, bool Truncated) ReadResponse(JsonElement root, string error)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidDataException(error);
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(error);
        var truncated = false;
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
        {
            if (reason.ValueKind != JsonValueKind.String) throw new InvalidDataException(error);
            truncated = reason.GetString() == "length";
        }
        return (message, truncated);
    }

    internal static string ReadText(JsonElement message, string error)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(content.GetString())) throw new InvalidDataException(error);
        return content.GetString()!.Trim();
    }

    internal static string JsonText(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var headerEnd = text.IndexOf('\n');
        var closing = text.LastIndexOf("\n```", StringComparison.Ordinal);
        if (headerEnd < 3 || closing <= headerEnd || text[(closing + 4)..].Trim().Length != 0)
            throw new InvalidDataException("AI 响应的 JSON 代码块格式无效。");
        var language = text[3..headerEnd].Trim();
        if (language.Length != 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("AI 响应的 JSON 代码块格式无效。");
        return text[(headerEnd + 1)..closing].Trim();
    }
}
