using System.Text.Json;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private async Task AnalyzeAsync(bool retryOnly)
    {
        if (_busy) return;
        var paths = _entries.Where(entry => entry.Include && (!retryOnly || !_results.ContainsKey(entry.Path) || _analysisPending.Contains(entry.Path))).Select(entry => entry.Path).ToArray();
        if (paths.Length == 0) return;
        var rules = _rules.ToArray(); FolderClassification.ValidateRules(rules);
        var options = new MediaTagOptions((int)(_frames.Value ?? 12), _gpu.IsChecked == true)
        { SemanticCandidates = rules.SelectMany(rule => rule.Candidates()).ToArray() };
        options.Validate();
        var threshold = (double)(_tagThreshold.Value ?? .5m);
        var previous = _results.ToDictionary(pair => pair.Key, pair => pair.Value, BatchRename.PathComparer);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; SetBusy(true);
        try
        {
            var store = new ModelStore();
            if (options.NeedsSemanticModel && !await SemanticModelConsent.IsInstalledAsync(operation.Token))
            {
                if (!await SemanticModelConsent.ConfirmAsync(this, "文件夹分类"))
                { _status.Text = Localization.Text("已取消。可移除语义规则后只识别标签。"); return; }
                await SemanticModelConsent.DownloadAsync(DownloadProgress(), operation.Token);
            }
            if (!await store.IsInstalledAsync(ModelCatalog.JoyTagId, ct: operation.Token))
                await store.DownloadAsync(ModelCatalog.JoyTagId, DownloadProgress(), operation.Token);
            _attempted = true; SavePreferences(); InvalidatePlan();
            foreach (var entry in _entries.Where(entry => paths.Contains(entry.Path, BatchRename.PathComparer)))
            { _analysisPending.Add(entry.Path); _results.Remove(entry.Path); entry.Status = Localization.Text("待分析"); entry.Details = ""; }
            var progress = new Progress<MediaTagProgress>(update =>
            {
                if (_closed || _operation != operation) return;
                if (update.Activity is { } activity) _activity.Update(activity);
                _status.Text = $"{update.Completed} / {update.Total} · {Path.GetFileName(update.Path)}";
                var entry = _entries.FirstOrDefault(entry => BatchRename.PathComparer.Equals(entry.Path, update.Path));
                if (entry is null) return;
                if (update.Result is { } result)
                { _results[result.Path] = FolderClassification.KeepManual(FolderClassification.Classify(result, rules, threshold), previous.GetValueOrDefault(result.Path)); _analysisPending.Remove(result.Path); UpdateEntry(entry); }
                else if (update.Error is { } error) { entry.Status = Localization.Text("失败"); entry.Details = error; }
                else entry.Status = Localization.Text(update.Activity?.Stage ?? "处理中");
                if (_files.SelectedItem == entry && (update.Result is not null || update.Error is not null)) RenderDetails();
            });
            var results = await new MediaTagService(_engine).AnalyzeAsync(paths, options, progress, operation.Token);
            if (_closed) return;
            foreach (var result in results)
            { _results[result.Path] = FolderClassification.KeepManual(FolderClassification.Classify(result, rules, threshold), previous.GetValueOrDefault(result.Path)); _analysisPending.Remove(result.Path); }
            foreach (var entry in _entries) UpdateEntry(entry);
            var failed = paths.Count(path => !_results.ContainsKey(path));
            var review = results.Count(result => _results[result.Path].Decisions.Any(decision => decision.NeedsReview));
            _status.Text = Localization.Format($"完成 {results.Count} 个，待确认 {review} 个，失败 {failed} 个");
            _activity.Finish(AiActivityState.Completed, "分类完成");
        }
        catch (OperationCanceledException)
        { if (!_closed) { _status.Text = Localization.Text("已停止，已完成结果已保留"); _activity.Finish(AiActivityState.Cancelled, "已停止"); } }
        catch (Exception error)
        {
            if (!_closed)
            {
                _activity.Finish(AiActivityState.Failed, "分析失败"); _status.Text = error.Message;
                await Ui.Message(this, "分析失败", error.Message);
            }
        }
        finally
        {
            _operation = null;
            if (!_closed)
            {
                foreach (var path in paths.Where(path => !_results.ContainsKey(path)))
                {
                    if (!previous.TryGetValue(path, out var preserved) || !preserved.Decisions.Any(decision => decision.Manual)) continue;
                    try
                    {
                        MediaTagService.ValidateSource(preserved.Media); _results[path] = preserved;
                        if (_entries.FirstOrDefault(entry => BatchRename.PathComparer.Equals(entry.Path, path)) is { } row)
                        { var state = row.Status; UpdateEntry(row); row.Status = state; }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                SetBusy(false); RenderDetails();
            }
        }

        IProgress<ModelDownloadProgress> DownloadProgress()
        {
            var started = DateTime.UtcNow;
            return new Progress<ModelDownloadProgress>(update =>
            {
                if (_closed || _operation != operation) return;
                _status.Text = Localization.Text(update.Stage);
                _activity.Update(new("下载分类模型", "", started, DateTime.UtcNow)
                { Current = update.Received, Total = update.Total, Unit = "字节", Detail = update.SourceName });
            });
        }
    }

    private async Task PreviewAsync()
    {
        var selected = _entries.Where(entry => entry.Include).ToArray();
        if (selected.Length == 0) throw new ArgumentException("请勾选要整理的文件。");
        if (selected.Any(entry => !_results.ContainsKey(entry.Path))) throw new ArgumentException("部分勾选文件尚未分析完成，请重试或取消勾选。");
        if (string.IsNullOrWhiteSpace(_output.Text)) throw new ArgumentException("请选择分类目录。");
        var files = selected.Select(entry => _results[entry.Path]).ToArray();
        var output = Path.GetFullPath(_output.Text);
        var split = _splitTypes.IsChecked == true; var writeText = _writeText.IsChecked == true;
        SetBusy(true);
        try
        {
            var plan = await Task.Run(() => FolderOrganization.Preview(files, output, split, writeText));
            if (_closed) return;
            _plan = plan;
            foreach (var entry in selected) entry.NewName = plan.First(item => BatchRename.PathComparer.Equals(item.File.Media.Path, entry.Path)).Target;
            SavePreferences(); _status.Text = Localization.Format($"预览 {plan.Length} 个文件，请核对分类目录");
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private async Task OrganizeAsync()
    {
        if (_busy || _plan is not { Length: > 0 } plan) return;
        var move = _mode.SelectedIndex == 1;
        if (move && !_canMove()) throw new InvalidOperationException("请在当前转换任务完成或停止后移动文件。");
        if (move && !await Ui.Confirm(this, "移动分类文件", Localization.Format($"将移动 {plan.Length} 个文件到预览目录。是否执行？"), "移动")) return;
        if (move && !_canMove()) throw new InvalidOperationException("请在当前转换任务完成或停止后移动文件。");
        var journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "folder-classification",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        _lastJournal = journal; SavePreferences();
        try { await RunOrganizationAsync(operation => FolderOrganization.ExecuteAsync(plan, move, journal, OrganizationProgress(), operation.Token), "整理完成"); }
        finally
        {
            var recorded = File.Exists(journal)
                ? JsonSerializer.Deserialize<FolderOrganization.Journal>(await File.ReadAllTextAsync(journal))!.Entries
                    .Where(entry => entry.Stage is "copied" or "reported" or "completed").ToArray() : [];
            var completedTargets = recorded.Select(entry => entry.Target).ToHashSet(BatchRename.PathComparer);
            var copied = plan.Where(item => completedTargets.Contains(item.Target) && File.Exists(item.Target)).ToArray();
            var moved = move ? copied.Where(item => !File.Exists(item.File.Media.Path)).ToArray() : [];
            if (moved.Length > 0) Moved?.Invoke(moved.Select(item => new RenameItem(item.File.Media.Path, item.Target, item.File.Media.Length, item.File.Media.LastWriteUtc)).ToArray());
            _syncing = true;
            try
            {
                foreach (var item in moved)
                {
                    var entry = _entries.First(entry => BatchRename.PathComparer.Equals(entry.Path, item.File.Media.Path));
                    if (_analysisPending.Remove(entry.Path)) _analysisPending.Add(item.Target);
                    _results.Remove(entry.Path);
                    _results[item.Target] = item.File with { Media = item.File.Media with { Path = item.Target, LastWriteUtc = File.GetLastWriteTimeUtc(item.Target) } };
                    entry.Renamed(item.Target); entry.Include = false; entry.Status = Localization.Text("已移动");
                }
                foreach (var item in copied.Except(moved))
                { var entry = _entries.First(entry => BatchRename.PathComparer.Equals(entry.Path, item.File.Media.Path)); entry.Include = false; entry.Status = Localization.Text("已复制"); }
            }
            finally { _syncing = false; InvalidatePlan(); RenderBoard(); }
        }
    }

    private async Task UndoAsync()
    {
        if (_lastJournal is not { } journal || !FolderOrganization.CanUndo(journal)) return;
        if (!_canMove()) throw new InvalidOperationException("请在当前转换任务完成或停止后撤销整理。");
        if (!await Ui.Confirm(this, "撤销分类整理", "将还原移动的源文件，并移除上次整理产生的副本与标签 TXT。已修改的文件将保留。", "撤销")) return;
        var before = JsonSerializer.Deserialize<FolderOrganization.Journal>(await File.ReadAllTextAsync(journal))!;
        try { await RunOrganizationAsync(operation => FolderOrganization.UndoAsync(journal, OrganizationProgress(), operation.Token), "撤销完成"); }
        finally
        {
            if (before.Move)
            {
                var after = JsonSerializer.Deserialize<FolderOrganization.Journal>(await File.ReadAllTextAsync(journal))!;
                var mappings = after.Entries.Where(entry => entry.Stage == "undone" && before.Entries.Any(old => old.Target == entry.Target && old.Stage != "undone"))
                    .Select(entry => new RenameItem(entry.Target, entry.Source, entry.Length, entry.LastWriteUtc)).ToArray();
                Moved?.Invoke(mappings);
                _syncing = true;
                try
                {
                    foreach (var mapping in mappings)
                    {
                        var row = _entries.FirstOrDefault(entry => BatchRename.PathComparer.Equals(entry.Path, mapping.Source));
                        if (row is null) continue;
                        if (_results.Remove(mapping.Source, out var result)) _results[mapping.Target] = result with { Media = result.Media with { Path = mapping.Target, LastWriteUtc = mapping.LastWriteUtc } };
                        if (_analysisPending.Remove(mapping.Source)) _analysisPending.Add(mapping.Target);
                        row.Renamed(mapping.Target); row.Include = true; UpdateEntry(row);
                    }
                }
                finally { _syncing = false; }
            }
            InvalidatePlan(); RenderBoard();
        }
    }

    private IProgress<FolderOrganizationProgress> OrganizationProgress()
    {
        var operation = _operation;
        return new Progress<FolderOrganizationProgress>(update =>
        {
            if (_closed || _operation != operation) return;
            _status.Text = $"{update.Completed} / {update.Total} · {Path.GetFileName(update.Source)}";
            if (update.Error is not null) _status.Text += " · " + update.Error;
        });
    }

    private async Task RunOrganizationAsync(Func<CancellationTokenSource, Task<FolderOrganizationResult>> run, string stage)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation;
        _writing = true; SetBusy(true);
        try
        {
            var result = await Task.Run(() => run(operation));
            _status.Text = Localization.Format($"{Localization.Key(result.Cancelled ? "已停止" : stage)} · 完成 {result.Completed} 个，失败 {result.Errors.Length} 个");
            _scanErrors.Text = string.Join(Environment.NewLine, result.Errors); _scanErrors.IsVisible = result.Errors.Length > 0;
        }
        finally { _operation = null; _writing = false; if (!_closed) SetBusy(false); }
    }

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("导出分类结果"), SuggestedFileName = "folder-classification.json", DefaultExtension = "json" });
        if (file is null) return;
        var report = new { Rules = _rules.ToArray(), VideoFrames = _frames.Value, TagThreshold = _tagThreshold.Value,
            OutputFolder = _output.Text, Plan = _plan, Results = _results.Values.ToArray() };
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
        await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
        _status.Text = Localization.Text("分类结果已导出");
    }
}
