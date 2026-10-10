using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class RenameWindow
{
    private sealed record MediaSnapshot(RenameMediaInfo Info, long Length, DateTime Modified);
    private readonly Dictionary<string, MediaSnapshot> _mediaInfo = new(BatchRename.PathComparer);
    private readonly Dictionary<string, int> _importOrder = new(BatchRename.PathComparer);
    private int _nextImportOrder;
    private int _sortOrder;

    private void AddPaths(IEnumerable<string> paths)
    {
        if (_closed) return;
        try
        {
            var existing = _entries.Select(entry => entry.Path).ToHashSet(BatchRename.PathComparer);
            foreach (var path in BatchRename.CollectMedia(paths, false))
            {
                if (!existing.Add(path)) continue;
                var entry = new MediaFileEntry(path);
                entry.PropertyChanged += (_, args) => { if (!_semanticApplying && args.PropertyName is nameof(MediaFileEntry.Include) or nameof(MediaFileEntry.Keyword)) InvalidatePlan(); };
                _entries.Add(entry); _importOrder[path] = _nextImportOrder++;
            }
            if (_operation is null) SortFiles(_sortOrder);
            else InvalidatePlan();
        }
        catch (Exception error) { AppDiagnostics.Record("Import rename files", error); _progressText.Text = error.Message; }
    }
    private async Task AddFolders(IEnumerable<string> paths)
    {
        if (_closed || _importing || _operation is not null || _renaming) return;
        var selected = paths.ToArray(); if (selected.Length == 0) return;
        var recursive = _recursive.IsChecked == true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cancellation; _importing = true; SetBusy(true); _progressText.Text = Localization.Text("正在查找图片和视频…");
        try
        {
            var files = await Task.Run(() => BatchRename.CollectMedia(selected, recursive, cancellation.Token), cancellation.Token);
            if (!_closed) { AddPaths(files); if (files.Length == 0) _progressText.Text = Localization.Text("没有可导入的图片或视频。"); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_closed) _progressText.Text = Localization.Text("已停止"); }
        catch (Exception error) { await ShowErrorAsync("导入失败", error); }
        finally { _operation = null; _importing = false; if (!_closed) { SetBusy(false); SortFiles(_sortOrder); } }
    }
    private void SortFiles(int order)
    {
        if (_renaming || _importing || _operation is not null) return;
        _sortOrder = order;
        try
        {
            var sorted = order switch
            {
                1 => _entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                2 => _entries.OrderByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                3 => _entries.OrderBy(entry => new FileInfo(entry.Path).LastWriteTimeUtc).ToArray(),
                _ => _entries.OrderBy(entry => _importOrder.GetValueOrDefault(entry.Path)).ToArray()
            };
            _entries.Clear(); foreach (var entry in sorted) _entries.Add(entry); InvalidatePlan();
        }
        catch (Exception error) { AppDiagnostics.Record("Sort rename files", error); _progressText.Text = error.Message; }
    }
    private void InvalidatePlan(bool preview = true)
    {
        if (_closed) return;
        _revision++; _renamePlan = null; if (_rename is not null) _rename.IsEnabled = false;
        _previewCancellation?.Cancel();
        foreach (var entry in _entries) entry.NewName = "";
        Localization.SetText(_summary, $"{_entries.Count} 个文件 · 图片 {_entries.Count(entry => new MediaFileRouter().Classify(entry.Path) == MediaFileKind.Image)} · 视频 {_entries.Count(entry => VideoFormats.IsVideo(entry.Path))} · 勾选 {_entries.Count(entry => entry.Include)} 个");
        if (!preview || _entries.Count == 0 || _operation is not null || _renaming || _importing) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _previewCancellation = cancellation;
        _ = PreviewAsync(_revision, cancellation, true);
    }
    private Task PreviewRename()
    {
        if (_closed || _operation is not null || _renaming || _importing) return Task.CompletedTask;
        _revision++; _renamePlan = null; _rename.IsEnabled = false; _previewCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _previewCancellation = cancellation;
        return PreviewAsync(_revision, cancellation, false);
    }
    private async Task PreviewAsync(int revision, CancellationTokenSource cancellation, bool delay)
    {
        try
        {
            if (delay) await Task.Delay(200, cancellation.Token);
            if (_closed || revision != _revision) return;
            if (_invalidRuleInputs.Count > 0) throw new ArgumentException("请输入范围内的整数。");
            var selected = _entries.Where(entry => entry.Include).ToArray();
            if (selected.Length == 0) { _progressText.Text = Localization.Text("请勾选需要重命名的文件。"); return; }
            var files = selected.Select(entry => entry.Path).ToArray(); var operations = _rules.Select(rule => rule.Operation).ToArray();
            ValidateSemanticSources(files);
            var keywords = selected.ToDictionary(entry => entry.Path, entry => entry.Keyword.Trim(), BatchRename.PathComparer);
            var snapshots = new Dictionary<string, MediaSnapshot>(_mediaInfo, BatchRename.PathComparer);
            var metadataRequired = BatchRename.NeedsMediaInfo(operations);
            _progressText.Text = Localization.Text(metadataRequired ? "读取媒体信息…" : "正在预览…");
            if (metadataRequired) { _stop.IsVisible = true; _stop.IsEnabled = true; }
            var result = await Task.Run(async () =>
            {
                var information = new Dictionary<string, RenameMediaInfo>(BatchRename.PathComparer);
                var errors = new Dictionary<string, string>(BatchRename.PathComparer);
                if (metadataRequired)
                    foreach (var path in files)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        try
                        {
                            var file = new FileInfo(path); var length = file.Length; var modified = file.LastWriteTimeUtc;
                            if (!snapshots.TryGetValue(path, out var cached) || cached.Length != length || cached.Modified != modified)
                            {
                                var info = await _engine.Probe(path, cancellation.Token);
                                if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("无法读取媒体画面尺寸。");
                                var duration = VideoFormats.IsVideo(path) && double.IsFinite(info.Duration) ? info.Duration : 0;
                                file.Refresh(); if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified) throw new IOException("读取期间源文件已改变，请重新预览。");
                                snapshots[path] = cached = new(new(info.Width, info.Height, duration), length, modified);
                            }
                            information[path] = cached.Info;
                        }
                        catch (Exception error) when (error is not OperationCanceledException) { errors[path] = error.Message; }
                    }
                var preview = BatchRename.PreviewRules(files, operations, information, keywords, cancellation.Token);
                return new RenamePreview(preview.Entries.Select(entry => errors.TryGetValue(entry.Source, out var error) ? entry with { Error = error } : entry).ToArray());
            }, cancellation.Token);
            if (_closed || revision != _revision || cancellation.IsCancellationRequested) return;
            foreach (var (path, snapshot) in snapshots) _mediaInfo[path] = snapshot;
            var byPath = result.Entries.ToDictionary(entry => entry.Source, BatchRename.PathComparer);
            foreach (var entry in _entries)
            {
                if (!byPath.TryGetValue(entry.Path, out var row)) { entry.NewName = ""; entry.Status = "未勾选"; continue; }
                entry.NewName = row.NewName;
                entry.Status = Localization.Text(row.Error is not null ? "错误" : row.Changed ? "待重命名" : "不变");
                entry.Details = (_semanticDetails.GetValueOrDefault(entry.Path) ?? entry.Path)
                    + (row.Error is not null ? Environment.NewLine + Localization.Text(row.Error) : "");
            }
            _renamePlan = result.CanApply ? result.Plan : null;
            _rename.IsEnabled = result.CanApply;
            Localization.SetText(_progressText, $"将修改 {result.Changes} 个 · 错误 {result.Errors} 个");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!_closed && revision == _revision) _progressText.Text = Localization.Text("预览已停止"); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Rename preview", error);
            if (!_closed && revision == _revision) { _renamePlan = null; _rename.IsEnabled = false; _progressText.Text = Localization.Text(error.Message); }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) { _previewCancellation = null; if (!_closed && _operation is null) _stop.IsVisible = false; }
            cancellation.Dispose();
        }
    }
    private Task ApplyRename() => ExecuteRenameAsync(false);
    private Task UndoRename() => ExecuteRenameAsync(true);
    private async Task ExecuteRenameAsync(bool undo)
    {
        if (_closed || _renaming || _importing || _operation is not null || !undo && _renamePlan is null) return;
        var plan = _renamePlan; _revision++; _previewCancellation?.Cancel(); _renaming = true; SetBusy(true);
        try
        {
            using var reservation = _reserveFiles?.Invoke(await SourceFileChanges.RenamePathsAsync(_journal, undo, plan));
            var mappings = await Task.Run(() => undo ? BatchRename.UndoRename(_journal) : BatchRename.ApplyRename(plan!, _journal));
            var map = mappings.ToDictionary(item => item.Source, item => item.Target, BatchRename.PathComparer);
            _semanticResults.Clear(); _semanticDetails.Clear(); _mediaInfo.Clear();
            foreach (var entry in _entries)
                if (map.TryGetValue(entry.Path, out var target))
                {
                    var old = entry.Path; var order = _importOrder.GetValueOrDefault(old); _importOrder.Remove(old); _importOrder[target] = order;
                    entry.Renamed(target); entry.Status = Localization.Text(undo ? "已还原" : "已重命名");
                }
            if (undo) Localization.SetText(_progressText, $"已还原 {mappings.Length} 个文件名。");
            else Localization.SetText(_progressText, $"已重命名 {mappings.Length} 个文件。");
            if (Renamed is { } changed)
                foreach (Action<IReadOnlyList<RenameItem>> subscriber in changed.GetInvocationList())
                    try { subscriber(mappings); } catch (Exception error) { await ShowErrorAsync("任务路径同步失败", error); }
        }
        catch (Exception error) { await ShowErrorAsync(undo ? "撤销失败" : "重命名失败", error); }
        finally
        {
            _renaming = false;
            if (!_closed) { SetBusy(false); _undo.IsEnabled = BatchRename.CanUndo(_journal); InvalidatePlan(preview: false); }
        }
    }
    private void SetBusy(bool busy)
    {
        if (busy) { _revision++; _previewCancellation?.Cancel(); _renamePlan = null; }
        _importBar.IsEnabled = _catalog.IsEnabled = _ruleList.IsEnabled = _ruleActions.IsEnabled = _renamePanel.IsEnabled = _list.IsEnabled = !busy;
        _previewButton.IsEnabled = !busy; _undo.IsEnabled = !busy && BatchRename.CanUndo(_journal); _rename.IsEnabled = !busy && _renamePlan is not null;
        _stop.IsVisible = busy && _operation is not null; _stop.IsEnabled = true;
    }
}
