using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpProviderModels(string ProviderId, bool Refreshed, OnlineAiModelInfo[] Models, string[] ModelIds, string Revision);
public sealed record McpProviderCheck(string ProviderId, string Mode, string Endpoint, string Model, string Status, string? Response);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_get_settings", Title = "读取完整设置", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read active preferences, online providers, default provider ID and listener configuration. Credentials are represented only by hasApiKey, never returned. revision supports compare-and-set updates. Private activation is excluded; listener settings are read-only here to keep the current connection usable. Read before editing; no GUI is opened.")]
    public async Task<McpSettingsView> GetSettings(CancellationToken ct = default) => McpConfiguration.View(await workspace.SettingsAsync(null, null, ct).ConfigureAwait(false));

    [McpServerTool(Name = "avamedia_update_settings", Title = "更新设置选项", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Patch the active desktop settings and persist through the desktop Apply pipeline. Omitted/null fields are preserved. Covers output, privacy/Beta, resources, local model lifecycle/download source, image quality, tool paths and application preferences. Existing sessions may adopt resource settings only after reload. Linux scale applies at next launch; background mode suppresses completion UI/actions. expectedRevision from get_settings rejects concurrent changes. Does not unlock private tools or change the listener. Bundled tool paths cannot be edited.")]
    public async Task<McpSettingsView> UpdateSettings(McpSettingsPatch patch, string? expectedRevision = null, CancellationToken ct = default) =>
        McpConfiguration.View(await workspace.SettingsAsync(patch.Apply, expectedRevision, ct).ConfigureAwait(false));

    [McpServerTool(Name = "avamedia_provider_presets", Title = "线上供应商预设", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List native online provider presets and their endpoint/token/response defaults. Create a provider from a preset, then set text/vision models explicitly if the preset has none. No network request.")]
    public IReadOnlyList<OnlineAiPreset> ProviderPresets() => OnlineAiPresets.All;

    [McpServerTool(Name = "avamedia_create_provider", Title = "添加线上供应商", Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create and persist an online provider with a caller-generated UUID in N format as providerId. Same ID with identical settings is idempotent; different settings conflict. presetId comes from provider_presets. patch overrides preset defaults; apiKey is write-only and omitted from all results/settings JSON/jobs. makeDefault requires an enabled provider. Does not make an inference request or upload media.")]
    public async Task<McpSettingsView> CreateProvider(string providerId, string presetId, McpProviderPatch? patch = null,
        string? apiKey = null, bool makeDefault = false, string? expectedRevision = null, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(providerId, "N", out _)) throw new ArgumentException("providerId 须为 N 格式 UUID。");
        if (!OnlineAiPresets.All.Any(p => p.Id == presetId)) throw new ArgumentException("未知供应商预设。");
        var provider = OnlineAiPresets.Create(presetId); provider.Id = providerId; patch?.Apply(provider);
        if (apiKey is not null) provider.ApiKey = apiKey;
        return McpConfiguration.View(await workspace.SettingsAsync(settings =>
        {
            var existing = settings.OnlineAi.Providers.Find(p => p.Id == providerId);
            if (existing is null) settings.OnlineAi.Providers.Add(provider.Clone());
            else if (System.Text.Json.JsonSerializer.Serialize(existing) != System.Text.Json.JsonSerializer.Serialize(provider) || existing.ApiKey != provider.ApiKey)
                throw new ArgumentException("供应商 ID 已存在，参数不同；请使用更新接口。");
            if (makeDefault) settings.OnlineAi.DefaultProviderId = providerId;
        }, expectedRevision, ct).ConfigureAwait(false));
    }

    [McpServerTool(Name = "avamedia_update_provider", Title = "修改线上供应商", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Patch one online provider by stable ID. Omitted/null fields preserve values; empty visionModel falls back to textModel. apiKey omitted preserves the key, empty explicitly deletes it. Changing endpoint clears the old key and model cache; supply apiKey again for the new endpoint. makeDefault=false clears this provider as default if selected. Disabling the default clears its default selection. Advanced controls: tokenLimit, responseFormat, timeoutSeconds, manual modelIds. No inference or media upload.")]
    public async Task<McpSettingsView> UpdateProvider(string providerId, McpProviderPatch patch, string? apiKey = null,
        bool? makeDefault = null, string? expectedRevision = null, CancellationToken ct = default) =>
        McpConfiguration.View(await workspace.SettingsAsync(settings =>
        {
            var provider = McpConfiguration.Find(settings, providerId); patch.Apply(provider);
            if (apiKey is not null) provider.ApiKey = apiKey;
            if ((!provider.Enabled || makeDefault == false) && settings.OnlineAi.DefaultProviderId == providerId) settings.OnlineAi.DefaultProviderId = "";
            if (makeDefault == true) settings.OnlineAi.DefaultProviderId = providerId;
        }, expectedRevision, ct).ConfigureAwait(false));

    [McpServerTool(Name = "avamedia_delete_provider", Title = "删除线上供应商", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete a provider and its separately stored credential. Refuses providers referenced by waiting/running/paused/stopping tasks. replacementDefaultProviderId optionally selects an enabled replacement; otherwise deleting the default clears the selection (automatic first-enabled fallback). An already absent ID is harmless. No model files are deleted.")]
    public async Task<McpSettingsView> DeleteProvider(string providerId, string? replacementDefaultProviderId = null,
        string? expectedRevision = null, CancellationToken ct = default) =>
        McpConfiguration.View(await workspace.SettingsAsync(settings =>
        {
            settings.OnlineAi.Providers.RemoveAll(p => p.Id == providerId);
            if (replacementDefaultProviderId is not null) settings.OnlineAi.DefaultProviderId = replacementDefaultProviderId;
            else if (settings.OnlineAi.DefaultProviderId == providerId) settings.OnlineAi.DefaultProviderId = "";
        }, expectedRevision, ct).ConfigureAwait(false));

    [McpServerTool(Name = "avamedia_provider_models", Title = "读取线上模型列表", Destructive = false, UseStructuredContent = true)]
    [Description("Read a provider's cached models (refresh=false, no network), or explicitly refresh using native GET /models and persist the cache. refresh checks endpoint/auth connectivity but does not prove vision/inference support. No media or prompts are sent. Concurrent configuration changes reject cache persistence; retry with fresh settings. Providers without /models can use manual modelIds/textModel/visionModel via update_provider.")]
    public async Task<McpProviderModels> ProviderModels(string providerId, bool refresh = false, CancellationToken ct = default)
    {
        var settings = await workspace.SettingsAsync(null, null, ct).ConfigureAwait(false);
        var provider = McpConfiguration.Find(settings, providerId);
        if (refresh)
        {
            using var client = new OnlineAiClient(provider);
            var models = await client.GetModelsAsync(ct).ConfigureAwait(false);
            settings = await workspace.SettingsAsync(current =>
            {
                var selected = McpConfiguration.Find(current, providerId);
                selected.ModelInfo = models; selected.ModelIds = models.Select(model => model.Id).ToArray();
            }, McpConfiguration.Revision(settings), ct).ConfigureAwait(false);
            provider = McpConfiguration.Find(settings, providerId);
        }
        return new(providerId, refresh, provider.ModelInfo, provider.ModelIds, McpConfiguration.Revision(settings));
    }

    [McpServerTool(Name = "avamedia_check_provider", Title = "验证线上模型配置", Destructive = false, UseStructuredContent = true)]
    [Description("Explicit provider diagnostic, without changing settings. mode=configuration only validates endpoint/auth/model settings locally. mode=text sends a small fixed JSON prompt to the configured text model. mode=vision requires mediaPath and sends one bounded sampled image/frame plus a fixed diagnostic prompt to the vision model. model optionally overrides the selected model for this call. Uses the same native OnlineSummaryModel token/response behavior as production. A successful response proves request acceptance, not semantic quality or NSFW support. These diagnostic calls may incur provider usage; do not invoke text/vision automatically just to read settings.")]
    public async Task<McpProviderCheck> CheckProvider(string providerId, string mode = "configuration", string? model = null,
        string? mediaPath = null, double seconds = 0, CancellationToken ct = default)
    {
        if (mode is not ("configuration" or "text" or "vision")) throw new ArgumentException("mode 须为 configuration/text/vision。");
        var settings = await workspace.SettingsAsync(null, null, ct).ConfigureAwait(false);
        var provider = McpConfiguration.Find(settings, providerId);
        provider.Validate(requireModel: false); provider.ValidateConnection();
        var selected = model ?? (mode == "vision" || mode == "configuration" && provider.TextModel.Length == 0 ? provider.EffectiveVisionModel : provider.TextModel);
        if (string.IsNullOrWhiteSpace(selected) || selected.Length > 128 || selected.Any(char.IsControl))
            throw new ArgumentException("请选择有效的模型名称。");
        if (mode == "configuration") return new(providerId, mode, provider.Endpoint, selected, "configuration-valid", null);
        byte[]? image = null;
        if (mode == "vision")
        {
            if (mediaPath is null) throw new ArgumentException("视觉诊断须提供服务器媒体路径 mediaPath。");
            var preview = await EditorPreview(mediaPath, seconds, ct).ConfigureAwait(false);
            image = Convert.FromBase64String(preview.Thumbnail);
        }
        await using var client = new OnlineSummaryModel(provider, vision: mode == "vision", model: selected);
        using var schema = System.Text.Json.JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"},\"description\":{\"type\":\"string\"}},\"required\":[\"ok\",\"description\"],\"additionalProperties\":false}");
        var response = await client.CompleteAsync("You are checking an application's model connection. Return a short JSON object with ok and description.",
            mode == "vision" ? "Set ok=true and briefly describe the attached image." : "Set ok=true and description=connection accepted.",
            ct, image, schema: schema.RootElement).ConfigureAwait(false);
        return new(providerId, mode, provider.Endpoint, selected, "response-received", response.Length <= 4096 ? response : response[..4096]);
    }
}
