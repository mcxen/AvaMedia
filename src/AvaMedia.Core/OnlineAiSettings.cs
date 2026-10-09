namespace AvaMedia.Core;

public sealed class OnlineAiSettings
{
    public List<OnlineAiOptions> Providers { get; set; } = [OnlineAiPresets.Create("dots")];
    public string DefaultProviderId { get; set; } = "";
    public OnlineAiSettings Clone() => new() { DefaultProviderId = DefaultProviderId, Providers = Providers.Select(p => p.Clone()).ToList() };

    public OnlineAiOptions Resolve(string? id = null)
    {
        var target = string.IsNullOrWhiteSpace(id) ? DefaultProviderId : id;
        var provider = target.Length == 0 ? Providers.FirstOrDefault(p => p.Enabled) : Providers.Find(p => p.Id == target);
        if (provider is null) throw new ArgumentException("请在 AI 供应商中添加或选择供应商。");
        if (!provider.Enabled) throw new ArgumentException("所选 AI 供应商已停用。");
        return provider;
    }

    public void Validate(bool requireModel = true)
    {
        if (Providers.Count > 32 || Providers.Select(p => p.Id).Distinct().Count() != Providers.Count
            || Providers.Select(p => p.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Providers.Count)
            throw new ArgumentException("供应商名称须唯一，最多添加 32 个供应商。");
        foreach (var provider in Providers) provider.Validate(requireModel: false);
        if (DefaultProviderId.Length != 0 && !Providers.Any(p => p.Id == DefaultProviderId && p.Enabled))
            throw new ArgumentException("默认供应商不存在或已停用。");
        if (requireModel) { var provider = Resolve(); provider.Validate(); provider.ValidateConnection(); }
    }
}

public sealed record OnlineAiPreset(string Id, string Name, string Endpoint, string Documentation, OnlineAiTokenLimit TokenLimit,
    string DefaultModel = "", string ApiKeyPage = "", OnlineAiResponseFormat ResponseFormat = OnlineAiResponseFormat.JsonObject);

public static class OnlineAiPresets
{
    public static IReadOnlyList<OnlineAiPreset> All { get; } = [
        new("dots", "小红书 Dots", "https://note3-prev-api.askdiandian.com/v1", "https://dots.ai/platform/docs", OnlineAiTokenLimit.MaxTokens,
            "dots3-note-prev", "https://dots.ai/platform/apikeys", OnlineAiResponseFormat.Prompt),
        new("openai", "OpenAI", "https://api.openai.com/v1", "https://developers.openai.com/api/docs", OnlineAiTokenLimit.MaxCompletionTokens),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "https://openrouter.ai/docs/quickstart", OnlineAiTokenLimit.MaxTokens),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1", "https://docs.siliconflow.cn/docs/userguide/quickstart", OnlineAiTokenLimit.MaxTokens),
        new("ollama", "Ollama", "http://localhost:11434/v1", "https://docs.ollama.com/api/openai-compatibility", OnlineAiTokenLimit.MaxTokens),
        new("custom", "自定义供应商", "", "", OnlineAiTokenLimit.MaxTokens)
    ];
    public static OnlineAiOptions Create(string id)
    {
        var preset = All.First(p => p.Id == id);
        return new()
        {
            Name = preset.Name, Preset = preset.Id, Endpoint = preset.Endpoint, TokenLimit = preset.TokenLimit,
            TextModel = preset.DefaultModel, VisionModel = preset.DefaultModel, ResponseFormat = preset.ResponseFormat,
            ModelIds = preset.DefaultModel.Length == 0 ? [] : [preset.DefaultModel]
        };
    }
}
