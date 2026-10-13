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
            await Controls.WindowChrome.ShowIndependentAsync(settings);
        }
        if (screenshots)
        {
            Controls.WindowChrome.ShowIndependent(new ContactSheetWindow(Engine, _settings.OutputFolder, files));
            return Task.CompletedTask;
        }
        var window = new RenameWindow(Engine, _settings, files, manageModels: ManageModels, reserveFiles: _queue.ReserveFiles);
        window.Renamed += SynchronizeFileChanges;
        Controls.WindowChrome.ShowIndependent(window);
        return Task.CompletedTask;
    }
}
