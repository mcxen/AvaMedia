using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private IMultiStageQueueHost? _multiStageQueueHost;
    internal IMultiStageQueueHost MultiStageQueueHost => _multiStageQueueHost ??= new DesktopMultiStageQueueHost(this);

    private sealed class DesktopMultiStageQueueHost(MainWindow owner) : IMultiStageQueueHost
    {
        private static T Ui<T>(Func<T> action) => Dispatcher.UIThread.CheckAccess() ? action()
            : Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        private static Task UiAsync(Func<Task> action) => Dispatcher.UIThread.CheckAccess() ? action()
            : Dispatcher.UIThread.InvokeAsync(action);
        public bool CanStart(Job job) => Ui(() => owner._jobs.Contains(job) && owner.CanStartTask(job));
        public Task StartAsync(Job job) => UiAsync(() => CanStart(job) ? owner.RequestTaskRunAsync(job) : Task.CompletedTask);
        public bool CanPause(Job job) => Ui(() => owner.CanPauseRowTask(job));
        public void Pause(Job job) => Ui(() => { if (CanPause(job)) owner.PauseTask(job); return true; });
        public bool CanResume(Job job) => Ui(() => owner._jobs.Contains(job) && owner.CanManageTasks && !owner._editingJobs.Contains(job) && job.State == JobState.Paused);
        public Task ResumeAsync(Job job) => UiAsync(() => CanResume(job) ? owner.RequestTaskRunAsync(job) : Task.CompletedTask);
        public bool CanStop(Job job) => Ui(() => owner.CanStopRowTask(job));
        public void Stop(Job job) => Ui(() => { owner.StopRowTask(job); return true; });
        public bool CanRerun(Job job) => Ui(() => owner._jobs.Contains(job) && owner.CanRequeueTask(job) && job.State is JobState.Completed or JobState.Failed or JobState.Cancelled);
        public Task RerunAsync(Job job, string? fromStageId) => UiAsync(() =>
        {
            if (!CanRerun(job)) return Task.CompletedTask;
            if (fromStageId is not null && (job.FeatureId != "person-clip"
                || fromStageId is not (PersonClipStageIds.Detect or PersonClipStageIds.Refine or PersonClipStageIds.Export)))
                throw new ArgumentException("不支持从该阶段重试。", nameof(fromStageId));
            return owner.StartToolWorkflow(() => owner.RestartTasksAsync([job], fromStageId));
        });
        public bool CanChangeOutput(Job job) => Ui(() => CanStart(job) && !job.HasInternalOutput);
        public Task ChangeOutputFolderAsync(Job job, string folder) => UiAsync(() =>
        {
            if (!CanChangeOutput(job)) return Task.CompletedTask;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) throw new ArgumentException("请选择有效的输出文件夹。", nameof(folder));
            var replacement = new Job { FeatureId = job.FeatureId, Inputs = job.Inputs.ToArray(), Options = job.Options.Clone(),
                Duration = job.Duration, Output = Path.Combine(Path.GetFullPath(folder), Path.GetFileName(job.Output)) };
            owner.ApplyEditedJobs(job, [replacement]);
            return Task.CompletedTask;
        });
    }
}
