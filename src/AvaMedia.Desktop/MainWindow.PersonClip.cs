using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigurePersonClipAsync(string[]? files)
    {
        async Task ManageModels(Window owner, string? modelId)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(modelId);
            settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }
        var edits = await new PersonClipWindow(Engine, _settings, files, ManageModels).ShowDialog<IReadOnlyList<ClipEditResult>?>(this);
        if (edits is null || edits.Count == 0 || !_settings.EnableBetaFeatures) return;
        await EditQuickClipAsync(edits.Select(edit => edit.Path), edits, allowJoin: true);
    }
}
