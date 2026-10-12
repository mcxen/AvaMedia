using Avalonia.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal async Task OpenExclusiveRenamerAsync(Window owner)
    {
        if (!_settings.ExclusiveToolUnlocked) return;
        async Task Configure(Window tool, bool online)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            if (online) settings.OpenOnlineAiSettings(); else settings.OpenModelManagement();
            settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(tool);
        }
        var window = new ExclusiveRenamerWindow(Engine, _settings, Configure, _queue.ReserveFiles);
        window.Renamed += SynchronizeFileChanges;
        await window.ShowDialog(owner);
    }
}
