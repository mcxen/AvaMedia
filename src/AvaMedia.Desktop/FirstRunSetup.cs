using System.Text.Json;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class FirstRunSetup
{
    // Native setup runs before the CLR exists. Import its choices before any window loads settings.
    internal static void ApplyPending()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia");
        var path = Path.Combine(root, "setup-pending.json");
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var choices = document.RootElement;
        var storage = new Storage();
        var settings = storage.LoadSettings();
        var theme = choices.GetProperty("Theme").GetString();
        if (theme is not ("Light" or "Dark" or "MacOS9" or "WindowsXP"))
            throw new InvalidDataException("首次配置的皮肤无效。");
        var language = choices.GetProperty("Language").GetString();
        if (language is not ("system" or "zh-CN" or "en-US"))
            throw new InvalidDataException("首次配置的语言无效。");
        settings.Theme = theme;
        settings.Language = language;
        settings.OutputFolder = choices.GetProperty("OutputFolder").GetString() ?? "";
        settings.OutputToSource = ReadBoolean(choices, "OutputToSource");
        settings.NotifyComplete = ReadBoolean(choices, "NotifyComplete");
        settings.ReduceMotion = ReadBoolean(choices, "ReduceMotion");
        settings.CheckForUpdates = ReadBoolean(choices, "CheckForUpdates");
        settings.AutoDetectGpu = ReadBoolean(choices, "AutoDetectGpu");
        SettingsPolicy.Validate(settings);
        Directory.CreateDirectory(settings.OutputFolder);
        storage.SaveSettings(settings);
        File.Delete(path);
    }

    private static bool ReadBoolean(JsonElement choices, string name)
    {
        var value = choices.GetProperty(name);
        // Native NSNumber switch values can arrive as JSON 0/1. Accept only those exact integers.
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
            _ => throw new InvalidDataException($"首次配置的开关 {name} 无效。")
        };
    }
}
