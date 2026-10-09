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
        }, () => !_queue.IsRunning,
            enqueue: EnqueueMediaTagJobs,
            showQueue: ShowMediaTagQueue);
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

    private void EnqueueMediaTagJobs(IReadOnlyList<Job> jobs, bool startImmediately)
    {
        if (_closing || jobs.Count == 0) return;
        AddToolJobs(jobs, startImmediately);
        RestoreFromTray();
        JobList.SelectedItems?.Clear();
        foreach (var job in jobs.Where(_jobs.Contains)) JobList.SelectedItems?.Add(job);
        if (jobs.FirstOrDefault(_jobs.Contains) is { } first) JobList.ScrollIntoView(first);
    }

    private void ShowMediaTagQueue()
    {
        if (_closing) return;
        RestoreFromTray(); Activate();
        JobList.SelectedItems?.Clear();
        foreach (var job in _jobs.Where(job => Catalog.Find(job.FeatureId).Operation == Operation.MediaTag))
            JobList.SelectedItems?.Add(job);
        if (JobList.SelectedItems?.Count > 0 && JobList.SelectedItems[0] is Job first)
            JobList.ScrollIntoView(first);
    }

    internal bool CanViewMediaTagResult(Job job) => !_closing && job.State == JobState.Completed
        && Catalog.Find(job.FeatureId).Operation == Operation.MediaTag
        && (File.Exists(job.Output) || job.Inputs.Any(File.Exists));

    internal async Task ShowMediaTagResultAsync(Job job)
    {
        if (!CanViewMediaTagResult(job)) return;
        try
        {
            if (File.Exists(job.Output)) Open(job.Output);
            else await ConfigureMediaAiAsync(job.Inputs);
        }
        catch (Exception error) { await Ui.Message(this, "AI 标签结果", error.Message); }
    }

    private async void ViewMediaTagResultClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (JobList.SelectedItem is Job job) await ShowMediaTagResultAsync(job); }
}
