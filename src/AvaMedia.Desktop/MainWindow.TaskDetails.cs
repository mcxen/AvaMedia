using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, TaskDetailsWindow> _taskDetailsWindows = [];

    internal bool CanViewTaskDetails(Job job) => !_closing && _jobs.Contains(job);

    internal Task ShowTaskDetailsAsync(Job job, bool showLog = false)
    {
        if (!CanViewTaskDetails(job)) return Task.CompletedTask;
        // Multi-stage tools keep one window for every state, including finished, failed and cancelled runs.
        if (UsesMultiStageWindow(job) && CanViewAiTask(job)) return ShowAiTaskAsync(job);
        if (_taskDetailsWindows.TryGetValue(job.Id, out var existing))
        {
            Controls.WindowChrome.Present(existing);
            if (showLog) existing.ShowLog();
            return Task.CompletedTask;
        }
        var window = new TaskDetailsWindow(job, () => RequestTaskRunAsync(job),
            () => job.State == JobState.Waiting ? CanStartTask(job) : CanContinueTask(job) || CanRequeueTask(job),
            () => EditJob(job), () => CanEditTask(job), Engine,
            job.Options.VlRename is not null ? () => OpenExclusiveRenamerAsync(this, job) : null);
        _taskDetailsWindows[job.Id] = window;
        window.Closed += (_, _) => _taskDetailsWindows.Remove(job.Id);
        Controls.WindowChrome.ShowIndependent(window);
        if (showLog) window.ShowLog();
        return Task.CompletedTask;
    }

    private async void ViewTaskDetailsClick(object? sender, RoutedEventArgs args)
    { if (SelectedJobs() is [var job]) await ShowTaskDetailsAsync(job); }
}
