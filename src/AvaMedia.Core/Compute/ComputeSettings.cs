using System.Text.Json;

namespace AvaMedia.Core.Compute;

/// <summary>Standalone until AppSettings can be changed without colliding with concurrent work.</summary>
public sealed class ComputeSettings
{
    public ComputePreference Preference { get; set; } = ComputePreference.Auto;
    private static AppSettings? _legacy;
    public static void Configure(AppSettings settings) => _legacy = settings;
    public static ComputePreference CurrentPreference => Load(_legacy?.AutoDetectGpu ?? ReadLegacy()).Preference;
    public ComputeSettings Clone() => new() { Preference = Preference };
    public void CopyFrom(ComputeSettings source) => Preference = source.Preference;
    public static ComputeSettings Load(bool autoDetectGpu = true, IComputeFiles? files = null, string? path = null)
    {
        files ??= new ComputeFiles(); path ??= Path.Combine(ComputeEnvironment.Folder, "compute-settings.json");
        try
        {
            if (files.Read(path) is { } json && JsonSerializer.Deserialize<ComputeSettings>(json, ComputeJson.Options) is { } settings
                && Enum.IsDefined(settings.Preference)) return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { ComputeRuntimeFormatter.Log("计算设置读取失败，使用默认偏好：" + e.Message); }
        return new() { Preference = autoDetectGpu ? ComputePreference.Auto : ComputePreference.ForceCpu };
    }
    public void Save(IComputeFiles? files = null, string? path = null)
        => (files ?? new ComputeFiles()).Write(path ?? Path.Combine(ComputeEnvironment.Folder, "compute-settings.json"), JsonSerializer.Serialize(this, ComputeJson.Options));
    private static bool ReadLegacy()
    {
        try
        {
            var path = Path.Combine(ComputeEnvironment.Folder, "settings.json");
            if (File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("AutoDetectGpu", out var value) && value.ValueKind == JsonValueKind.False) return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return true;
    }
}
