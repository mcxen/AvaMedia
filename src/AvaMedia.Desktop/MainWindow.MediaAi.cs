using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private MediaAiWindow? _mediaAiWorkspace;
    private Task ConfigureMediaAiAsync(string[]? files)
    {
        if (_mediaAiWorkspace is { } existing)
        {
            existing.ImportPaths(files ?? []); existing.Show(); existing.Activate(); return Task.CompletedTask;
        }
        var window = new MediaAiWindow(Engine, _settings, files, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        },()=>!_queue.IsRunning);
        window.Renamed += mappings =>
        {
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            foreach (var job in _jobs) job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            JobList.ItemsSource = null; JobList.ItemsSource = _jobs; Save(); Refresh();
        };
        _mediaAiWorkspace = window;
        window.Closed += (_, _) => _mediaAiWorkspace = null;
        window.Show(this); return Task.CompletedTask;
    }
}
