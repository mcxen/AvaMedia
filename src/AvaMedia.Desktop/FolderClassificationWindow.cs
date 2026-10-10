using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly Func<bool> _canMove;
    private readonly Func<IEnumerable<string>, IDisposable>? _reserveFiles;
    private readonly Func<Window, Task> _manageModels;
    private readonly Storage _storage = new();
    private readonly ObservableCollection<MediaFileEntry> _entries = [];
    private readonly ObservableCollection<FolderClassificationRule> _rules = [];
    private readonly ObservableCollection<FolderClassificationRule> _savedRules = [];
    private FolderClassificationRule[] _defaultRules = FolderClassificationRule.DefaultRules();
    private readonly Dictionary<string, FolderClassifiedFile> _results = new(BatchRename.PathComparer);
    private readonly List<string> _inputs = [];
    private readonly HashSet<string> _analysisPending = new(BatchRename.PathComparer);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private FolderOrganizationItem[]? _plan;
    private bool _closed, _busy, _writing, _syncing, _attempted;
    private string? _lastJournal;
    public event Action<IReadOnlyList<RenameItem>>? Moved;

    public sealed class Preferences
    {
        public FolderClassificationRule[] SceneRules { get; set; } = FolderClassificationRule.DefaultRules();
        public FolderClassificationRule[] SavedRules { get; set; } = [];
        public string OutputFolder { get; set; } = "";
        public bool Recursive { get; set; } = true;
        public bool SplitTypes { get; set; } = true;
        public bool WriteText { get; set; } = true;
        public bool PreferGpu { get; set; }
        public int VideoFrames { get; set; } = 12;
        public decimal TagThreshold { get; set; } = .5m;
        public string? LastJournal { get; set; }
    }

    public FolderClassificationWindow(IMediaEngine engine, IEnumerable<string>? initial, Func<bool> canMove,
        Func<Window, Task> manageModels, Action<Job> enqueueTask, Func<Job, Task> resumeTask,
        Action<Job> stopTask, Action newTask, Action showTasks, AppSettings? settings = null,
        Action<Job>? pauseTask = null, Func<IEnumerable<string>, IDisposable>? reserveFiles = null)
    {
        _engine = engine; _canMove = canMove; _manageModels = manageModels;
        _pauseTask = pauseTask; _reserveFiles = reserveFiles;
        _enqueueTask = enqueueTask; _resumeTask = resumeTask; _stopTask = stopTask; _newTask = newTask; _showTasks = showTasks;
        _settings = settings ?? engine.Settings;
        Title = Catalog.Find("folder-classification").Label; Width = 1360; Height = 840; MinWidth = 920; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "gear");
        LoadPreferences(); BuildInterface(); UpdateActions();
        RefreshSavedVisibility(); _settings.NsfwContentChanged += PrivacyChanged;
        _files.SelectionChanged += (_, _) => { if (!_renderingBoard) RenderDetails(); };
        _files.DoubleTapped += (_, _) => OpenSelected();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) =>
        { if (!args.Handled) args.DragEffects = !_busy && args.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; });
        AddHandler(DragDrop.DropEvent, async (_, args) =>
        {
            if (!args.Handled && !_busy) await ImportPathsAsync(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []);
        });
        Closing += (_, args) =>
        {
            if (_writing) { args.Cancel = true; _operation?.Cancel(); return; }
            if (!_closingView && !TaskActive && _taskJob is not null)
            { args.Cancel = true; _ = SaveAndCloseAsync(); return; }
            _closed = true; _boardTimer.Stop(); _lifetime.Cancel(); _operation?.Cancel();
            _coverCache.Dispose();
        };
        Closed += (_, _) =>
        {
            if (_taskJob is { } task) task.PropertyChanged -= TaskChanged;
            _settings.NsfwContentChanged -= PrivacyChanged; _files.ItemsSource = null; _baskets.Children.Clear(); _basketViews.Clear(); _selectedCover.Path = null; _lifetime.Dispose();
        };
        if (initial is not null) Opened += async (_, _) => await ImportPathsAsync(initial);
    }

    private void LoadPreferences()
    {
        var saved = _storage.LoadToolOptions<Preferences>("folder-classification") ?? new();
        try { FolderClassification.ValidateRules(saved.SceneRules); }
        catch (Exception error) when (error is ArgumentException or NullReferenceException) { saved.SceneRules = FolderClassificationRule.DefaultRules(); }
        try { ValidateSavedRules(saved.SavedRules); }
        catch (Exception error) when (error is ArgumentException or NullReferenceException) { saved.SavedRules = []; }
        _defaultRules = saved.SceneRules;
        foreach (var rule in saved.SceneRules)
            if (RuleVisible(rule)) _rules.Add(rule); else _disabledNsfwRules.Add(rule);
        foreach (var rule in saved.SavedRules) _savedRules.Add(rule);
        _output.Text = saved.OutputFolder; _recursive.IsChecked = saved.Recursive;
        _splitTypes.IsChecked = saved.SplitTypes; _writeText.IsChecked = saved.WriteText; _gpu.IsChecked = saved.PreferGpu;
        _frames.Value = Math.Clamp(saved.VideoFrames, 1, 32); _tagThreshold.Value = Math.Clamp(saved.TagThreshold, 0, 1);
        _lastJournal = saved.LastJournal;
    }

    private void SavePreferences(FolderClassificationRule[]? defaultRules = null, FolderClassificationRule[]? savedRules = null)
        => _storage.SaveToolOptions("folder-classification", new Preferences
    {
        SceneRules = defaultRules ?? _defaultRules, SavedRules = savedRules ?? _savedRules.ToArray(),
        OutputFolder = _output.Text ?? "", Recursive = _recursive.IsChecked == true,
        SplitTypes = _splitTypes.IsChecked == true, WriteText = _writeText.IsChecked == true, PreferGpu = _gpu.IsChecked == true,
        VideoFrames = (int)(_frames.Value ?? 12), TagThreshold = _tagThreshold.Value ?? .5m, LastJournal = _lastJournal
    });

    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (_busy || _closed) return;
        var incoming = paths.Select(Path.GetFullPath).Where(path => Directory.Exists(path) || File.Exists(path)).ToArray();
        if (incoming.Length == 0) return;
        if (string.IsNullOrWhiteSpace(_output.Text))
        {
            var first = incoming[0];
            _output.Text = Path.Combine(Directory.Exists(first) ? first : Path.GetDirectoryName(first)!, "分类结果");
        }
        foreach (var path in incoming) if (!_inputs.Contains(path, BatchRename.PathComparer)) _inputs.Add(path);
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_busy || _inputs.Count == 0) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation;
        SetBusy(true); _status.Text = Localization.Text("扫描文件夹…");
        try
        {
            var output = string.IsNullOrWhiteSpace(_output.Text) ? null : Path.GetFullPath(_output.Text);
            if (output is not null && _inputs.Where(Directory.Exists).Any(folder => FolderClassification.IsWithin(folder, output)))
                throw new ArgumentException("分类目录不能是源文件夹本身或其上级目录。");
            var recursive = _recursive.IsChecked == true;
            var inputs = _inputs.ToArray();
            var scan = await Task.Run(() => FolderClassification.Scan(inputs, recursive, output, operation.Token), operation.Token);
            if (_closed) return;
            var retained = _entries.ToDictionary(entry => entry.Path, BatchRename.PathComparer);
            _entries.Clear();
            foreach (var path in scan.Files)
            {
                var entry = retained.GetValueOrDefault(path) ?? new MediaFileEntry(path) { Status = Localization.Text("待分析") };
                if (!retained.ContainsKey(path)) entry.PropertyChanged += (_, change) =>
                { if (change.PropertyName == nameof(MediaFileEntry.Include)) InvalidatePlan(); };
                _entries.Add(entry);
            }
            foreach (var path in _results.Keys.Where(path => !scan.Files.Contains(path, BatchRename.PathComparer)).ToArray()) _results.Remove(path);
            _analysisPending.RemoveWhere(path => !scan.Files.Contains(path, BatchRename.PathComparer));
            InvalidatePlan(); SavePreferences();
            _status.Text = Localization.Format($"已扫描 {scan.Files.Length} 个媒体文件，无法访问 {scan.Errors.Length} 项");
            _scanErrors.Text = string.Join(Environment.NewLine, scan.Errors); _scanErrors.IsVisible = scan.Errors.Length > 0;
            if (_files.SelectedItem is null && _entries.Count > 0) _files.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("已停止"); }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "扫描失败", error.Message); }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private void InvalidatePlan()
    {
        if (_syncing) return;
        _plan = null;
        foreach (var entry in _entries) entry.NewName = "";
        UpdateActions(); QueueBoardRefresh();
    }

    private void InvalidateAnalysis()
    {
        if (_syncing) return;
        _attempted = false;
        _results.Clear(); _hiddenPrivateResults.Clear(); _analysisPending.Clear();
        foreach (var entry in _entries) { entry.Status = Localization.Text("待分析"); entry.Details = entry.Path; }
        InvalidatePlan(); RenderDetails();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy; _imports.IsEnabled = _settingsPanel.IsEnabled = _rulesPanel.IsEnabled = !busy;
        _stop.IsVisible = busy; UpdateActions(); RenderBoard();
    }
    private void UpdateActions()
    {
        _videoSettings.IsVisible = _entries.Any(entry => entry.Include && VideoFormats.IsVideo(entry.Path));
        _analyze.IsEnabled = !_busy && _entries.Any(entry => entry.Include);
        _retry.IsEnabled = !_busy && _entries.Any(entry => entry.Include && AnalysisPending(entry));
        _retry.IsVisible = _attempted;
        _preview.IsEnabled = !_busy && _results.Count > 0;
        _organize.IsEnabled = !_busy && _plan is { Length: > 0 };
        _undo.IsEnabled = !_busy && FolderOrganization.CanUndo(_lastJournal);
        _export.IsEnabled = !_busy && _results.Count > 0;
        _analyze.Classes.Set("primary", _plan is not { Length: > 0 });
        _organize.Classes.Set("primary", _plan is { Length: > 0 });
        RefreshRuleActions();
    }

    private void SelectEntries(Func<MediaFileEntry, bool> select)
    {
        _syncing = true;
        try { foreach (var entry in _entries) entry.Include = select(entry); }
        finally { _syncing = false; }
        InvalidatePlan();
    }

    private void OpenSelected()
    {
        if (_files.SelectedItem is not MediaFileEntry entry || !File.Exists(entry.Path)) return;
        if (ImageFormats.Supports(entry.Path))
        {
            var viewer = new ImageViewerWindow(_entries.Where(item => ImageFormats.Supports(item.Path)).Select(item => item.Path), entry.Path);
            viewer.Show(this); return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.Path) { UseShellExecute = true }); }
        catch (Exception error) { _ = Ui.Message(this, "打开文件失败", error.Message); }
    }
}
