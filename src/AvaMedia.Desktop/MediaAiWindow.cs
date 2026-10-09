using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<MediaFileEntry> _entries = [];
    private readonly Dictionary<string, MediaTagResult> _results = new(BatchRename.PathComparer);
    private readonly ListBox _list = new() { Name = "MediaAiFiles" };
    private readonly WrapPanel _imports = new();
    private readonly NumericUpDown _threshold = new() { Minimum = .05m, Maximum = .95m, Value = .4m, Increment = .05m };
    private readonly NumericUpDown _frames = new() { Minimum = 1, Maximum = 32, Value = 8, Increment = 1 };
    private readonly CheckBox _gpu = new() { Content = "自动适配 GPU" };
    private readonly CheckBox _reuse = new() { Content = "复用相似画面", IsChecked = true };
    private readonly CheckBox _sceneTags = new() { Content = "识别场景、照明与面部", IsChecked = true };
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹", IsChecked = true };
    private readonly TextBlock _status = Ui.Text("就绪", "caption");
    private readonly TextBlock _modelStatus = Ui.Text("读取模型状态…", "caption");
    private readonly Controls.AiActivityView _activity = new() { Compact = true };
    private readonly Button _analyze = new() { Name = "MediaAiAnalyze", Content = "开始分析", Classes = { "primary" } };
    private readonly Button _rename = new() { Content = "标签重命名…" };
    private readonly Button _undo = new() { Content = "撤销重命名" };
    private readonly Button _stop = new() { Content = "停止", IsVisible = false };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<Window, Task> _manageModels;
    private readonly Func<bool> _canRename;
    private readonly string _journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "ai-rename.json");
    private CancellationTokenSource? _operation;
    private bool _closed, _renaming, _busy, _modelReady;
    public event Action<IReadOnlyList<RenameItem>>? Renamed;

    public MediaAiWindow(IMediaEngine engine, AppSettings settings, IEnumerable<string>? initial, Func<Window, Task> manageModels, Func<bool>? canRename=null, Storage? storage=null)
    {
        _manageModels = manageModels; _canRename=canRename??(()=>true); _storage=storage??new Storage();
        _engine = engine; _settings = settings; _gpu.IsChecked = settings.AutoDetectGpu;
        LoadPreferences();
        if (!ModelCatalog.Find(ModelCatalog.EmbeddingId).Supported) { _sceneTags.IsChecked = false; _sceneTags.IsEnabled = false; }
        // These inputs live in the optional settings dialog, so initialize text before any template is attached.
        _threshold.Text = _threshold.Value?.ToString(_threshold.NumberFormat);
        _frames.Text = _frames.Value?.ToString(_frames.NumberFormat);
        Title = "AI 标签工作台"; Width = 1440; Height = 900; MinWidth = 1100; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Controls.WindowArtwork.SetKind(this, "image");
        BuildInterface();
        var queueState=new Avalonia.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
        queueState.Tick+=(_,_)=>UpdateActions();Opened+=(_,_)=>queueState.Start();Closed+=(_,_)=>queueState.Stop();
        InitializeWordLibraries();
        Opened += async (_, _) => await RefreshModelAsync();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _busy ? DragDropEffects.None : DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, async (_, e) => { if (!_busy) await AddFoldersAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, e) => { if (_renaming) { e.Cancel = true; return; } _closed = true; _operation?.Cancel(); _lifetime.Cancel(); };
        Closed += (_, _) => { try { SavePreferences(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppDiagnostics.Record("AI tag preferences", error); } _previewRequest?.Cancel(); _preview.Source = null; _previewBitmap?.Dispose(); _lifetime.Dispose(); };
        AddPaths(initial ?? []);
    }
    public void ImportPaths(IEnumerable<string> paths) => AddPaths(paths);
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_closed) return;
        foreach (var path in paths.Where(File.Exists).Where(MediaTagService.Supports).Select(Path.GetFullPath).Distinct(BatchRename.PathComparer))
        {
            if (_entries.Any(entry => BatchRename.PathComparer.Equals(entry.Path, path))) continue;
            var entry = new MediaFileEntry(path) { Details = "", Status = "待分析" };
            entry.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(MediaFileEntry.Include)) UpdateActions();
            };
            _entries.Add(entry);
        }
        if (_list.SelectedItem is null) _list.SelectedItem = _entries.FirstOrDefault();
        UpdateActions();
    }
    private async Task AddFoldersAsync(IEnumerable<string> paths)
    {
        if (_busy || _closed) return;
        var recursive = _recursive.IsChecked == true;
        SetBusy(true); _status.Text = Localization.Text("读取文件…");
        try
        {
            var files = await Task.Run(() => BatchRename.CollectMedia(paths, recursive, _lifetime.Token), _lifetime.Token);
            AddPaths(files);
            if (!_closed) _status.Text = Localization.Format($"已添加 {files.Length} 个文件");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "导入失败", error.Message); }
        finally { if (!_closed) SetBusy(false); }
    }
    private async Task RefreshModelAsync()
    {
        try
        {
            var installed = await new ModelStore().IsInstalledAsync(ModelCatalog.JoyTagId, ct: _lifetime.Token);
            if (_closed) return;
            _modelReady = installed;
            var missingBytes = installed ? 0 : ModelCatalog.Find(ModelCatalog.JoyTagId).DownloadSize;
            if (NeedsSemanticModel && !await new ModelStore().IsInstalledAsync(ModelCatalog.EmbeddingId, ct: _lifetime.Token))
                missingBytes += ModelCatalog.Find(ModelCatalog.EmbeddingId).DownloadSize;
            if (_closed) return;
            _modelStatus.Text = missingBytes == 0 ? "" : Localization.Format($"首次分析需要下载模型 · 约 {Math.Ceiling(missingBytes / 1_000_000d):0} MB");
            _modelStatus.IsVisible = missingBytes > 0; UpdateActions();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) _modelStatus.Text = error.Message; }
    }
    private static double Number(NumericUpDown control)
    {
        if (!decimal.TryParse(control.Text, System.Globalization.NumberStyles.Number, control.NumberFormat, out var number)
            || number < control.Minimum || number > control.Maximum) throw new ArgumentException("请输入范围内的参数。");
        return (double)number;
    }
    private Task ManageModelsAsync() => ManageModelsAsync(this);
    private async Task ManageModelsAsync(Window owner)
    {
        await _manageModels(owner);
        if (_closed) return;
        await RefreshModelAsync();
    }
    private bool CanAnalyzeNotification(string[] paths) => !_closed && !_busy && _entries.Any(entry => paths.Contains(entry.Path, BatchRename.PathComparer));
    private async Task AnalyzeAsync(string[]? requestedPaths = null)
    {
        if (_busy || _writingTxt) return;
        var paths = _entries.Where(entry => requestedPaths is null ? entry.Include : requestedPaths.Contains(entry.Path, BatchRename.PathComparer)).Select(entry => entry.Path).ToArray();
        if (paths.Length == 0) { await Ui.Message(this, "AI 标签", "请添加并勾选图片或视频。"); return; }
        MediaTagOptions options;
        try
        {
            var frames = Number(_frames); if (frames != Math.Truncate(frames)) throw new ArgumentException("采样帧数须为整数。");
            options = new((int)frames, _gpu.IsChecked == true, _reuse.IsChecked == true, RecognizeScenes: _sceneTags.IsChecked == true)
                { SemanticCandidates = SemanticLibraryCandidates };
            options.Validate(); Number(_threshold); SavePreferences();
        }
        catch (Exception error) { await Ui.Message(this, "参数错误", error.Message); return; }
        foreach (var entry in _entries.Where(entry => paths.Contains(entry.Path, BatchRename.PathComparer)))
        { _results.Remove(entry.Path); _liveResults.Remove(entry.Path); _traces.Remove(entry.Path); _positions.Remove(entry.Path); _editedTags.Remove(entry.Path); entry.Status = "待分析"; entry.Details = ""; entry.Keyword = ""; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; SetBusy(true); RenderSelectedResult();
        _status.Text = Localization.Text(_modelReady ? "准备分析…" : "下载标签模型");
        _activity.Update(new("加载标签模型", "JoyTag", DateTime.UtcNow, DateTime.UtcNow));
        var progress = new Progress<MediaTagProgress>(update =>
        {
            if (_closed || _operation != operation) return;
            if (update.Activity is { } activity)
            {
                _activity.Update(activity);
                _status.Text = $"{update.Completed} / {update.Total} · {Path.GetFileName(update.Path)}";
            }
            var entry = _entries.FirstOrDefault(entry => BatchRename.PathComparer.Equals(entry.Path, update.Path)); if (entry is null) return;
            if (update.PreviewResult is { } preview)
            {
                _liveResults[preview.Path] = preview;
                if (_followLive.IsChecked == true) _positions[preview.Path] = (preview.Scenes?.Frames.LastOrDefault()?.Seconds ?? preview.Frames.LastOrDefault()?.Seconds) ?? 0;
                entry.Details = string.Join(" · ", ResultTags(preview).Take(5).Select(tag => tag.Label));
                if (_list.SelectedItem == entry) { RenderSelectedResult(); if (_followLive.IsChecked == true) _ = RefreshSelectedPreviewAsync(CursorFor(preview)); }
            }
            if (update.Result is null && update.Error is null) { entry.Status = Localization.Text(update.Activity?.Stage ?? "处理中"); return; }
            if (update.Result is { } result) { _results[result.Path] = result; _liveResults.Remove(result.Path); ShowResult(entry, result); }
            else { entry.Status = Localization.Text("失败"); entry.Details = update.Error ?? ""; }
            _status.Text = $"{update.Completed} / {update.Total}";
            RenderSelectedResult();
        });
        try
        {
            if (!_modelReady)
            {
                var started = DateTime.UtcNow;
                var download = new Progress<ModelDownloadProgress>(update =>
                {
                    if (_closed || _operation != operation) return;
                    _status.Text = Localization.Text(update.Stage);
                    _activity.Update(new("下载标签模型", "JoyTag", started, DateTime.UtcNow)
                    { Current = update.Received, Total = update.Total, Unit = "字节", Detail = update.Source });
                });
                await new ModelStore().DownloadAsync(ModelCatalog.JoyTagId, download, operation.Token);
                _modelReady = true; _modelStatus.IsVisible = false;
            }
            var results = await new MediaTagService(_engine).AnalyzeAsync(paths, options, progress, operation.Token);
            if (_closed) return;
            foreach (var result in results) { _results[result.Path] = result; _liveResults.Remove(result.Path); }
            _status.Text = Localization.Format($"完成 {results.Count} / {paths.Length} 个文件");
            _activity.Finish(AiActivityState.Completed, "标签分析完成");
            var failedPaths = paths.Where(path => !_results.ContainsKey(path)).ToArray();
            var sceneFailedPaths = results.Where(result => result.SceneError is not null).Select(result => result.Path).ToArray();
            var resultActions = new List<Notifications.NotificationAction> {
                    new("查看结果", () => Notifications.NotificationCenter.ShowOwnerAsync(this), Primary: failedPaths.Length == 0, Enabled: () => !_closed),
                    new("生成同目录 TXT", () => SaveTextReportsAsync(paths), Enabled: () => !_closed && !_busy && !_writingTxt),
                    new("导出标签 JSON…", ExportAsync, Enabled: () => !_closed && !_busy),
                    new("重新分析", () => { _ = AnalyzeAsync(paths); return Task.CompletedTask; }, Enabled: () => CanAnalyzeNotification(paths)) };
            if (failedPaths.Length > 0) resultActions.Insert(0, new("重试失败文件", () => { _ = AnalyzeAsync(failedPaths); return Task.CompletedTask; }, Primary: true,
                Enabled: () => CanAnalyzeNotification(failedPaths)));
            if (sceneFailedPaths.Length > 0) resultActions.Insert(0, new("重试语义识别", () => { _ = AnalyzeAsync(sceneFailedPaths); return Task.CompletedTask; },
                Enabled: () => CanAnalyzeNotification(sceneFailedPaths)));
            Notifications.NotificationCenter.Shared.Publish(this, new(Guid.NewGuid().ToString("N"), "标签分析完成",
                sceneFailedPaths.Length == 0 ? (FormattableString)$"成功 {results.Count} 个，失败 {failedPaths.Length} 个。"
                    : $"标签完成 {results.Count}/{paths.Length} 个 · 语义识别失败 {sceneFailedPaths.Length} 个",
                failedPaths.Length == 0 && sceneFailedPaths.Length == 0 ? Notifications.NotificationKind.Success : Notifications.NotificationKind.Warning, resultActions));
            foreach (var entry in _entries.Where(entry => paths.Contains(entry.Path, BatchRename.PathComparer)))
                if (_results.TryGetValue(entry.Path, out var result)) ShowResult(entry, result);
            RenderSelectedResult();
            if (_autoTxt.IsChecked == true) await SaveTextReportsAsync(results.Select(result => result.Path).ToArray());
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                _activity.Finish(AiActivityState.Cancelled, "已停止"); _status.Text = Localization.Text("已停止，已完成结果已保留");
                foreach (var entry in _entries.Where(entry => paths.Contains(entry.Path, BatchRename.PathComparer) && !_results.ContainsKey(entry.Path)))
                    if (entry.Status != Localization.Text("失败")) entry.Status = Localization.Text("已停止");
            }
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                _activity.Finish(AiActivityState.Failed, "分析失败");
                _status.Text = Localization.Text("分析失败");
                foreach (var entry in _entries.Where(entry => paths.Contains(entry.Path, BatchRename.PathComparer) && !_results.ContainsKey(entry.Path)))
                { entry.Status = Localization.Text("失败"); entry.Details = error.Message; }
                Notifications.NotificationCenter.Shared.Publish(this, new(Guid.NewGuid().ToString("N"), "分析失败", error.Message, Notifications.NotificationKind.Error, [
                    new("重试分析", () => { _ = AnalyzeAsync(paths); return Task.CompletedTask; }, Primary: true, Enabled: () => CanAnalyzeNotification(paths)),
                    new("模型管理", ManageModelsAsync, Enabled: () => !_closed)]));
            }
        }
        finally { _operation = null; if (!_closed) { SetBusy(false); RenderSelectedResult(); await RefreshModelAsync(); } }
    }
    private void ShowResult(MediaFileEntry entry, MediaTagResult result)
    {
        var tags = ResultTags(result).ToArray();
        entry.Status = result.SceneError is null ? Localization.Format($"已识别 {tags.Length} 个标签")
            : Localization.Format($"已识别 {tags.Length} 个标签 · 语义识别失败");
        entry.Details = string.Join(" · ", tags.Take(5).Select(tag => tag.Label));
    }
    private static string NsfwStateText(NsfwSignalState state) => Localization.Text(state switch
    {
        NsfwSignalState.Suspected => "疑似 NSFW",
        NsfwSignalState.ContextOnly => "仅命中提示标签",
        _ => "未检出风险标签"
    });
    private async Task RenameAsync(bool undo, RenameItem[]? plan = null)
    {
        if (_busy || !_canRename() || !undo && plan is null) return;
        _renaming = true; SetBusy(true);
        try
        {
            var mappings = await Task.Run(() => undo ? BatchRename.UndoRename(_journal) : BatchRename.ApplyRename(plan!, _journal));
            var retained=mappings.Select(mapping=>(Mapping:mapping,Result:_results.GetValueOrDefault(mapping.Source),
                Tags:_editedTags.GetValueOrDefault(mapping.Source),Position:_positions.GetValueOrDefault(mapping.Source),Traces:_traces.GetValueOrDefault(mapping.Source),ReportSource:_reportSources.GetValueOrDefault(mapping.Source),Entry:_entries.FirstOrDefault(entry=>BatchRename.PathComparer.Equals(entry.Path,mapping.Source)))).ToArray();
            foreach(var item in retained){_results.Remove(item.Mapping.Source);_editedTags.Remove(item.Mapping.Source);_liveResults.Remove(item.Mapping.Source);_positions.Remove(item.Mapping.Source);_traces.Remove(item.Mapping.Source);_reportSources.Remove(item.Mapping.Source);}
            foreach(var item in retained)
            {
                if(item.Result is {} result)_results[item.Mapping.Target]=result with { Path=item.Mapping.Target };
                _positions[item.Mapping.Target]=item.Position;
                if(item.Traces is {} traces)_traces[item.Mapping.Target]=traces;
                if(item.ReportSource is {} reportSource)_reportSources[item.Mapping.Target]=reportSource;
                if(item.Tags is {} tags)_editedTags[item.Mapping.Target]=tags;
                if(item.Entry is {} entry){entry.Renamed(item.Mapping.Target);entry.Status=Localization.Text("已重命名");entry.Keyword="";}
            }
            Renamed?.Invoke(mappings); _status.Text = Localization.Format($"已更新 {mappings.Length} 个文件名"); _undo.IsVisible = CanUndo();
        }
        catch (Exception error) { await Ui.Message(this, "重命名失败", error.Message); }
        finally { _renaming = false; SetBusy(false); RenderSelectedResult(); }
    }
    private async Task ExportAsync()
    {
        if (_results.Count == 0) return;
        try
        {
            var threshold = Number(_threshold); SavePreferences();
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("导出标签"), SuggestedFileName = "ai-tags.json", DefaultExtension = "json" });
            if (file is null) return;
            var report = new { Model = ModelCatalog.JoyTagId, Threshold = threshold, SceneThreshold = _sceneThreshold.Value, SceneMargin = _sceneMargin.Value, ScoreMode = _scoreMode.SelectedIndex, Results = _results.Values.Select(result => new
            { result.Path, result.Backend, result.FallbackReason, result.SampledFrames, result.InferredFrames,
                DictionarySha256 = NsfwModeration.DictionarySha256, Moderation = NsfwModeration.Evaluate(result, threshold),
                Tags = ResultTags(result).ToArray(), result.DurationSeconds, Vocabulary = result.Scores.Select(score => score.Tag).ToArray(), Evidence=result.Frames, result.Scenes, result.SceneError }) };
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error) { await Ui.Message(this, "导出失败", error.Message); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; UpdateActions();
    }
    private bool CanUndo()
    {
        try { return JsonSerializer.Deserialize<BatchRename.RenameJournal>(File.ReadAllText(_journal))?.State == "completed"; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
}
