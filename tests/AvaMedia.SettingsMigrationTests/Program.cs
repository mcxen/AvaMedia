using System.Text.Json;
using AvaMedia.Core;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var root = Path.Combine(Path.GetTempPath(), "avamedia-settings-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var storage = new Storage(root);
    foreach (var enabled in new[] { false, true })
    foreach (var count in Enumerable.Range(1, 16))
    {
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new { MultiThread = enabled, CpuThreads = count }));
        var settings = storage.LoadSettings();
        Check(settings.ThreadSelection == (enabled ? count : 1), "Legacy thread count changed");
        Check(!settings.AutomaticThreads && settings.CpuThreads == count, "Legacy execution settings changed");
        storage.SaveSettings(settings);
        Check(storage.LoadSettings().ThreadSelection == settings.ThreadSelection, "Thread migration did not survive roundtrip");
    }
    foreach (var check in new[] { false, true })
    foreach (var auto in new[] { false, true })
    foreach (var silent in new[] { false, true })
    {
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new { CheckForUpdates = check, AutoUpdate = auto, SilentUpdate = silent }));
        var settings = storage.LoadSettings();
        Check(settings.UpdateMode == (auto || silent ? 2 : check ? 1 : 0), "Legacy update mapping failed");
        storage.SaveSettings(settings); var roundtrip = storage.LoadSettings();
        Check(roundtrip.CheckForUpdates == check && roundtrip.AutoUpdate == auto && roundtrip.SilentUpdate == silent, "Unedited update flags changed");
    }
    var automatic = new AppSettings(); automatic.SetThreadSelection(0); storage.SaveSettings(automatic);
    Check(storage.LoadSettings().ThreadSelection == 0, "Automatic selection lost");
    Check(automatic.EffectiveCpuThreads == Math.Clamp(Environment.ProcessorCount, 1, 16), "Automatic CPU limit wrong");
    foreach (var mode in Enumerable.Range(0, 3))
    {
        automatic.SetUpdateMode(mode); storage.SaveSettings(automatic);
        var loaded = storage.LoadSettings();
        Check(loaded.UpdateMode == mode && loaded.AutoUpdate == (mode == 2) && loaded.SilentUpdate == (mode == 2), "Update selection roundtrip failed");
    }
    var current = new AppSettings { ExclusiveToolUnlocked = true, FFmpegPath = "hidden", Theme = "Dark", Language = "en-US", EnableBetaFeatures = false };
    var defaults = SettingsPresentation.DefaultsForWindow(current, false);
    Check(defaults.ExclusiveToolUnlocked && defaults.FFmpegPath == "hidden", "Reset changed undisplayed preferences");
    Check(defaults.EnableBetaFeatures && defaults.Theme == new AppSettings().Theme && defaults.Language == "system", "Displayed defaults not reset");
    var copy = current.Clone(); copy.CopyFrom(automatic);
    Check(copy.AutomaticThreads == automatic.AutomaticThreads && copy.UpdateMode == automatic.UpdateMode, "Merged settings not copied");
    File.WriteAllText(Path.Combine(root, "settings.json"), "invalid");
    Check(storage.LoadSettings().EnableBetaFeatures, "Invalid settings fallback failed");
    File.WriteAllText(Path.Combine(root, "settings.json"), "{\"CpuThreads\":\"invalid\"}");
    Check(storage.LoadSettings().AutomaticThreads, "Malformed property fallback failed");
    Console.WriteLine($"Passed {checks} settings migration checks.");
}
finally { Directory.Delete(root, true); }
