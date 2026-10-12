using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed partial class AppSettings
{
    // The legacy fields remain the execution settings used by all media tools.
    public bool AutomaticThreads { get; set; } = true;
    public string AccentColor { get; set; } = "Blue";
    public DateTimeOffset? LastUpdateCheck { get; set; }
    [JsonIgnore] public int ThreadSelection => !MultiThread ? 1 : AutomaticThreads ? 0 : CpuThreads;
    [JsonIgnore] public int EffectiveCpuThreads => AutomaticThreads ? Math.Clamp(Environment.ProcessorCount, 1, 16) : CpuThreads;
    [JsonIgnore] public int UpdateMode => AutoUpdate || SilentUpdate ? 2 : CheckForUpdates ? 1 : 0;

    public void SetThreadSelection(int threads)
    {
        if (threads is < 0 or > 16) throw new ArgumentOutOfRangeException(nameof(threads));
        AutomaticThreads = threads == 0; MultiThread = threads != 1;
        CpuThreads = threads == 0 ? EffectiveCpuThreads : threads;
    }
    public void SetUpdateMode(int mode)
    {
        if (mode is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        CheckForUpdates = mode != 0; AutoUpdate = mode == 2; SilentUpdate = mode == 2;
    }
}

public static class SettingsPresentation
{
    public static AppSettings Deserialize(JsonElement json)
    {
        var settings = json.Deserialize<AppSettings>() ?? new();
        // Missing metadata means an existing settings.json; retain its explicit thread limit.
        if (json.ValueKind == JsonValueKind.Object && !json.TryGetProperty(nameof(AppSettings.AutomaticThreads), out _))
            settings.AutomaticThreads = false;
        if (settings.AutomaticThreads) settings.CpuThreads = settings.EffectiveCpuThreads;
        return settings;
    }

    public static AppSettings DefaultsForWindow(AppSettings current, bool externalToolsVisible)
    {
        var defaults = new AppSettings();
        // Activation is an action, not a resettable preference. Hidden bundled tool paths are not displayed.
        defaults.ExclusiveToolUnlocked = current.ExclusiveToolUnlocked;
        defaults.LastUpdateCheck = current.LastUpdateCheck;
        if (!externalToolsVisible)
        {
            defaults.FFmpegPath = current.FFmpegPath;
            defaults.FFprobePath = current.FFprobePath;
            defaults.YtDlpPath = current.YtDlpPath;
        }
        return defaults;
    }
}
