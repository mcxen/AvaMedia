using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private bool CanManageTasks => !_closing && _editingJob is null;
    private Job[] SelectedJobs() => _jobs.Where(job => JobList.SelectedItems?.Contains(job) == true).ToArray();
    private bool CanStartTask(Job job) => CanManageTasks && !_queue.IsStopping && job.State == JobState.Waiting && !_queue.IsScheduled(job);
    private bool CanRemoveTask(Job job) => CanManageTasks && _jobs.Contains(job) && !_queue.IsExecuting(job) && job.State != JobState.Stopping;
    private bool CanRequeueTask(Job job) => CanManageTasks && !_queue.IsStopping && !_queue.IsExecuting(job)
        && job.State is JobState.Paused or JobState.Cancelled or JobState.Failed or JobState.Completed;
    private bool CanMoveTask(Job job) => CanManageTasks && !_queue.IsExecuting(job) && job.State is JobState.Waiting or JobState.Paused;

    private void InitializeTaskManagement()
    {
        JobList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(JobList).Properties.IsRightButtonPressed || e.Source is not Control source) return;
            var row = source.GetVisualAncestors().Prepend(source).OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is not Job job) { JobList.SelectedItems?.Clear(); return; }
            if (JobList.SelectedItems?.Contains(job) == true) return;
            JobList.SelectedItems?.Clear(); JobList.SelectedItems?.Add(job);
            JobList.Focus(NavigationMethod.Pointer);
        }, RoutingStrategies.Tunnel);
    }

    private void UpdateTaskManagementActions()
    {
        var selected = SelectedJobs();
        StartTaskMenu.IsVisible = selected.Any(CanStartTask);
        ContinueTaskMenu.IsVisible = selected.Any(job => CanRequeueTask(job) && job.State is JobState.Paused or JobState.Cancelled);
        ContinueTaskMenu.Header = Localization.Text(selected.Any(job => job.State == JobState.Cancelled && job.FeatureId != "download")
            ? "继续任务（重新执行）" : "继续任务");
        PauseTaskMenu.IsVisible = selected.Any(job => CanManageTasks && job.State == JobState.Waiting && !_queue.IsExecuting(job));
        TerminateTaskMenu.IsVisible = selected.Any(job => CanManageTasks && job.State is JobState.Waiting or JobState.Running or JobState.Paused);
        RetryTaskMenu.IsVisible = selected.Any(job => CanRequeueTask(job) && job.State == JobState.Failed);
        RerunTaskMenu.IsVisible = selected.Any(job => CanRequeueTask(job) && job.State == JobState.Completed);
        TaskExecutionSeparator.IsVisible = StartTaskMenu.IsVisible || ContinueTaskMenu.IsVisible || PauseTaskMenu.IsVisible
            || TerminateTaskMenu.IsVisible || RetryTaskMenu.IsVisible || RerunTaskMenu.IsVisible;
        EditTaskContextMenu.IsVisible = DuplicateTaskMenu.IsVisible = TaskFilesSeparator.IsVisible = selected.Length > 0;
        DuplicateTaskMenu.IsEnabled = CanManageTasks && selected.Length > 0;
        var moving = selected.Where(CanMoveTask).ToHashSet();
        ReorderTaskMenu.IsVisible = moving.Count > 0;
        MoveTaskTopMenu.IsEnabled = MoveTaskUpMenu.IsEnabled = moving.Any(job => _jobs.IndexOf(job) > 0 && !moving.Contains(_jobs[_jobs.IndexOf(job) - 1]));
        MoveTaskBottomMenu.IsEnabled = MoveTaskDownMenu.IsEnabled = moving.Any(job => _jobs.IndexOf(job) < _jobs.Count - 1 && !moving.Contains(_jobs[_jobs.IndexOf(job) + 1]));
        var one = selected.Length == 1 ? selected[0] : null;
        ViewSummaryResultMenu.IsVisible = one is not null && CanViewSummaryResult(one);
        PlayOutputMenu.IsVisible = SelectedPlayableOutputs().Length > 0;
        OpenTaskSourceMenu.IsVisible = OpenTaskSourceFolderMenu.IsVisible = OpenTaskOutputMenu.IsVisible
            = OpenTaskOutputFolderMenu.IsVisible = CopyTaskPathsMenu.IsVisible = TaskLogMenu.IsVisible
            = RemoveTaskMenu.IsVisible = TaskRemovalSeparator.IsVisible = selected.Length > 0;
        OpenTaskSourceMenu.IsEnabled = one?.Inputs.Any(IsAccessibleSource) == true;
        OpenTaskSourceFolderMenu.IsEnabled = one?.Inputs.Any(path => File.Exists(path) || Directory.Exists(path)) == true;
        OpenTaskOutputMenu.IsEnabled = one?.State == JobState.Completed && (File.Exists(one.Output) || Directory.Exists(one.Output));
        OpenTaskOutputFolderMenu.IsEnabled = one is not null && Directory.Exists(Path.GetDirectoryName(one.Output));
        CopyTaskPathsMenu.IsEnabled = selected.Length > 0;
        TaskLogMenu.IsEnabled = one is not null;
        RemoveTaskMenu.IsEnabled = selected.Any(CanRemoveTask);
        ClearCompletedTasksMenu.IsEnabled = CanManageTasks && _jobs.Any(job => job.State == JobState.Completed && CanRemoveTask(job));
        SelectAllTasksMenu.IsEnabled = _jobs.Count > selected.Length;
        DeselectTasksMenu.IsEnabled = selected.Length > 0;
    }

    private async void StartSelectedClick(object? sender, RoutedEventArgs args)
    {
        try { await StartQueueAsync(SelectedJobs().Where(CanStartTask).ToArray()); }
        catch (Exception error) { await Ui.Message(this, "开始任务", error.Message); }
    }

    private async void ContinueSelectedClick(object? sender, RoutedEventArgs args)
    {
        try { await RestartTasksAsync(SelectedJobs().Where(job => job.State is JobState.Paused or JobState.Cancelled).ToArray()); }
        catch (Exception error) { await Ui.Message(this, "继续任务", error.Message); }
    }

    private async void RerunSelectedClick(object? sender, RoutedEventArgs args)
    {
        try { await RestartTasksAsync(SelectedJobs().Where(job => job.State == JobState.Completed).ToArray()); }
        catch (Exception error) { await Ui.Message(this, "重新执行", error.Message); }
    }

    private void PauseSelectedClick(object? sender, RoutedEventArgs args)
    {
        if (!CanManageTasks) return;
        foreach (var job in SelectedJobs()) _queue.PauseQueued(job);
        Save(); Refresh();
    }

    private void TerminateSelectedClick(object? sender, RoutedEventArgs args)
    {
        if (!CanManageTasks) return;
        foreach (var job in SelectedJobs()) _queue.Stop(job);
        Save(); Refresh();
    }

    private string NextTaskOutput(Job job, bool reserveOriginal = false)
    {
        var directory = Catalog.DirectoryOutput(Catalog.Find(job.FeatureId).Operation);
        var name = directory ? Path.GetFileName(job.Output) : Path.GetFileNameWithoutExtension(job.Output);
        var reserved = EditingReservations(job).Concat(_jobs.SelectMany(item => item.Inputs));
        if (reserveOriginal) reserved = reserved.Append(job.Output);
        return MediaEngine.UniqueOutput(Path.GetDirectoryName(job.Output)!, name, job.Options.Format, reserved, directory);
    }

    private async Task RestartTasksAsync(Job[] jobs)
    {
        var restarting = jobs.Where(job => _jobs.Contains(job) && CanRequeueTask(job)).ToArray();
        if (restarting.Length == 0) return;
        foreach (var job in restarting)
        {
            // Keep the job ID for download staging and allocate a fresh destination if a prior output exists.
            var output = NextTaskOutput(job); ResetTask(job); job.Output = output;
        }
        Save(); Refresh(); await StartQueueAsync(restarting);
    }

    private async void DuplicateSelectedClick(object? sender, RoutedEventArgs args)
    {
        if (!CanManageTasks) return;
        try
        {
            var copies = new List<Job>();
            foreach (var original in SelectedJobs())
            {
                var copy = new Job { FeatureId = original.FeatureId, Inputs = original.Inputs.ToArray(), Options = original.Options.Clone(),
                    InputOptions = original.InputOptions?.Select(options => options.Clone()).ToList(),
                    Output = NextTaskOutput(original, reserveOriginal: true), DownloadTitle = original.DownloadTitle, Duration = original.Duration };
                _jobs.Insert(_jobs.IndexOf(original) + 1, copy); copies.Add(copy);
            }
            JobList.SelectedItems?.Clear();
            foreach (var copy in copies) JobList.SelectedItems?.Add(copy);
            Save(); Refresh();
        }
        catch (Exception error) { await Ui.Message(this, "复制任务", error.Message); }
    }

    private void MoveTasks(int direction)
    {
        var selected = SelectedJobs().Where(CanMoveTask).ToArray(); var set = selected.ToHashSet();
        if (direction == -2) foreach (var job in selected.Reverse()) _jobs.Move(_jobs.IndexOf(job), 0);
        else if (direction == 2) foreach (var job in selected) _jobs.Move(_jobs.IndexOf(job), _jobs.Count - 1);
        else if (direction < 0)
        {
            for (var index = 1; index < _jobs.Count; index++)
                if (set.Contains(_jobs[index]) && !set.Contains(_jobs[index - 1])) _jobs.Move(index, index - 1);
        }
        else
        {
            for (var index = _jobs.Count - 2; index >= 0; index--)
                if (set.Contains(_jobs[index]) && !set.Contains(_jobs[index + 1])) _jobs.Move(index, index + 1);
        }
        _queue.ReorderPending(_jobs); Save(); Refresh();
    }
    private void MoveTaskTopClick(object? sender, RoutedEventArgs args) => MoveTasks(-2);
    private void MoveTaskUpClick(object? sender, RoutedEventArgs args) => MoveTasks(-1);
    private void MoveTaskDownClick(object? sender, RoutedEventArgs args) => MoveTasks(1);
    private void MoveTaskBottomClick(object? sender, RoutedEventArgs args) => MoveTasks(2);

    private async Task RemoveTasksAsync(IEnumerable<Job> jobs)
    {
        var removed = jobs.Distinct().Where(job => CanRemoveTask(job) && _queue.Withdraw(job)).ToArray();
        if (removed.Length == 0) return;
        foreach (var job in removed) _jobs.Remove(job);
        Save(); Refresh(); await _queueSave; await _storage.DeleteJobLogsAsync(removed);
    }
    private async void ClearCompletedTasksClick(object? sender, RoutedEventArgs args)
    {
        try { await RemoveTasksAsync(_jobs.Where(job => job.State == JobState.Completed).ToArray()); }
        catch (Exception error) { await Ui.Message(this, "清理已完成任务", error.Message); }
    }
    private void SelectAllTasksClick(object? sender, RoutedEventArgs args) => JobList.SelectAll();
    private void DeselectTasksClick(object? sender, RoutedEventArgs args) => JobList.SelectedItems?.Clear();

    private static bool IsAccessibleSource(string path) => File.Exists(path) || Directory.Exists(path)
        || Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    private async void OpenSourceSelectedClick(object? sender, RoutedEventArgs args)
    {
        if (SelectedJobs() is not [var job]) return;
        try { foreach (var path in job.Inputs.Where(IsAccessibleSource).Distinct()) Open(path); }
        catch (Exception error) { await Ui.Message(this, "打开来源", error.Message); }
    }
    private async void OpenSourceFolderSelectedClick(object? sender, RoutedEventArgs args)
    {
        if (SelectedJobs() is not [var job]) return;
        try
        {
            foreach (var folder in job.Inputs.Where(path => File.Exists(path) || Directory.Exists(path))
                .Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path)!).Distinct()) Open(folder);
        }
        catch (Exception error) { await Ui.Message(this, "打开源文件目录", error.Message); }
    }
    private async void CopyTaskPathsClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, SelectedJobs().SelectMany(job => job.Inputs.Append(job.Output)).Distinct()));
        }
        catch (Exception error) { await Ui.Message(this, "复制文件路径", error.Message); }
    }
}
