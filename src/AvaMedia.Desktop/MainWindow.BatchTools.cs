using AvaMedia.Core;

namespace AvaMedia.Desktop;
public partial class MainWindow
{
    private Task ConfigureBatchToolsAsync(string[]? files, bool screenshots)
    {
        async Task ManageModels(Avalonia.Controls.Window owner)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement();
            settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }
        if (screenshots)
        {
            new ContactSheetWindow(Engine, _settings.OutputFolder, files).Show(this);
            return Task.CompletedTask;
        }
        var window = new RenameWindow(Engine, _settings, files, manageModels: ManageModels, reserveFiles: _queue.ReserveFiles);
        window.Renamed += mappings =>
        {
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            foreach (var job in _jobs) job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            Save(); Refresh();
        };
        window.Show(this);
        return Task.CompletedTask;
    }
}
