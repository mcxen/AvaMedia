using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed record OnlineAiModelInfo(string Id, int? ContextTokens = null, int? MaxOutputTokens = null)
{
    [JsonIgnore] public bool HasLimits => ContextTokens.HasValue || MaxOutputTokens.HasValue;

    internal static OnlineAiModelInfo Read(JsonElement model)
    {
        var id = model.GetProperty("id").GetString()!;
        int? context = ReadLimit(model, "context_length", "context_window", "max_context_tokens", "max_model_len");
        int? output = ReadLimit(model, "max_output_tokens", "max_completion_tokens");
        foreach (var name in new[] { "top_provider", "limits", "capabilities" })
            if (model.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                context ??= ReadLimit(nested, "context_length", "context_window", "max_context_tokens", "max_model_len");
                output ??= ReadLimit(nested, "max_output_tokens", "max_completion_tokens");
            }
        return new(id, context, output);
    }

    private static int? ReadLimit(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.TryGetProperty(name, out var limit) && limit.ValueKind == JsonValueKind.Number
                && limit.TryGetInt32(out var tokens) && tokens > 0) return tokens;
        return null;
    }

    // Context includes the request. Only an explicit output limit can define the completion ceiling.
    public int? OutputBudget(long inputTextBytes, int images)
    {
        if (MaxOutputTokens is not { } output) return null;
        if (ContextTokens is not { } context) return output;
        // Conservative reserve; the provider remains authoritative about image tokenization.
        var remaining = context - inputTextBytes - 128L - 4096L * images;
        return remaining > 0 ? (int)Math.Min(output, remaining) : null;
    }
}
