using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, FolderClassificationWindow> _classificationWindows = [];

    private readonly Dictionary<Guid, Classification.ClassificationRunWindow> _classificationRuns = [];

    private Task ConfigureFolderClassificationAsync(string[]? files)
    {
        if (_closing) return Task.CompletedTask;
        var submit = new Classification.ClassificationSubmitWindow(Engine, _settings, files, job =>
        {
            AddToolJobs([job], startImmediately: true); OpenClassificationRun(job);
        }, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }, ShowClassificationTasks);
        submit.Show(this); return Task.CompletedTask;
    }

    /// <summary>Full rule editor (custom groups, manual review); opened from the compact submit window.</summary>
    internal void OpenAdvancedClassification(string[] files) => OpenClassificationWindow(files.Length == 0 ? null : files);

    private void OpenClassificationRun(Job job)
    {
        if (_closing) return;
        if (_classificationRuns.TryGetValue(job.Id, out var existing)) { existing.Show(); existing.Activate(); return; }
        var run = new Classification.ClassificationRunWindow(job, PauseTask, task => { _queue.Stop(task); Save(); Refresh(); },
            (task, action) => OpenClassificationWindow(task: task, action: action));
        _classificationRuns[job.Id] = run;
        run.Closed += (_, _) => _classificationRuns.Remove(job.Id);
        run.Show(this);
    }

    private void OpenClassificationWindow(string[]? files = null, Job? task = null, string? action = null)
    {
        if (_closing) return;
        if (task is not null && _classificationWindows.TryGetValue(task.Id, out var existing))
        {
            existing.Show(); existing.Activate();
            if (action is not null) _ = existing.RunTaskActionAsync(action);
            return;
        }
        FolderClassificationWindow? window = null;
        window = new FolderClassificationWindow(Engine, files, () => !_closing, async owner =>
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }, job =>
        {
            foreach (var id in _classificationWindows.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
                _classificationWindows.Remove(id);
            _classificationWindows[job.Id] = window!; AddToolJobs([job], startImmediately: true);
        }, RequestTaskRunAsync, job => { _queue.Stop(job); Save(); Refresh(); },
            () => OpenClassificationWindow(), ShowClassificationTasks, _settings, pauseTask: PauseTask, reserveFiles: _queue.ReserveFiles);
        window.Moved += SynchronizeFileChanges;
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
                try
                {
                    await window.LoadTaskAsync(task);
                    if (action is not null) await window.RunTaskActionAsync(action);
                    if (_classificationRuns.TryGetValue(task.Id, out var run)) run.Invalidate();
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { await Ui.Message(window, "打开分类任务失败", error.Message); }
            };
        }
        window.Show(this);
    }

    private void ShowClassificationTasks()
    {
        if (_closing) return;
        RestoreFromTray(); Activate();
        SelectTaskRows(_jobs.Where(job => job.FeatureId == "folder-classification"));
    }

    internal bool CanViewClassificationTask(Job job) => !_closing && _jobs.Contains(job)
        && job.FeatureId == "folder-classification" && job.Options.FolderClassification is not null;
    internal Task ShowClassificationTaskAsync(Job job)
    { if (CanViewClassificationTask(job)) OpenClassificationRun(job); return Task.CompletedTask; }
}
