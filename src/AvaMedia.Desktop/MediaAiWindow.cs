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
    private readonly MediaTagService _tagService;
    private readonly LocalModelWarmupController _captionWarmup;
    private readonly ObservableCollection<MediaFileEntry> _entries = [];
    private readonly Dictionary<string, MediaTagResult> _results = new(BatchRename.PathComparer);
    private readonly ListBox _list = new() { Name = "MediaAiFiles" };
    private readonly WrapPanel _imports = new();
    private readonly NumericUpDown _threshold = new() { Minimum = .05m, Maximum = .95m, Value = .4m, Increment = .05m };
    private readonly NumericUpDown _frames = new() { Minimum = 1, Maximum = 32, Value = 8, Increment = 1 };
    private readonly CheckBox _reuse = new() { Content = "复用相似画面", IsChecked = true };
    private readonly CheckBox _sceneTags = new() { Content = "识别场景、照明与面部", IsChecked = false };
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹", IsChecked = true };
    private readonly TextBlock _status = Ui.Status("就绪");
    private readonly TextBlock _modelStatus = Ui.Text("读取模型状态…", "caption");
    private readonly Controls.AiActivityView _activity = new() { Compact = true };
    private readonly Button _analyze = new() { Name = "MediaAiAnalyze", Content = "开始分析", Classes = { "primary", "dialog-action" } };
    private readonly Button _rename = new() { Content = "标签重命名…" };
    private readonly Button _undo = new() { Content = "撤销重命名" };
    private readonly Button _pause = new() { Content = "暂停任务", IsVisible = false };
    private readonly Button _stop = new() { Name = "MediaAiStop", Content = "结束任务", IsVisible = false, Classes = { "primary", "dialog-action" } };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<Window, Task> _manageModels;
    private readonly Func<bool> _canRename;
    private readonly Func<IEnumerable<string>, IDisposable>? _reserveFiles;
    private readonly Action<IReadOnlyList<Job>, bool>? _enqueue;
    private readonly Action? _showQueue;
    private readonly Button _enqueueQueue = new() { Content = "加入任务队列" };
    private readonly Button _viewQueue = new() { Content = "查看任务队列" };
    private readonly string _journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "ai-rename.json");
    private CancellationTokenSource? _operation;
    private bool _closed, _renaming, _busy, _modelReady;
    public event Action<IReadOnlyList<RenameItem>>? Renamed;

    public MediaAiWindow(IMediaEngine engine, AppSettings settings, IEnumerable<string>? initial, Func<Window, Task> manageModels, Func<bool>? canRename=null, Storage? storage=null,
        Action<IReadOnlyList<Job>, bool>? enqueue = null, Action? showQueue = null, Action<Job>? stopTask = null, Action? newTask = null, Action<Job>? pauseTask = null, Action<Job>? resumeTask = null,
        Func<IEnumerable<string>, IDisposable>? reserveFiles = null)
    {
        _manageModels = manageModels; _canRename=canRename??(()=>true); _storage=storage??new Storage();
        _enqueue = enqueue; _showQueue = showQueue; _stopTask = stopTask; _newTask = newTask;
        _pauseTask = pauseTask; _resumeTask = resumeTask; _reserveFiles = reserveFiles;
        _engine = engine; _settings = settings;
        _tagService = new(engine); MediaTagRuntime.Configure(settings);
        LoadPreferences();
        if (!ModelCatalog.Find(ModelCatalog.EmbeddingId).Supported) { _sceneTags.IsChecked = false; _sceneTags.IsEnabled = false; }
        // These inputs live in the optional settings dialog, so initialize text before any template is attached.
        _threshold.Text = _threshold.Value?.ToString(_threshold.NumberFormat);
        _frames.Text = _frames.Value?.ToString(_frames.NumberFormat);
        Title = Catalog.Find("media-ai").Label; Width = 1240; Height = 820; MinWidth = 1000; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Controls.WindowArtwork.SetKind(this, "image");
        BuildInterface();
        _captionWarmup = new(this, engine, settings);
        _captionWarmup.Changed += UpdateModelPreparationActions;
        _settings.NsfwContentChanged += PrivacyChanged; UpdatePrivacyScopes();
        var queueState=new Avalonia.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
        queueState.Tick+=(_,_)=>UpdateActions();Opened+=(_,_)=>queueState.Start();Closed+=(_,_)=>queueState.Stop();
        InitializeWordLibraries();
        Opened += async (_, _) => await RefreshModelAsync();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _busy ? DragDropEffects.None : DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, async (_, e) => { if (!_busy) await AddFoldersAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, e) => { if (_renaming) { e.Cancel = true; return; } _closed = true; _operation?.Cancel(); _lifetime.Cancel(); };
        Closed += (_, _) => { _settings.NsfwContentChanged -= PrivacyChanged; DetachTaskObservers(); try { SavePreferences(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppDiagnostics.Record("AI tag preferences", error); } _previewRequest?.Cancel(); _preview.Source = null; _previewBitmap?.Dispose(); _lifetime.Dispose(); };
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
                if (change.PropertyName == nameof(MediaFileEntry.Include)) { UpdateActions(); UpdateCaptionWarmup(); }
            };
            _entries.Add(entry);
        }
        if (_list.SelectedItem is null) _list.SelectedItem = _entries.FirstOrDefault();
        UpdateActions(); UpdateCaptionWarmup();
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
            var store = new ModelStore();
            var required = _settings.EnableNsfwContent && _realPeople.IsChecked == true ? new[] { ModelCatalog.JoyTagId, ModelCatalog.NsfwId } : [ModelCatalog.JoyTagId];
            var missing = new List<string>();
            foreach (var id in required) if (!await store.IsInstalledAsync(id, ct: _lifetime.Token)) missing.Add(id);
            var installed = missing.Count == 0;
            if (_closed) return;
            _modelReady = installed;
            var semanticMissing = NeedsSemanticModel && SemanticModelConsent.Model.Supported && !await SemanticModelConsent.IsInstalledAsync(_lifetime.Token);
            if (_closed) return;
            // Sizes come from the catalog: tags are required, the semantic model only when scenes or semantic words are selected.
            var parts = new List<string>();
            if (!installed)
                parts.Add(Localization.Format($"首次分析需要下载标签模型 · 约 {SemanticModelConsent.Megabytes(missing.Sum(id => ModelCatalog.Find(id).DownloadSize))} MB"));
            if (semanticMissing)
                parts.Add(Localization.Format($"场景识别另需语义模型 · 约 {SemanticModelConsent.Megabytes(SemanticModelConsent.Model.DownloadSize)} MB（开始前询问）"));
            if (_generateCaptions.IsChecked == true && _captionLocalModelId is { } captionId
                && (!await store.IsInstalledAsync(captionId, ct: _lifetime.Token)
                    || !await store.IsInstalledAsync(ModelCatalog.SummaryRuntimeId, ct: _lifetime.Token)))
                parts.Add(Localization.Format($"首次生成画面描述需要下载模型 · 约 {SemanticModelConsent.Megabytes(ModelCatalog.Find(captionId).DownloadSize + ModelCatalog.Find(ModelCatalog.SummaryRuntimeId).DownloadSize)} MB"));
            if (_closed) return;
            _modelStatus.Text = string.Join(Environment.NewLine, parts);
            _modelStatus.IsVisible = parts.Count > 0; UpdateActions();
            UpdateModelPreparationActions();
            UpdateCaptionWarmup();
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
        _captionWarmup.RefreshModels();
    }
    private Task AnalyzeAsync(string[]? requestedPaths = null) => QueueAnalysisAsync(true, requestedPaths);

    private MediaTagTaskOptions ReadTaskOptions()
    {
        var analysis = ReadAnalysisOptions(_entries.Any(entry => entry.Include && VideoFormats.IsVideo(entry.Path)));
        var options = new MediaTagTaskOptions
        {
            Analysis = analysis,
            Threshold = Number(_threshold),
            SceneThreshold = _sceneThreshold.Value,
            SceneMargin = (double)(_sceneMargin.Value ?? .03m),
            WriteTextReport = _autoTxt.IsChecked == true,
            OnlyLibrary = _onlyLibrary.IsChecked == true,
            LibraryCandidates = _libraryCandidates.ToArray()
        };
        options.Validate(); SavePreferences();
        return options;
    }

    private MediaTagOptions ReadAnalysisOptions(bool hasVideo)
    {
        var frames = hasVideo ? Number(_frames) : 1;
        if (frames != Math.Truncate(frames)) throw new ArgumentException("采样帧数须为整数。");
        return new((int)frames, hasVideo && _reuse.IsChecked == true, BatchSize: 1,
            RecognizeScenes: _sceneTags.IsChecked == true, GenerateCaptions: _generateCaptions.IsChecked == true, CaptionPrompt: _captionPrompt)
        {
            SemanticCandidates = SemanticLibraryCandidates,
            RealPeopleOnly = _realPeople.IsChecked == true, RecognizeNsfw = _settings.EnableNsfwContent && _realPeople.IsChecked == true,
            CaptionSystemPrompt = _captionSystemPrompt, CaptionUseFrameTools = _captionUseFrameTools,
            CaptionLocalModelId = _captionLocalModelId,
            CaptionProviderId = _captionLocalModelId is null ? _settings.OnlineAi.DefaultProviderId : null
        };
    }

    private Task EnqueueSelectedAsync() => QueueAnalysisAsync(false);

    private void ShowQueuedTasks()
    {
        if (_showQueue is null) return;
        _showQueue();
    }

    private void ShowResult(MediaFileEntry entry, MediaTagResult result)
    {
        var tags = ResultTags(result).ToArray();
        entry.Status = result.SceneError is null ? Localization.Format($"已识别 {tags.Length} 个标签")
            : result.SceneSkipped ? Localization.Format($"已识别 {tags.Length} 个标签 · 已跳过场景识别")
            : Localization.Format($"已识别 {tags.Length} 个标签 · 语义识别失败");
        if (result.CaptionError is not null) entry.Status = Localization.Join(" · ", [entry.Status, Localization.Text("画面描述失败")]);
        entry.Details = string.Join(" · ", tags.Take(5).Select(tag => tag.Label));
    }
    private static string NsfwStateText(NsfwSignalState state) => Localization.Text(state switch
    {
        NsfwSignalState.Suspected => "疑似 NSFW",
        NsfwSignalState.ContextOnly => "命中 NSFW 相关提示，未达风险阈值",
        _ => "未检出风险标签"
    });
    private async Task RenameAsync(bool undo, RenameItem[]? plan = null)
    {
        if (_busy || !_canRename() || !undo && plan is null) return;
        _renaming = true; SetBusy(true);
        try
        {
            using var reservation = _reserveFiles?.Invoke(await SourceFileChanges.RenamePathsAsync(_journal, undo, plan));
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
        var results = _entries.Where(entry => entry.Include && _results.ContainsKey(entry.Path)).Select(entry => _results[entry.Path]).ToArray();
        if (results.Length == 0) return;
        try
        {
            var threshold = Number(_threshold); SavePreferences();
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("导出标签"), SuggestedFileName = "ai-tags.json", DefaultExtension = "json" });
            if (file is null) return;
            var report = new { Model = ModelCatalog.JoyTagId, Threshold = threshold, SceneThreshold = _sceneThreshold.Value, SceneMargin = _sceneMargin.Value, ScoreMode = _scoreMode.SelectedIndex, GenerateCaptions = _generateCaptions.IsChecked == true, Results = results.Select(result =>
            {
                var safe = MediaPrivacy.Filter(result, _settings.EnableNsfwContent, _privateLibraryLabels);
                return new { safe.Path, safe.IsPartial, safe.Backend, safe.FallbackReason, safe.SampledFrames, safe.InferredFrames,
                    safe.RealPeopleOnly, safe.Nsfw,
                    DictionarySha256 = NsfwModeration.DictionarySha256, Moderation = _settings.EnableNsfwContent ? NsfwModeration.Evaluate(result, threshold) : null,
                    Tags = ResultTags(result).ToArray(), safe.DurationSeconds, Vocabulary = safe.Scores.Select(score => score.Tag).ToArray(), Evidence = safe.Frames,
                    safe.Scenes, safe.SceneError, safe.Caption, safe.CaptionModel, safe.CaptionError };
            }).ToArray() };
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
