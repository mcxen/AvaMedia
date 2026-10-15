using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, FolderClassificationWindow> _classificationWindows = [];

    private readonly Dictionary<Guid, Classification.ClassificationRunWindow> _classificationRuns = [];

    private Task ConfigureFolderClassificationAsync(string[]? files)
        => ConfigureLifecycleEntryAsync(Catalog.Find("folder-classification"), files);

    private void OpenClassificationRun(Job job)
    {
        if (_closing) return;
        if (_classificationRuns.TryGetValue(job.Id, out var existing)) { Controls.WindowChrome.Present(existing); return; }
        var run = new Classification.ClassificationRunWindow(job,
            (task, action) => OpenClassificationWindow(task: task, action: action));
        _classificationRuns[job.Id] = run;
        run.Closed += (_, _) => _classificationRuns.Remove(job.Id);
        Controls.WindowChrome.ShowIndependent(run);
    }

    private void OpenClassificationWindow(string[]? files = null, Job? task = null, string? action = null)
    {
        if (_closing) return;
        if (task is not null && _classificationWindows.TryGetValue(task.Id, out var existing))
        {
            Controls.WindowChrome.Present(existing);
            if (action is not null) _ = existing.RunTaskActionAsync(action);
            return;
        }
        FolderClassificationWindow? window = null;
        window = new FolderClassificationWindow(Engine, files, () => !_closing, async owner =>
        {
            await ShowSettingsAsync(owner, settings => settings.OpenModelManagement());
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
        Controls.WindowChrome.ShowIndependent(window);
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
    {
        if (!CanViewClassificationTask(job)) return Task.CompletedTask;
        // Running classification is followed in the multi-stage window; the run window reviews finished results.
        if (Active(job)) return ShowAiTaskAsync(job);
        OpenClassificationRun(job); return Task.CompletedTask;
    }
}
