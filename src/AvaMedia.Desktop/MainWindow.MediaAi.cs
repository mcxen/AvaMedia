using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal Task ConfigureMediaAiAsync(string[]? files)
    {
        CreateMediaAiWindow(files); return Task.CompletedTask;
    }

    private MediaAiWindow CreateMediaAiWindow(string[]? files)
    {
        var window = new MediaAiWindow(Engine, _settings, files, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }, () => !_queue.IsRunning,
            enqueue: EnqueueMediaTagJobs, showQueue: ShowMediaTagQueue,
            stopTask: job => { _queue.Stop(job); Save(); Refresh(); }, newTask: () => { _ = ConfigureMediaAiAsync(null); });
        window.Renamed += mappings =>
        {
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            foreach (var job in _jobs) job.Inputs = job.Inputs.Select(path => map.GetValueOrDefault(path) ?? path).ToArray();
            Save(); Refresh();
        };
        window.Show(this); return window;
    }

    private void EnqueueMediaTagJobs(IReadOnlyList<Job> jobs, bool startImmediately)
    {
        if (_closing || jobs.Count == 0) return;
        AddToolJobs(jobs, startImmediately);
        RestoreFromTray();
        SelectTaskRows(jobs);
    }

    private void ShowMediaTagQueue()
    {
        if (_closing) return;
        RestoreFromTray(); Activate();
        SelectTaskRows(_jobs.Where(job => Catalog.Find(job.FeatureId).Operation == Operation.MediaTag));
    }

    internal bool CanViewMediaTagResult(Job job) => !_closing && HasAiResult(job)
        && Catalog.Find(job.FeatureId).Operation == Operation.MediaTag
        && (File.Exists(job.Output) || job.Inputs.Any(File.Exists));

    internal async Task ShowMediaTagResultAsync(Job job)
    {
        if (!CanViewMediaTagResult(job)) return;
        try
        {
            var window = CreateMediaAiWindow(job.Inputs);
            await window.ObserveTaskAsync(job);
        }
        catch (Exception error) { await Ui.Message(this, "AI 标签结果", error.Message); }
    }

    private async void ViewMediaTagResultClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (JobList.SelectedItem is Job job) await ShowMediaTagResultAsync(job); }
}
