using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigureFolderClassificationAsync(string[]? files)
    {
        var window = new FolderClassificationWindow(Engine, files, () => !_queue.IsRunning, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }, _settings);
        window.Moved += mappings =>
        {
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            foreach (var job in _jobs) job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            JobList.ItemsSource = null; JobList.ItemsSource = _jobs; Save(); Refresh();
        };
        await window.ShowDialog(this);
    }
}
