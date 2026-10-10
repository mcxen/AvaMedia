using System.ComponentModel;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private readonly Action<Job> _enqueueTask;
    private readonly Func<Job, Task> _resumeTask;
    private readonly Action<Job> _stopTask;
    private readonly Action _newTask, _showTasks;
    private Job? _taskJob;
    private FolderClassificationTaskSnapshot? _seenSnapshot;
    private readonly Dictionary<string, FolderClassificationTaskFile> _seenFiles = new(BatchRename.PathComparer);
    private bool _loadingTask, _closingView, _savingClose;
    private int _taskRefreshPosted;
    private Task _viewSave = Task.CompletedTask;
    private bool TaskActive => _taskJob?.State is JobState.Waiting or JobState.Paused or JobState.Running or JobState.Stopping;

    private FolderClassificationTaskOptions CaptureTaskOptions(MediaTagOptions? analysis = null) => new()
    {
        Rules = _rules.ToArray(),
        Analysis = analysis ?? new((int)(_frames.Value ?? 12), _gpu.IsChecked == true),
        TagThreshold = (double)(_tagThreshold.Value ?? .5m), IncludeNsfw = _settings.EnableNsfwContent,
        OutputFolder = _output.Text ?? "", SplitTypes = _splitTypes.IsChecked == true, WriteText = _writeText.IsChecked == true,
        Move = _mode.SelectedIndex == 1, ExcludedPaths = _entries.Where(entry => !entry.Include).Select(entry => entry.Path).ToArray(), LastJournal = _lastJournal
    };

    public async Task LoadTaskAsync(Job job)
    {
        _loadingTask = true; SetBusy(true);
        try
        {
            var spec = job.Options.FolderClassification ?? throw new ArgumentException("缺少分类任务参数。");
            _syncing = true;
            try
            {
                _rules.Clear(); _disabledNsfwRules.Clear();
                foreach (var rule in spec.Rules) if (RuleVisible(rule)) _rules.Add(rule); else _disabledNsfwRules.Add(rule);
                _ruleList.SelectedItem = _rules.FirstOrDefault();
                _frames.Value = spec.Analysis.VideoFrames; _gpu.IsChecked = spec.Analysis.PreferGpu;
                _tagThreshold.Value = (decimal)spec.TagThreshold; _output.Text = spec.OutputFolder;
                _splitTypes.IsChecked = spec.SplitTypes; _writeText.IsChecked = spec.WriteText; _mode.SelectedIndex = spec.Move ? 1 : 0;
                _lastJournal = spec.LastJournal; _inputs.Clear(); _inputs.AddRange(job.Inputs);
                _entries.Clear();
                var excluded = spec.ExcludedPaths.ToHashSet(BatchRename.PathComparer);
                foreach (var path in job.Inputs)
                {
                    var entry = new MediaFileEntry(path) { Include = !excluded.Contains(path) };
                    entry.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(MediaFileEntry.Include)) InvalidatePlan(); };
                    _entries.Add(entry);
                }
            }
            finally { _syncing = false; }
            if (job.ClassificationSnapshot is null)
            {
                var loaded = await FolderClassificationTaskStore.LoadAsync(job, _lifetime.Token);
                job.InitializeClassificationSnapshot(loaded);
            }
            if (_closed) return;
            AttachTask(job); _files.SelectedIndex = _entries.Count > 0 ? 0 : -1;
        }
        finally { _loadingTask = false; if (!_closed) { SetBusy(TaskActive); RefreshTask(); } }
    }

    private void AttachTask(Job job)
    {
        if (_taskJob is { } previous) previous.PropertyChanged -= TaskChanged;
        _taskJob = job; _seenSnapshot = null; _seenFiles.Clear(); _results.Clear(); _analysisPending.Clear();
        job.PropertyChanged += TaskChanged;
        Title = Catalog.Find("folder-classification").Label + " · " + job.Name;
        _attempted = true; RefreshTask();
    }

    private void TaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed || Interlocked.Exchange(ref _taskRefreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _taskRefreshPosted, 0); if (!_closed) RefreshTask(); }, DispatcherPriority.Background);
    }

    private void RefreshTask()
    {
        if (_loadingTask || _taskJob is not { } job || _closed) return;
        if (_busy != TaskActive && !_writing && _operation is null) SetBusy(TaskActive);
        if (job.ClassificationSnapshot is { } snapshot && !ReferenceEquals(snapshot, _seenSnapshot))
        {
            _seenSnapshot = snapshot;
            var entries = _entries.ToDictionary(entry => entry.Path, BatchRename.PathComparer);
            foreach (var file in snapshot.Files)
            {
                if (_seenFiles.TryGetValue(file.Path, out var seen) && ReferenceEquals(file, seen)) continue;
                _seenFiles[file.Path] = file;
                if (!entries.TryGetValue(file.Path, out var entry)) continue;
                if (file.Pending || file.Error is not null) _analysisPending.Add(file.Path); else _analysisPending.Remove(file.Path);
                if (file.Result is { } result)
                {
                    _results[file.Path] = FolderOutfitClassification.KeepGroups(FolderClassification.KeepManual(FolderClassification.Classify(result.Media, _rules.ToArray(),
                        (double)(_tagThreshold.Value ?? .5m), _settings.EnableNsfwContent), result), result, _rules.ToArray(), _settings.EnableNsfwContent);
                    UpdateEntry(entry);
                }
                else { _results.Remove(file.Path); entry.Status = Localization.Text("待分析"); }
                if (file.Error is { } error) { entry.Status = Localization.Text("失败"); entry.Details = error; }
            }
            _plan = null; QueueBoardRefresh(); RenderDetails(); UpdateActions();
        }
        _activity.Update(job.Activity is { } activity ? MediaPrivacy.Filter(activity, _settings.EnableNsfwContent) : null);
        _status.Text = TaskActive ? Localization.Join(" · ", [Localization.Text(job.Status), Localization.Text("关闭窗口后任务继续运行")])
            : Localization.Join(" · ", [Localization.Text(job.Status), job.Error]);
        _stop.IsVisible = job.State is JobState.Waiting or JobState.Running;
        _stop.IsEnabled = job.State is JobState.Waiting or JobState.Running;
    }

    private async Task SaveTaskViewAsync()
    {
        if (_loadingTask || TaskActive || _taskJob is not { } job) return;
        RefreshTask();
        var spec = CaptureTaskOptions();
        spec.Rules = _rules.Concat(_disabledNsfwRules).Take(8).ToArray();
        spec.AllowSemanticDownload = job.Options.FolderClassification!.AllowSemanticDownload;
        // Turning privacy off only hides saved private rules; it must not silently opt a task back into them.
        spec.IncludeNsfw = job.Options.FolderClassification!.IncludeNsfw && _settings.EnableNsfwContent;
        job.Options.FolderClassification = spec.Clone(); job.Inputs = _entries.Select(entry => entry.Path).ToArray();
        var files = _entries.Select(entry => new FolderClassificationTaskFile(entry.Path, _results.GetValueOrDefault(entry.Path),
            _seenFiles.GetValueOrDefault(entry.Path)?.Error, _analysisPending.Contains(entry.Path))).ToArray();
        var changed = files.Where(file => !_seenFiles.TryGetValue(file.Path, out var old)
            || !ReferenceEquals(file.Result, old.Result) || file.Error != old.Error || file.Pending != old.Pending).ToArray();
        var snapshot = new FolderClassificationTaskSnapshot(files); _seenSnapshot = snapshot;
        _seenFiles.Clear(); foreach (var file in files) _seenFiles[file.Path] = file;
        job.ClassificationSnapshot = snapshot;
        var previousSave = _viewSave;
        _viewSave = SaveAsync(); await _viewSave;
        async Task SaveAsync()
        {
            var retry = false;
            try { await previousSave; } catch { retry = true; }
            try
            {
                foreach (var file in retry ? files : changed) await FolderClassificationTaskStore.SaveFileAsync(job, file);
            }
            catch { _seenFiles.Clear(); throw; }
        }
    }

    private async Task SaveAndCloseAsync()
    {
        if (_savingClose) return;
        _savingClose = true;
        try { await SaveTaskViewAsync(); _closingView = true; Close(); }
        catch (Exception error) { await Ui.Message(this, "保存分类任务失败", error.Message); }
        finally { _savingClose = false; }
    }

    public async Task FlushTaskEditsAsync()
    { await SaveTaskViewAsync(); await _viewSave; }
}
