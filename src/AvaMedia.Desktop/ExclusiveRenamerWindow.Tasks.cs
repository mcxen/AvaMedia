using Avalonia.Controls;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class ExclusiveRenamerWindow
{
    private readonly Func<Job, Task> _submit;
    private readonly Func<Job, RenameItem[], Task<Job>> _commit;
    private readonly Action<Job> _stopTask;
    private readonly Func<Job, Task> _viewTask;
    private readonly Action _persist;
    private readonly Button _background;
    private readonly DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _reviewTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Job? _analysisJob, _renameJob;
    private VlRenameTaskResult? _shownResult;
    private bool _seenRename, _checkingCommit, _renameUndone, _refreshingTask, _reviewDirty;
    public event Action<Job>? TaskAttached;

    private static bool TaskActive(Job? job) => job?.State is JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping;
    private Entry AddEntry(string path, string folderContext)
    {
        var entry = new Entry(path, folderContext);
        entry.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(Entry.Include) or nameof(Entry.Ready)) UpdateActions();
            if (_analysisJob is null || _refreshingTask || TaskActive(_analysisJob) || TaskActive(_renameJob)
                || args.PropertyName is not (nameof(Entry.Include) or nameof(Entry.NewName))) return;
            var spec = _analysisJob.Options.VlRename!;
            spec.Review = spec.Sources.Select(source =>
            {
                var row = _entries.FirstOrDefault(item => BatchRename.PathComparer.Equals(item.Path, source.Path));
                if (row is null) return new VlRenameReviewItem(source.Path,
                    spec.Review.FirstOrDefault(item => BatchRename.PathComparer.Equals(item.Source, source.Path))?.Name, false);
                var edited = row.Suggestion is not null && row.NewName != Path.GetFileName(row.Suggestion.Item.Target);
                return new VlRenameReviewItem(row.Path, edited ? row.NewName : null, row.Include);
            }).ToArray();
            _reviewDirty = true; _reviewTimer.Stop(); _reviewTimer.Start();
        };
        _entries.Add(entry); return entry;
    }

    internal void AttachTask(Job analysis, Job? rename = null)
    {
        var spec = analysis.Options.VlRename ?? throw new ArgumentException("此任务不是 VL 命名分析。");
        _analysisJob = analysis; _renameJob = rename; _shownResult = null; _seenRename = _renameUndone = false;
        _journal = rename?.Output ?? ""; _canUndo = false;
        _refreshingTask = true;
        try
        {
            _entries.Clear();
            foreach (var source in spec.Sources) AddEntry(source.Path, source.FolderContext);
        }
        finally { _refreshingTask = false; }
        _mode.SelectedIndex = spec.Mode == "online" ? 1 : spec.Mode == "rules" ? 2 : 0;
        RefreshProviders();
        if (_providers.ItemsSource is IEnumerable<ProviderChoice> choices)
            _providers.SelectedItem = choices.FirstOrDefault(provider => provider.Id == spec.ProviderId) ?? _providers.SelectedItem;
        TaskAttached?.Invoke(analysis); RefreshTask();
    }

    private async Task GenerateAsync()
    {
        if (_busy || _closed || TaskActive(_analysisJob) || TaskActive(_renameJob)) return;
        var inputs = _entries.Where(entry => entry.Include).Select(entry => new VlMediaRenameInput(entry.Path, entry.FolderContext)).ToArray();
        if (inputs.Length == 0) return;
        var mode = _mode.SelectedIndex == 1 ? "online" : _mode.SelectedIndex == 2 ? "rules" : "local";
        var providerId = (_providers.SelectedItem as ProviderChoice)?.Id;
        var settings = _settings.Clone();
        SetBusy(true);
        try
        {
            var job = await Task.Run(() => VlRenameTasks.CreateAnalysis(settings, inputs, mode, providerId), _lifetime.Token);
            if (_closed) return;
            await _submit(job);
            if (!_closed) AttachTask(job);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Report(error); }
        finally { if (!_closed) SetBusy(false); }
    }

    private void StopCurrentTask()
    {
        _operation?.Cancel();
        if (TaskActive(_renameJob)) _stopTask(_renameJob!);
        else if (TaskActive(_analysisJob)) _stopTask(_analysisJob!);
    }

    private void RefreshTask()
    {
        if (_closed || _analysisJob is null) return;
        var snapshot = _analysisJob.VlRenameResult;
        if (snapshot is not null && !ReferenceEquals(snapshot, _shownResult))
        {
            _shownResult = snapshot;
            _refreshingTask = true;
            try
            {
                foreach (var file in snapshot.Files)
                {
                    var row = _entries.FirstOrDefault(entry => BatchRename.PathComparer.Equals(entry.Path, file.Source.Path));
                    if (row is null) continue;
                    row.Suggestion = file.Suggestion;
                    var review = _analysisJob.Options.VlRename!.Review.FirstOrDefault(item => BatchRename.PathComparer.Equals(item.Source, file.Source.Path));
                    row.NewName = file.Suggestion is null ? "" : review?.Name ?? Path.GetFileName(file.Suggestion.Item.Target);
                    if (review is not null) row.Include = review.Include;
                    row.Status = file.Error is not null ? "分析失败" : file.Suggestion is not null ? "VL 已分析" : "待分析";
                    row.Details = file.Source.Path + "\n" + (file.Error ?? file.Suggestion?.Details ?? "");
                }
            }
            finally { _refreshingTask = false; }
        }
        foreach (var row in _entries.Where(row => row.Suggestion is null && row.Status != "分析失败"))
            row.Status = TaskActive(_analysisJob) && _analysisJob.Activity?.Detail == row.Path ? "分析中"
                : TaskActive(_analysisJob) ? "待分析" : "未完成";
        if (_renameJob is { } rename)
        {
            if (rename.State == JobState.Completed && !_seenRename && !_checkingCommit) _ = FinishRenameAsync(rename);
            _status.Text = Localization.Text(_renameUndone ? "原文件名已恢复。" : rename.Status)
                + (rename.Error.Length > 0 ? " · " + rename.Error : "");
        }
        else
        {
            var count = snapshot is null ? "" : Localization.Format($"成功 {snapshot.Completed} 个，失败 {snapshot.Failed} 个，共 {snapshot.Files.Length} 个。");
            _status.Text = Localization.Text(_analysisJob.Status) + " · " + count;
            if (_analysisJob.Error.Length > 0) _status.Text += " · " + _analysisJob.Error;
        }
        UpdateActions();
    }

    private async Task FinishRenameAsync(Job job)
    {
        _checkingCommit = true;
        try
        {
            var journal = await AiTaskResults.LoadAsync<BatchRename.RenameJournal>(job.Output, _lifetime.Token)
                ?? throw new IOException("重命名日志缺失。");
            if (_closed) return;
            _renameUndone = journal.State == "undone";
            if (journal.State == "completed")
            {
                _refreshingTask = true;
                try { ApplyChanges(job.Options.Rename!.Plan); }
                finally { _refreshingTask = false; }
            }
            _canUndo = BatchRename.CanUndo(job.Output); _seenRename = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _seenRename = true; Report(error); }
        finally { _checkingCommit = false; if (!_closed) UpdateActions(); }
    }
}
