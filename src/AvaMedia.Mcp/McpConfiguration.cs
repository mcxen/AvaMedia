using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvaMedia.Core;

namespace AvaMedia.Mcp;

/// <summary>Explicit writable preferences. Null means preserve the current value.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class McpSettingsPatch
{
    public string? OutputFolder { get; set; }
    public bool? OutputToSource { get; set; }
    public bool? AddSettingName { get; set; }
    public bool? OpenOutputFolderOnComplete { get; set; }
    public bool? ShutdownOnComplete { get; set; }
    public bool? PlayOperationSound { get; set; }
    public bool? PlayCompleteSound { get; set; }
    public bool? PlayErrorSound { get; set; }
    public bool? SystemContextMenu { get; set; }
    public bool? MinimizeToTray { get; set; }
    public bool? CloseToTray { get; set; }
    public bool? CheckForUpdates { get; set; }
    public bool? AutoUpdate { get; set; }
    public bool? SilentUpdate { get; set; }
    public bool? EnableBetaFeatures { get; set; }
    public bool? EnableNsfwContent { get; set; }
    public bool? AutoDownloadRepairModel { get; set; }
    public bool? PrewarmLocalModels { get; set; }
    public int? ModelWarmupInitialCpuPercent { get; set; }
    public int? ModelWarmupInitialGpuPercent { get; set; }
    public int? ModelWarmupCpuPercent { get; set; }
    public int? ModelWarmupGpuPercent { get; set; }
    public int? ModelWarmupRampSeconds { get; set; }
    [Description("Idle release in minutes: 1, 5, 15, 30, or -1 to retain until exit.")]
    public int? TagModelIdleMinutes { get; set; }
    [Description("Auto, HuggingFace, ModelScope, HfMirror, Custom.")]
    public string? ModelSource { get; set; }
    public string? ModelSourceUrl { get; set; }
    public string? FFmpegPath { get; set; }
    public string? FFprobePath { get; set; }
    public string? YtDlpPath { get; set; }
    public bool? MultiThread { get; set; }
    public bool? AutomaticThreads { get; set; }
    public int? CpuThreads { get; set; }
    public int? CpuReservePercent { get; set; }
    public bool? NotifyComplete { get; set; }
    public bool? CollapseCompletedTasks { get; set; }
    public bool? ReduceMotion { get; set; }
    public string? Theme { get; set; }
    public string? AccentColor { get; set; }
    public string? Language { get; set; }
    public int? LinuxInterfaceScalePercent { get; set; }
    public bool? ConfirmPlayerDeletion { get; set; }
    public bool? PlayerNativeHighResolution { get; set; }
    public bool? PlayerNativeSdr { get; set; }
    public bool? AutoDetectGpu { get; set; }
    public int? JpegQuality { get; set; }
    public int? WebpQuality { get; set; }

    private static readonly (PropertyInfo Patch, PropertyInfo Setting)[] Properties = typeof(McpSettingsPatch).GetProperties()
        .Select(property => (property, typeof(AppSettings).GetProperty(property.Name)!)).ToArray();
    public void Apply(AppSettings settings)
    {
        foreach (var (patch, setting) in Properties)
            if (patch.GetValue(this) is { } value) setting.SetValue(settings, value);
    }
    public static McpSettingsPatch From(AppSettings settings)
    {
        var result = new McpSettingsPatch();
        foreach (var (patch, setting) in Properties) patch.SetValue(result, setting.GetValue(settings));
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class McpProviderPatch
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public string? Endpoint { get; set; }
    public string? TextModel { get; set; }
    public string? VisionModel { get; set; }
    public OnlineAiTokenLimit? TokenLimit { get; set; }
    public OnlineAiResponseFormat? ResponseFormat { get; set; }
    public int? TimeoutSeconds { get; set; }
    public string[]? ModelIds { get; set; }
    public OnlineAiModelInfo[]? ModelInfo { get; set; }

    public void Apply(OnlineAiOptions provider)
    {
        if (Endpoint is not null && Endpoint != provider.Endpoint)
        {
            // A key for the old endpoint must never silently be sent to a new host.
            provider.ApiKey = ""; provider.ModelIds = []; provider.ModelInfo = [];
        }
        provider.Name = Name ?? provider.Name; provider.Enabled = Enabled ?? provider.Enabled;
        provider.Endpoint = Endpoint ?? provider.Endpoint; provider.TextModel = TextModel ?? provider.TextModel;
        provider.VisionModel = VisionModel ?? provider.VisionModel; provider.TokenLimit = TokenLimit ?? provider.TokenLimit;
        provider.ResponseFormat = ResponseFormat ?? provider.ResponseFormat; provider.TimeoutSeconds = TimeoutSeconds ?? provider.TimeoutSeconds;
        if (ModelIds is not null) provider.ModelIds = ModelIds.ToArray();
        if (ModelInfo is not null) provider.ModelInfo = ModelInfo.ToArray();
    }
}

public sealed record McpProviderView(string Id, string Name, string Preset, bool Enabled, string Endpoint,
    string TextModel, string VisionModel, string EffectiveVisionModel, bool HasApiKey, OnlineAiTokenLimit TokenLimit,
    OnlineAiResponseFormat ResponseFormat, int TimeoutSeconds, string[] ModelIds, OnlineAiModelInfo[] ModelInfo)
{
    public static McpProviderView From(OnlineAiOptions p) => new(p.Id, p.Name, p.Preset, p.Enabled, p.Endpoint,
        p.TextModel, p.VisionModel, p.EffectiveVisionModel, p.ApiKey.Length != 0, p.TokenLimit, p.ResponseFormat,
        p.TimeoutSeconds, p.ModelIds.ToArray(), p.ModelInfo.ToArray());
}
public sealed record McpSettingsView(string Revision, McpSettingsPatch Preferences, string DefaultProviderId,
    McpProviderView[] Providers, McpSettings RemoteManagement, bool BundledTools);

public static class McpConfiguration
{
    public static string Revision(AppSettings settings) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Settings = settings,
        // Credential changes participate in optimistic concurrency without returning any credential.
        Keys = settings.OnlineAi.Providers.Select(p => new { p.Id, Hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.ApiKey))) })
    })));
    public static McpSettingsView View(AppSettings settings) => new(Revision(settings), McpSettingsPatch.From(settings),
        settings.OnlineAi.DefaultProviderId, settings.OnlineAi.Providers.Select(McpProviderView.From).ToArray(), settings.Mcp.Clone(), MediaEngine.UsesBundledTools);
    public static OnlineAiOptions Find(AppSettings settings, string id) => settings.OnlineAi.Providers.Find(p => p.Id == id)
        ?? throw new ArgumentException("供应商不存在：" + id);
}
