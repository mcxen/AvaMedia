using Avalonia.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal void UnlockExclusiveTools()
    {
        if (_settings.ExclusiveToolUnlocked) { ShowCategory("视频"); return; }
        try { _settings.ExclusiveToolUnlocked = true; ApplyOptions(); }
        catch { _settings.ExclusiveToolUnlocked = false; throw; }
    }

    internal async Task OpenExclusiveRenamerAsync(Window owner)
    {
        if (!_settings.ExclusiveToolUnlocked) return;
        async Task Configure(Window tool, bool online)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            if (online) settings.OpenOnlineAiSettings(); else settings.OpenModelManagement();
            settings.Applied += (_, _) => ApplyOptions();
            await Controls.WindowChrome.ShowIndependentAsync(settings);
        }
        var window = new ExclusiveRenamerWindow(Engine, _settings, Configure, _queue.ReserveFiles);
        window.Renamed += SynchronizeFileChanges;
        await Controls.WindowChrome.ShowIndependentAsync(window);
    }
}
