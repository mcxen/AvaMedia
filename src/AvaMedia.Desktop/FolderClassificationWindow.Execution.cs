using System.Text.Json;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private async Task AnalyzeAsync(bool retryOnly)
    {
        if (_busy) return;
        if (retryOnly && _taskJob is { } previous && previous.State is JobState.Failed or JobState.Cancelled)
        { await SaveTaskViewAsync(); await _resumeTask(previous); return; }
        var paths = _entries.Where(entry => entry.Include).Select(entry => entry.Path).ToArray();
        if (paths.Length == 0) return;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = preparation; SetBusy(true);
        try
        {
            var rules = _rules.ToArray(); FolderClassification.ValidateRules(rules);
            var options = new MediaTagOptions((int)(_frames.Value ?? 12), _gpu.IsChecked == true)
            { SemanticCandidates = rules.SelectMany(rule => rule.Candidates()).ToArray() };
            options.Validate();
            var downloadSemantic = options.NeedsSemanticModel && !await SemanticModelConsent.IsInstalledAsync(preparation.Token);
            if (downloadSemantic && !await SemanticModelConsent.ConfirmAsync(this, Catalog.Find("folder-classification").Label)) return;
            if (_closed) return;
            var selected = paths.ToHashSet(BatchRename.PathComparer);
            var job = new Job { FeatureId = "folder-classification", Inputs = _entries.Select(entry => entry.Path).ToArray(),
                DownloadTitle = Localization.Format($"分类任务 · {DateTime.Now:HH:mm:ss}"),
                Options = new() { Format = "", FolderClassification = CaptureTaskOptions(options) } };
            job.Output = FolderClassificationTaskStore.Folder(job);
            job.Options.FolderClassification!.AllowSemanticDownload = downloadSemantic;
            job.ClassificationSnapshot = new(_entries.Select(entry => new FolderClassificationTaskFile(entry.Path,
                _results.GetValueOrDefault(entry.Path), Pending: selected.Contains(entry.Path)
                    && (!retryOnly || !_results.ContainsKey(entry.Path) || _analysisPending.Contains(entry.Path)))).ToArray());
            foreach (var file in job.ClassificationSnapshot.Files)
                await FolderClassificationTaskStore.SaveFileAsync(job, file, preparation.Token);
            SavePreferences(); _attempted = true; InvalidatePlan();
            preparation.Token.ThrowIfCancellationRequested();
            AttachTask(job); _enqueueTask(job);
        }
        finally { _operation = null; if (!_closed) SetBusy(TaskActive); }
    }

    private async Task PreviewAsync()
    {
        var selected = _entries.Where(entry => entry.Include).ToArray();
        if (selected.Length == 0) throw new ArgumentException("请勾选要整理的文件。");
        if (selected.Any(entry => AnalysisPending(entry))) throw new ArgumentException("部分勾选文件尚未分析完成，请重试或取消勾选。");
        if (string.IsNullOrWhiteSpace(_output.Text)) throw new ArgumentException("请选择分类目录。");
        var files = selected.Select(entry => _results[entry.Path] with { Media = MediaPrivacy.Filter(_results[entry.Path].Media, _settings.EnableNsfwContent, PrivateSemanticLabels) }).ToArray();
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
        if (move && !_canMove()) throw new InvalidOperationException("应用正在退出，请稍后操作。");
        if (move && !await Ui.Confirm(this, "移动分类文件", Localization.Format($"将移动 {plan.Length} 个文件到预览目录。是否执行？"), "移动")) return;
        if (move && !_canMove()) throw new InvalidOperationException("应用正在退出，请稍后操作。");
        var journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "folder-classification",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        using var reservation = _reserveFiles?.Invoke(plan.SelectMany(item => move
            ? new[] { item.File.Media.Path, item.Target } : [item.Target]).Append(journal));
        _lastJournal = journal; SavePreferences();
        try { await RunOrganizationAsync((operation, progress) => FolderOrganization.ExecuteAsync(plan, move, journal, progress, operation.Token), "整理完成"); }
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
        if (!_canMove()) throw new InvalidOperationException("应用正在退出，请稍后操作。");
        if (!await Ui.Confirm(this, "撤销分类整理", "将还原移动的源文件，并移除上次整理产生的副本与标签 TXT。已修改的文件将保留。", "撤销")) return;
        var before = JsonSerializer.Deserialize<FolderOrganization.Journal>(await File.ReadAllTextAsync(journal))!;
        using var reservation = _reserveFiles?.Invoke(before.Entries.SelectMany(entry => before.Move
            ? new[] { entry.Source, entry.Target } : [entry.Target]).Append(journal));
        try { await RunOrganizationAsync((operation, progress) => FolderOrganization.UndoAsync(journal, progress, operation.Token), "撤销完成"); }
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
            else
            {
                var after = JsonSerializer.Deserialize<FolderOrganization.Journal>(await File.ReadAllTextAsync(journal))!;
                var restored = after.Entries.Where(entry => entry.Stage == "undone"
                    && before.Entries.Any(old => old.Target == entry.Target && old.Stage != "undone"));
                _syncing = true;
                try
                {
                    foreach (var entry in restored)
                    {
                        var row = _entries.FirstOrDefault(row => BatchRename.PathComparer.Equals(row.Path, entry.Source));
                        if (row is null) continue;
                        row.Include = true; UpdateEntry(row);
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

    private async Task RunOrganizationAsync(Func<CancellationTokenSource, IProgress<FolderOrganizationProgress>, Task<FolderOrganizationResult>> run, string stage)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation;
        _writing = true; SetBusy(true);
        try
        {
            // Progress must capture the UI context before work starts on the background thread.
            var progress = OrganizationProgress();
            var result = await Task.Run(() => run(operation, progress));
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
            OutputFolder = _output.Text, Plan = _plan, Results = _results.Values.Select(result => result with { Media = MediaPrivacy.Filter(result.Media, _settings.EnableNsfwContent, PrivateSemanticLabels) }).ToArray() };
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
        await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
        _status.Text = Localization.Text("分类结果已导出");
    }
}
