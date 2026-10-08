using System.Text.Json;
using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class FirstRunSetup
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia");
    private static Exception? _failure;
    internal static bool Failed => _failure is not null;

    internal static void TryApplyPending()
    {
        try { ApplyPending(); }
        catch (Exception error)
        {
            _failure = error;
            AppDiagnostics.Record("First-run configuration", error);
            // Preserve the failed attempt for diagnosis without reapplying it on every launch.
            try { File.Move(Path.Combine(Root, "setup-pending.json"), Path.Combine(Root, "setup-failed.json"), true); }
            catch (Exception preserveError) { AppDiagnostics.Record("Preserve failed first-run configuration", preserveError); }
        }
    }

    internal static void AttachFailureNotice(Window window)
    {
        if (_failure is not { } failure) return;
        window.Opened += async (_, _) =>
        {
            try
            {
                await Ui.MessageFormatted(window, "首次配置未完成",
                    $"首次配置未能应用，已使用当前设置进入软件。\n{failure.Message}\n请在选项中重新配置。");
            }
            catch (Exception error) { AppDiagnostics.Record("First-run configuration notice", error); }
        };
    }

    // Native setup runs before the CLR exists. Import its choices before any window loads settings.
    internal static void ApplyPending()
    {
        var path = Path.Combine(Root, "setup-pending.json");
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
        // Settings are committed. A cleanup failure must not turn a successful setup into a startup failure.
        try { File.Delete(path); }
        catch (Exception error) { AppDiagnostics.Record("Remove applied first-run configuration", error); }
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
