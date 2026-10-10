using System.ComponentModel;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private readonly Action<Job> _enqueueTask;
    private readonly Func<Job, Task> _resumeTask;
    private readonly Action<Job> _stopTask;
    private readonly Action<Job>? _pauseTask;
    private readonly Action _newTask, _showTasks;
    private Job? _taskJob;
    private FolderClassificationTaskSnapshot? _seenSnapshot;
    private readonly Dictionary<string, FolderClassificationTaskFile> _seenFiles = new(BatchRename.PathComparer);
    private bool _loadingTask, _closingView, _savingClose;
    private bool _refreshingSnapshot;
    private int _taskRefreshPosted;
    private Task _viewSave = Task.CompletedTask;
    private Task _snapshotRefresh = Task.CompletedTask;
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
        var token = _lifetime.Token;
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
            }
            finally { _syncing = false; }
            AttachTask(job);
            // Paint the task controls and live activity before hydrating a large file list.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var excluded = spec.ExcludedPaths.ToHashSet(BatchRename.PathComparer);
            var count = 0;
            foreach (var path in job.Inputs)
            {
                token.ThrowIfCancellationRequested();
                var entry = new MediaFileEntry(path) { Include = !excluded.Contains(path) };
                entry.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(MediaFileEntry.Include)) InvalidatePlan(); };
                _entries.Add(entry);
                if (++count % 64 != 0) continue;
                RenderBoard();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            if (job.ClassificationSnapshot is null)
            {
                var loaded = await Task.Run(() => FolderClassificationTaskStore.LoadAsync(job, token), token);
                job.InitializeClassificationSnapshot(loaded);
            }
            if (_closed) return;
            RenderBoard();
        }
        finally { _loadingTask = false; if (!_closed) RefreshTask(); }
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
        if (_taskJob is not { } job || _closed) return;
        var pendingSnapshot = !_loadingTask && !_refreshingSnapshot && job.ClassificationSnapshot is { } current && !ReferenceEquals(current, _seenSnapshot);
        var busy = _loadingTask || _refreshingSnapshot || pendingSnapshot || TaskActive;
        if (_busy != busy && !_writing && _operation is null) SetBusy(busy);
        _activity.Update(job.Activity is { } activity ? MediaPrivacy.Filter(activity, _settings.EnableNsfwContent) : null);
        _status.Text = TaskActive ? Localization.Join(" · ", [Localization.Text(job.Status), Localization.Text("关闭窗口后任务继续运行")])
            : Localization.Join(" · ", [Localization.Text(job.Status), job.Error]);
        _stop.IsVisible = job.State is JobState.Waiting or JobState.Running or JobState.Paused;
        _stop.IsEnabled = _stop.IsVisible;
        _pause.IsVisible = _pauseTask is not null && job.State is JobState.Waiting or JobState.Running or JobState.Paused;
        _pause.Content = Localization.Text(job.State == JobState.Paused ? "继续任务" : "暂停任务");
        if (!_loadingTask && !_refreshingSnapshot && job.ClassificationSnapshot is { } snapshot && !ReferenceEquals(snapshot, _seenSnapshot))
            _snapshotRefresh = RefreshSnapshotAsync(job);
    }

    private async Task RefreshSnapshotAsync(Job job)
    {
        _refreshingSnapshot = true;
        var token = _lifetime.Token;
        try
        {
            while (!_closed && ReferenceEquals(job, _taskJob) && job.ClassificationSnapshot is { } snapshot && !ReferenceEquals(snapshot, _seenSnapshot))
            {
                var rules = _rules.ToArray();
                var threshold = (double)(_tagThreshold.Value ?? .5m);
                var includeNsfw = _settings.EnableNsfwContent;
                var spec = job.Options.FolderClassification!;
                var reuseResults = threshold == spec.TagThreshold && includeNsfw == spec.IncludeNsfw
                    && rules.SequenceEqual(spec.Rules.Where(rule => includeNsfw || !MediaPrivacy.IsSensitiveRule(rule)));
                var entries = _entries.ToDictionary(entry => entry.Path, BatchRename.PathComparer);
                var changed = snapshot.Files.Where(file => !_seenFiles.TryGetValue(file.Path, out var seen) || !ReferenceEquals(file, seen)).ToArray();
                _seenSnapshot = snapshot;
                foreach (var batch in changed.Chunk(64))
                {
                    // Task results already contain the classification and clothing groups. Reproject only changed view settings.
                    var projected = reuseResults ? batch.Select(file => file.Result).ToArray() : await Task.Run(() => batch.Select(file =>
                    {
                        token.ThrowIfCancellationRequested();
                        return file.Result is not { } result ? null : FolderOutfitClassification.KeepGroups(
                            FolderClassification.KeepManual(FolderClassification.Classify(result.Media, rules, threshold, includeNsfw), result), result, rules, includeNsfw);
                    }).ToArray(), token);
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(job, _taskJob)) return;
                    if (!_rules.SequenceEqual(rules) || (double)(_tagThreshold.Value ?? .5m) != threshold || _settings.EnableNsfwContent != includeNsfw)
                    { _seenSnapshot = null; _seenFiles.Clear(); break; }
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var file = batch[index];
                        _seenFiles[file.Path] = file;
                        if (!entries.TryGetValue(file.Path, out var entry)) continue;
                        if (file.Pending || file.Error is not null) _analysisPending.Add(file.Path); else _analysisPending.Remove(file.Path);
                        if (projected[index] is { } result) { _results[file.Path] = result; UpdateEntry(entry); }
                        else { _results.Remove(file.Path); entry.Status = Localization.Text("待分析"); }
                        if (file.Error is { } error) { entry.Status = Localization.Text("失败"); entry.Details = error; }
                    }
                    _plan = null; QueueBoardRefresh();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
            }
            if (!_closed) { QueueBoardRefresh(); RenderDetails(); UpdateActions(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_closed) { _seenSnapshot = null; await Ui.Message(this, "打开分类任务失败", error.Message); } }
        finally
        {
            _refreshingSnapshot = false;
            var busy = _loadingTask || TaskActive;
            if (!_closed && !_writing && _operation is null && _busy != busy) SetBusy(busy);
        }
    }

    private async Task SaveTaskViewAsync()
    {
        if (_loadingTask || TaskActive || _taskJob is not { } job) return;
        RefreshTask();
        await _snapshotRefresh;
        if (_closed || TaskActive || !ReferenceEquals(job, _taskJob)) return;
        if (_seenSnapshot is null && job.ClassificationSnapshot is not null)
            throw new InvalidOperationException("分类结果尚未载入，请重新打开任务。");
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
