using Avalonia.Interactivity;
using AvaMedia.Core;
using AvaMedia.Desktop.Notifications;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal bool CanExitForUpdate => !_closing && !_queue.IsRunning && _editingJob is null;
    private void NotificationsClick(object? sender, RoutedEventArgs args) => NotificationCenter.Shared.OpenHistory(this);

    internal async Task OpenModelManagementAsync()
    {
        if (_closing) return;
        if (!IsVisible) RestoreFromTray();
        var window = new SettingsWindow(_settings, _optionServices); window.OpenModelManagement();
        window.Applied += (_, _) => ApplyOptions(); await window.ShowDialog<bool>(this);
    }
    private bool CanRetryNotification(Job[] jobs) => !_closing && !_queue.IsRunning && _editingJob is null
        && jobs.Any(job => _jobs.Contains(job) && (job.CanRetry || job.State == JobState.Completed));

    private async Task RetryNotificationAsync(Job[] jobs)
    {
        if (!CanRetryNotification(jobs)) return;
        foreach (var job in jobs.Where(job => _jobs.Contains(job) && (job.CanRetry || job.State == JobState.Completed)))
        {
            var directory = Catalog.DirectoryOutput(Catalog.Find(job.FeatureId).Operation);
            var name = directory ? Path.GetFileName(job.Output) : Path.GetFileNameWithoutExtension(job.Output);
            var output = MediaEngine.UniqueOutput(Path.GetDirectoryName(job.Output)!, name, job.Options.Format, EditingReservations(job), directory);
            ResetTask(job); job.Output = output;
        }
        Save(); Refresh(); await StartQueueAsync();
    }
    private Task FocusJobsAsync(Job[] jobs)
    {
        if (_closing) return Task.CompletedTask;
        RestoreFromTray(); JobList.SelectedItems?.Clear();
        foreach (var job in jobs.Where(_jobs.Contains)) JobList.SelectedItems?.Add(job);
        if (jobs.FirstOrDefault(_jobs.Contains) is { } first) JobList.ScrollIntoView(first);
        return Task.CompletedTask;
    }
    private Task OpenNotificationOutputAsync(string[] folders)
    {
        foreach (var folder in folders.Where(Directory.Exists)) _optionServices.OpenFolder(folder);
        return Task.CompletedTask;
    }
    private void NotifyQueueResults(Job[] batch)
    {
        var completion = QueueCompletion.From(batch); var id = Guid.NewGuid().ToString("N");
        foreach (var job in batch.Where(job => job.State == JobState.Completed && Catalog.Find(job.FeatureId).Operation == Operation.VideoSummary))
        {
            var item = job;
            NotificationCenter.Shared.Publish(this, new("summary:" + id + ":" + item.Id, "视频总结已完成", (FormattableString)$"{Path.GetFileName(item.Inputs.FirstOrDefault())}",
                NotificationKind.Success, [
                    new("查看结果", () => { RestoreFromTray(); return ShowSummaryResultAsync(item); }, Primary: true, Enabled: () => !_closing),
                    new("打开目录", () => OpenNotificationOutputAsync([item.Output])),
                    new("重新处理", () => RetryNotificationAsync([item]), Enabled: () => CanRetryNotification([item]))]));
        }
        var failed = batch.Where(job => job.State == JobState.Failed).ToArray();
        foreach (var job in failed)
        {
            var item = job;
            var failureActions = new List<NotificationAction> {
                    new("重试任务", () => RetryNotificationAsync([item]), Primary: true, Enabled: () => CanRetryNotification([item])),
                    new("查看任务", () => FocusJobsAsync([item])) };
            if (Catalog.Find(item.FeatureId).Operation is Operation.VideoSummary or Operation.Transcribe) failureActions.Add(new("模型管理", ModelNotifications.OpenManagementAsync));
            NotificationCenter.Shared.Publish(this, new("task-failure:" + id + ":" + item.Id, "任务执行失败",
                (FormattableString)$"{Path.GetFileName(item.Inputs.FirstOrDefault())}\n{item.Error}", NotificationKind.Error, failureActions));
        }
        if (batch.All(job => job.State == JobState.Completed && Catalog.Find(job.FeatureId).Operation == Operation.VideoSummary)) return;
        var actions = new List<NotificationAction> { new("查看任务", () => FocusJobsAsync(batch), Primary: true),
            new("打开输出目录", () => OpenNotificationOutputAsync(completion.OutputFolders.ToArray())) };
        if (failed.Length > 0) actions.Add(new("重试失败任务", () => RetryNotificationAsync(failed), Enabled: () => CanRetryNotification(failed)));
        NotificationCenter.Shared.Publish(this, new("queue:" + id, "任务执行完成", CompletionMessage(completion),
            failed.Length > 0 ? NotificationKind.Warning : NotificationKind.Success, actions));
    }
}
