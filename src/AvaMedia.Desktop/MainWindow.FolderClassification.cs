using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, FolderClassificationWindow> _classificationWindows = [];

    private Task ConfigureFolderClassificationAsync(string[]? files)
    {
        OpenClassificationWindow(files); return Task.CompletedTask;
    }

    private void OpenClassificationWindow(string[]? files = null, Job? task = null)
    {
        if (_closing) return;
        if (task is not null && _classificationWindows.TryGetValue(task.Id, out var existing))
        { existing.Show(); existing.Activate(); return; }
        FolderClassificationWindow? window = null;
        window = new FolderClassificationWindow(Engine, files, () => !_queue.IsRunning, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }, job =>
        {
            foreach (var id in _classificationWindows.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
                _classificationWindows.Remove(id);
            _classificationWindows[job.Id] = window!; AddToolJobs([job], startImmediately: true);
        }, job => RestartTasksAsync([job]), job => { _queue.Stop(job); Save(); Refresh(); },
            () => OpenClassificationWindow(), ShowClassificationTasks, _settings);
        window.Moved += mappings =>
        {
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            foreach (var job in _jobs) job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            JobList.ItemsSource = null; JobList.ItemsSource = _jobs; Save(); Refresh();
        };
        window.Closed += (_, _) =>
        {
            foreach (var id in _classificationWindows.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
                _classificationWindows.Remove(id);
            Save(); Refresh();
        };
        if (task is not null)
        {
            _classificationWindows[task.Id] = window;
            window.Opened += async (_, _) =>
            {
                try { await window.LoadTaskAsync(task); }
                catch (OperationCanceledException) { }
                catch (Exception error) { await Ui.Message(window, "打开分类任务失败", error.Message); }
            };
        }
        window.Show(this);
    }

    private void ShowClassificationTasks()
    {
        if (_closing) return;
        RestoreFromTray(); Activate(); JobList.SelectedItems?.Clear();
        foreach (var job in _jobs.Where(job => job.FeatureId == "folder-classification")) JobList.SelectedItems?.Add(job);
        if (JobList.SelectedItems?.Count > 0 && JobList.SelectedItems[0] is Job first) JobList.ScrollIntoView(first);
    }

    internal bool CanViewClassificationTask(Job job) => !_closing && _jobs.Contains(job)
        && job.FeatureId == "folder-classification" && job.Options.FolderClassification is not null;
    internal Task ShowClassificationTaskAsync(Job job)
    { if (CanViewClassificationTask(job)) OpenClassificationWindow(task: job); return Task.CompletedTask; }
    private async void ViewClassificationTaskClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (JobList.SelectedItem is Job job) await ShowClassificationTaskAsync(job); }
}
