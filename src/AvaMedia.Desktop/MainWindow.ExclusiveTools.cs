using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, ExclusiveRenamerWindow> _vlRenameWindows = [];
    internal void UnlockExclusiveTools()
    {
        if (_settings.ExclusiveToolUnlocked) { ShowCategory("视频"); return; }
        try { _settings.ExclusiveToolUnlocked = true; ApplyOptions(); }
        catch { _settings.ExclusiveToolUnlocked = false; throw; }
    }

    internal async Task OpenExclusiveRenamerAsync(Window owner, Job? analysis = null)
    {
        if (!_settings.ExclusiveToolUnlocked) return;
        if (analysis is not null && _vlRenameWindows.TryGetValue(analysis.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Show(); existing.Activate(); return;
        }
        if (analysis is not null)
            analysis.VlRenameResult ??= await AiTaskResults.LoadAsync<VlRenameTaskResult>(analysis.Output);
        if (_closing) return;
        async Task Configure(Window tool, bool online)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            if (online) settings.OpenOnlineAiSettings(); else settings.OpenModelManagement();
            settings.Applied += (_, _) => ApplyOptions();
            await Controls.WindowChrome.ShowIndependentAsync(settings);
        }
        async Task Submit(Job job)
        {
            if (_closing || _queue.IsStopping) throw new InvalidOperationException("任务队列正在停止，请稍后提交。");
            MediaEngine.Validate(job); AddToolJobs([job], true); await PersistenceReady;
        }
        async Task<Job> Commit(Job source, RenameItem[] plan)
        {
            var job = VlRenameTasks.CreateCommit(plan);
            var spec = source.Options.VlRename!; var previous = spec.RenameTaskId;
            spec.RenameTaskId = job.Id;
            try { await Submit(job); }
            catch { spec.RenameTaskId = previous; Save(); throw; }
            return job;
        }
        var window = new ExclusiveRenamerWindow(_settings, Submit, Commit, StopRowTask,
            job => ShowTaskDetailsAsync(job), Save, Configure, _queue.ReserveFiles);
        window.Renamed += SynchronizeFileChanges;
        window.TaskAttached += job =>
        {
            foreach (var id in _vlRenameWindows.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
                _vlRenameWindows.Remove(id);
            _vlRenameWindows[job.Id] = window;
        };
        window.Closed += (_, _) =>
        {
            foreach (var id in _vlRenameWindows.Where(pair => ReferenceEquals(pair.Value, window)).Select(pair => pair.Key).ToArray())
                _vlRenameWindows.Remove(id);
        };
        if (analysis is not null)
            window.AttachTask(analysis, _jobs.FirstOrDefault(job => job.Id == analysis.Options.VlRename?.RenameTaskId));
        await Controls.WindowChrome.ShowIndependentAsync(window);
    }
}
