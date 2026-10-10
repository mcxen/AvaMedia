using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed record PersonClipRequest(IReadOnlyList<ClipEditResult> Edits, string OutputFolder, bool OutputToSource, string Preset, bool StartImmediately);

public sealed partial class PersonClipWindow : Window
{
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public PersonClipRange[] Excluded { get; set; } = [];
        public ClipEditResult? Result { get; set; }
        public string Status { get; set; } = "待分析";
        public string Error { get; set; } = "";
        public Job? Task { get; set; }
        public PersonDetectionTaskResult? Observed { get; set; }
        public long Length { get; set; }
        public DateTime WriteUtc { get; set; }
    }
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly Func<Window, string?, Task> _manageModels;
    private readonly List<Entry> _entries = [];
    private readonly ListBox _files = new() { Name = "PersonClipFiles" };
    private readonly ListBox _ranges = new() { Name = "PersonClipExcludedRanges", Height = 200 };
    private readonly TextBox _start = new() { IsReadOnly = true, Name = "PersonClipExcludeStart", Text = "00:00:00.000" };
    private readonly TextBox _end = new() { IsReadOnly = true, Name = "PersonClipExcludeEnd", Text = "00:00:00.000" };
    private readonly NumericUpDown _fps = Number(.25m, 16, 2, .25m);
    private readonly NumericUpDown _threshold = Number(.1m, .9m, .35m, .05m);
    private readonly NumericUpDown _padding = Number(0, 30, .5m, .1m);
    private readonly NumericUpDown _gap = Number(0, 30, 1, .5m);
    private readonly NumericUpDown _minimum = Number(0, 30, .5m, .1m);
    private readonly NumericUpDown _darkThreshold = Number(1, 32, 8, 1);
    private readonly CheckBox _uncertain = new() { Content = "保留不确定片段" };
    private readonly CheckBox _embedding = new() { Content = "使用 EmbeddingGemma 2 语义辅助", IsEnabled = true };
    private readonly CheckBox _gpu = new() { Content = "自动适配 GPU", IsChecked = true };
    private readonly CheckBox _reuseFrames = new() { Content = "复用相似画面", IsChecked = true };
    private readonly CheckBox _dark = new() { Content = "快速排除黑灯画面", IsChecked = true };
    private readonly CheckBox _blank = new() { Content = "快速排除无画面", IsChecked = true };
    private readonly CheckBox _sourceFolder = new() { Content = "输出至源文件目录" };
    private readonly TextBox _folder = new() { IsReadOnly = true, Name = "PersonClipOutputFolder" };
    private readonly TextBlock _modelStatus = Ui.Text("", "caption");
    private readonly TextBlock _status = Ui.Status();
    private readonly StackPanel _rangePanel = new() { Spacing = 8 };
    private readonly Dictionary<string, CheckBox> _detectorBoxes = [];
    private readonly Dictionary<string, TextBlock> _detectorStates = [];
    private readonly Dictionary<string, Button> _detectorDownloads = [];
    private readonly HashSet<string> _installedDetectors = [];
    private readonly ComboBox _detectionMode = new()
    {
        Name = "PersonDetectionMode", ItemsSource = new[] { "平衡检测", "减少漏检", "交叉确认" }, SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox _format = new() { Name = "PersonClipExportFormat", ItemsSource = QuickClipBatch.Presets };
    private readonly Button _submit;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;

    public PersonClipWindow(IMediaEngine engine, AppSettings settings, IEnumerable<string>? paths,
        Func<Window, string?, Task> manageModels, string? outputFolder = null, PersonClipTaskOptions? initial = null,
        bool editing = false, bool? outputToSource = null, Action<IReadOnlyList<Job>, bool>? enqueue = null, Action<Job>? stopTask = null, Action? newTask = null, Action<Job>? pauseTask = null, Action<Job>? resumeTask = null)
    {
        _engine = engine; _settings = settings; _manageModels = manageModels; _enqueue = enqueue; _stopTask = stopTask; _pauseTask = pauseTask; _resumeTask = resumeTask;
        Title = "保留有人片段 · Beta"; Width = 1120; Height = 850; MinWidth = 980; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ToolExecution.SaveOnClose(this, FlushTaskEditsAsync);
        var defaults = initial?.Detection ?? new Storage().LoadToolOptions<PersonClipOptions>("person-clip") ?? new();
        _fps.Value = (decimal)defaults.FramesPerSecond; _threshold.Value = (decimal)defaults.Threshold;
        _padding.Value = (decimal)defaults.PaddingSeconds; _gap.Value = (decimal)defaults.MergeGapSeconds;
        _minimum.Value = (decimal)defaults.MinimumSeconds; _darkThreshold.Value = (decimal)defaults.DarkLumaThreshold;
        _uncertain.IsChecked = defaults.KeepUncertain; _embedding.IsChecked = defaults.UseEmbedding;
        _gpu.IsChecked = initial is null ? settings.AutoDetectGpu : defaults.PreferGpu;
        _reuseFrames.IsChecked = defaults.ReuseSimilarFrames; _dark.IsChecked = defaults.SkipDarkFrames; _blank.IsChecked = defaults.SkipBlankFrames;
        _detectionMode.SelectedIndex = (int)defaults.DetectionMode;
        foreach (var input in new[] { _fps, _threshold, _padding, _gap, _minimum, _darkThreshold }) input.Text = input.Value?.ToString(input.NumberFormat);
        _folder.Text = outputFolder ?? settings.OutputFolder; _sourceFolder.IsChecked = outputToSource ?? settings.OutputToSource;
        _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _sourceFolder.IsCheckedChanged += (_, _) => _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _format.SelectedItem = initial?.ExportPreset ?? QuickClipBatch.DefaultPreset;
        _format.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => Ui.Text(QuickClipBatch.PresetLabel(value ?? "")));

        _submit = Ui.DialogButton(editing ? "保存修改" : "加入任务列表", Submit); _submit.Name = "QueuePersonClips"; _submit.IsEnabled = false;
        _submit.IsDefault = true; _submit.Classes.Add("primary");
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto"), Margin = new(16), RowSpacing = 12 };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(Ui.Button("添加视频…", async () => await AddFilesAsync()));
        tools.Children.Add(Ui.Button("移除", () => { if (!_busy && _files.SelectedIndex >= 0) { if (!SelectedTaskActive) _entries.RemoveAt(_files.SelectedIndex); RefreshFiles(); } }));
        tools.Children.Add(Ui.Button("高级设置…", async () => await OpenAdvancedAsync())); layout.Children.Add(tools);
        var parameters = new StackPanel { Spacing = 8 };
        parameters.Children.Add(Ui.Text("检测模型", "settingsHeading"));
        foreach (var detector in PersonDetectorCatalog.All)
        {
            var checkbox = new CheckBox { Name = "Detector_" + detector.Id, Content = detector.Name,
                IsChecked = defaults.SelectedDetectors.Contains(detector.Id) };
            var state = Ui.Text("读取模型状态…", "caption");
            var download = Ui.Button("下载模型…", async () => await OpenModelsAsync(detector.Id));
            download.Name = "Download_" + detector.Id; download.IsVisible = false; download.Classes.Add("field-action");
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(checkbox); Grid.SetColumn(state, 1); row.Children.Add(state); Grid.SetColumn(download, 2); row.Children.Add(download);
            parameters.Children.Add(row); _detectorBoxes.Add(detector.Id, checkbox); _detectorStates.Add(detector.Id, state); _detectorDownloads.Add(detector.Id, download);
            checkbox.IsCheckedChanged += (_, _) => UpdateDetectorSelection();
        }

        _detectionMode.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => Ui.Text(value ?? ""));
        foreach (var (label, input) in new[] { ("每秒采样帧数", _fps), ("检测阈值", _threshold), ("前后保留秒数", _padding),
            ("合并间隔秒数", _gap), ("最短片段秒数", _minimum) }) AddField(parameters, label, input);
        parameters.Children.Add(_dark); AddField(parameters, "黑灯亮度阈值", _darkThreshold); parameters.Children.Add(_blank);
        _darkThreshold.IsEnabled = _dark.IsChecked == true;
        _dark.IsCheckedChanged += (_, _) => _darkThreshold.IsEnabled = _dark.IsChecked == true;
        parameters.Children.Add(_uncertain); parameters.Children.Add(_embedding); parameters.Children.Add(_gpu); parameters.Children.Add(_reuseFrames); parameters.Children.Add(_modelStatus);
        _advancedParameters = parameters;
        parameters.Children.Add(Ui.Button("模型管理…", async () => await OpenModelsAsync(null)));
        var left = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12 };
        var profile = new StackPanel { Spacing = 8 }; AddField(profile, "检测档位", _detectionMode); left.Children.Add(profile);
        var preview = new MediaPreviewPanel(engine);
        var sourceArea = new Grid { RowDefinitions = new("*,240"), RowSpacing = 8 }; sourceArea.Children.Add(_files); Grid.SetRow(preview,1); sourceArea.Children.Add(preview);
        Grid.SetRow(sourceArea, 1); left.Children.Add(sourceArea);
        _files.SelectionChanged += (_,_) => preview.SetSource(Selected?.Path); Closed += (_,_) => preview.Dispose();
        var analyzeActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _analyze = Ui.Button("开始分析", async () => await AnalyzeAsync()); _analyze.Classes.Add("primary");
        StableLayout.Reserve(_analyze, "开始分析", "后台检测", "后台重新检测");
        StableLayout.Reserve(_pause, "暂停任务", "继续任务");
        _stop = Ui.Button("停止任务", StopDetectionTasks); _stop.IsVisible = false;
        _pause.Click += (_, _) =>
        {
            var jobs = _entries.Select(entry => entry.Task).OfType<Job>().Where(job => job.State is JobState.Waiting or JobState.Running or JobState.Paused).ToArray();
            var resume = jobs.Length > 0 && jobs.All(job => job.State == JobState.Paused);
            foreach (var job in jobs) { if (resume) _resumeTask?.Invoke(job); else if (job.State != JobState.Paused) _pauseTask?.Invoke(job); }
            RefreshDetectionTasks();
        };
        analyzeActions.Children.Add(_analyze); analyzeActions.Children.Add(_pause); analyzeActions.Children.Add(_stop);
        if (newTask is not null) analyzeActions.Children.Add(Ui.Button("新建检测任务", newTask)); Grid.SetRow(analyzeActions, 2); left.Children.Add(analyzeActions);
        var body = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 }; body.Children.Add(left);
        _rangePanel.Children.Add(Ui.Text("排除这些区间", "settingsHeading")); _rangePanel.Children.Add(_ranges);
        _ranges.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PersonClipRange>((range, _) => Ui.Text(range is null ? "" : MediaTime.Format(range.Start) + " – " + MediaTime.Format(range.End)));
        AddField(_rangePanel, "开始时间", _start); AddField(_rangePanel, "结束时间", _end);
        var rangeActions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { Ui.Button("从视频标记 / 调整…", async () => await MarkRangeAsync()), Ui.Button("移除区间", RemoveRange) })
        { button.Margin = new(0, 0, 8, 8); rangeActions.Children.Add(button); }
        _rangePanel.Children.Add(rangeActions);
        var results = new StackPanel { Spacing = 10 };
        results.Children.Add(Ui.Text("保留片段", "settingsHeading")); results.Children.Add(_resultSummary); results.Children.Add(_retained);
        _review = Ui.Button("播放和调整片段…", async () => await ReviewAsync()); results.Children.Add(_review);
        results.Children.Add(new Expander { Header = "排除区间", Content = _rangePanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        results.Children.Add(_activity);
        var rangeScroll = new ScrollViewer { Content = results }; Grid.SetColumn(rangeScroll, 1); body.Children.Add(rangeScroll);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var output = new Grid { ColumnDefinitions = new("Auto,140,Auto,*,Auto"), ColumnSpacing = 8 };
        output.Children.Add(Ui.Text("输出格式")); Grid.SetColumn(_format, 1); output.Children.Add(_format);
        var folderLabel = Ui.Text("保存位置"); Grid.SetColumn(folderLabel, 2); output.Children.Add(folderLabel); Grid.SetColumn(_folder, 3); output.Children.Add(_folder);
        var browse = Ui.Button("浏览…", async () => { if (await Ui.Folder(this, "保存位置") is { } folder) { _folder.Text = folder; _sourceFolder.IsChecked = false; } });
        Grid.SetColumn(browse, 4); output.Children.Add(browse); Grid.SetRow(output, 2); layout.Children.Add(output);
        var footer = new StackPanel { Spacing = 6 }; footer.Children.Add(_sourceFolder); footer.Children.Add(_status); Grid.SetRow(footer, 3); layout.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null))); actions.Children.Add(_submit); Grid.SetRow(actions, 4); layout.Children.Add(actions); Content = layout;
        _files.SelectionChanged += (_, _) => { RefreshRanges(); RefreshResults(); };
        ToolExecution.Configure(this, _submit, "导出保留片段", editing);
        _ranges.SelectionChanged += (_, _) => { if (_ranges.SelectedItem is PersonClipRange range) { _start.Text = MediaTime.Format(range.Start); _end.Text = MediaTime.Format(range.End); } };
        if (paths is not null) AddPaths(paths);
        if (initial is not null && _entries.Count == 1) { _entries[0].Excluded = PersonClipExclusions.Normalize(defaults.ExcludedRanges); RefreshRanges(); }
        Opened += async (_, _) => { try { await RefreshModelsAsync(); } catch (Exception error) { if (!_closed) _status.Text = error.Message; } };
        Closed += (_, _) => { DetachDetectionTasks(); _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal step) => new()
        { Minimum = min, Maximum = max, Value = value, Increment = step, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private static void AddField(StackPanel panel, string label, Control input)
    { input=Ui.Parameter(input,label);
        var row = new Grid { ColumnDefinitions = new("145,*"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(input, 1); row.Children.Add(input); panel.Children.Add(row);
    }
    private Entry? Selected => _files.SelectedIndex >= 0 && _files.SelectedIndex < _entries.Count ? _entries[_files.SelectedIndex] : null;
    private string[] SelectedDetectors => _detectorBoxes.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToArray();
    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists).Where(VideoFormats.IsVideo))
        {
            var full = Path.GetFullPath(path);
            if (!_entries.Any(entry => string.Equals(entry.Path, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) _entries.Add(new(full));
        }
        RefreshFiles();
    }
    private void RefreshFiles()
    {
        var index = _files.SelectedIndex; _files.ItemsSource = _entries.Select(entry => Path.GetFileName(entry.Path) + " · " + Localization.Text(entry.Status)).ToArray();
        _files.SelectedIndex = _entries.Count == 0 ? -1 : Math.Clamp(index, 0, _entries.Count - 1); RefreshRanges(); RefreshResults(); UpdateDetectorSelection();
    }
    private void RefreshRanges()
    {
        _ranges.ItemsSource = Selected?.Excluded ?? []; _rangePanel.IsEnabled = Selected is not null && !SelectedTaskActive;
        _start.Text = _end.Text = "00:00:00.000";
    }
    private void RemoveRange()
    {
        if (SelectedTaskActive || Selected is not { } entry || _ranges.SelectedIndex < 0) return;
        entry.Excluded = entry.Excluded.Where((_, index) => index != _ranges.SelectedIndex).ToArray(); entry.Result = null; entry.Status = "待分析"; RefreshFiles();
    }
    private async Task MarkRangeAsync()
    {
        if (SelectedTaskActive || Selected is not { } entry) return;
        var previous = _ranges.SelectedItem as PersonClipRange;
        var editor = new EditorWindow(_engine, entry.Path, new() { Start = previous?.Start ?? 0, End = previous?.End ?? 0 }, "person-exclusion");
        var result = await editor.ShowDialog<ConversionOptions?>(this);
        if (_closed || result is null || !_entries.Contains(entry)) return;
        var ranges = entry.Excluded.Where(range => range != previous).Append(new PersonClipRange(result.Start, result.End));
        entry.Excluded = PersonClipExclusions.Normalize(ranges); entry.Result = null; entry.Status = "待分析"; RefreshFiles();
    }
    private async Task AddFilesAsync()
    {
        if (_busy) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择视频"), AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Localization.Text("视频")) { Patterns = QuickClipBatch.VideoExtensions.Select(extension => "*." + extension).ToArray() }] });
        if (!_closed) AddPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }
    private async Task RefreshModelsAsync()
    {
        var store = new ModelStore(); _installedDetectors.Clear();
        foreach (var detector in PersonDetectorCatalog.All)
        {
            var installed = await store.IsInstalledAsync(detector.Id, ct: _lifetime.Token);
            if (_closed) return;
            if (installed) _installedDetectors.Add(detector.Id);
            _detectorDownloads[detector.Id].IsVisible = !installed;
            _detectorStates[detector.Id].Text = Localization.Text(installed ? "已下载" : "未下载") + $" · {ModelCatalog.Find(detector.Id).DownloadSize / 1048576d:0.00} MiB";
        }
        var embedding = await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: _lifetime.Token);
        if (_closed) return;
        _embedding.IsEnabled = true; UpdateDetectorSelection();
    }
    private async Task OpenModelsAsync(string? modelId)
    {
        try { await _manageModels(this, modelId); if (!_closed) { if (!_settings.EnableBetaFeatures) Close(null); else await RefreshModelsAsync(); } }
        catch (Exception error) { if (!_closed) _status.Text = error.Message; }
    }
    private void UpdateDetectorSelection()
    {
        if (_closed) return;
        _modelStatus.IsVisible = false; _detectionMode.IsEnabled=!_busy;
        if (_analyze is not null) { _analyze.IsEnabled = !_busy && _entries.Any(entry => entry.Task is null || !DetectionTaskActive(entry.Task)) && SelectedDetectors.Length > 0; _analyze.Content = Localization.Text(_entries.All(entry => entry.Result is not null) && _entries.Count > 0 ? "后台重新检测" : "后台检测"); }
        _submit.IsEnabled = !_busy && _entries.Any(entry => entry.Result?.Segments.Count > 0 && (entry.Task is null || !DetectionTaskActive(entry.Task)));
    }
    private static double Value(NumericUpDown input)
    {
        if (!decimal.TryParse(input.Text, System.Globalization.NumberStyles.Number, input.NumberFormat, out var value)
            || value < input.Minimum || value > input.Maximum) throw new ArgumentException("请输入范围内的检测参数。");
        return (double)value;
    }
    private void Submit()
    {
        try
        {
            if (!_settings.EnableBetaFeatures) return;
            var edits = _entries.Where(entry => entry.Result?.Segments.Count > 0 && (entry.Task is null || !DetectionTaskActive(entry.Task))).ToArray();
            if (edits.Length == 0) throw new ArgumentException("请先分析并保留片段。");
            foreach (var entry in edits) CheckSource(entry);
            var preset = _format.SelectedItem as string ?? QuickClipBatch.DefaultPreset;
            QuickClipWorkflow.ValidateJoinedExports(edits.Select(entry => entry.Result!), preset);
            var folder = _sourceFolder.IsChecked == true ? Path.GetDirectoryName(edits[0].Path)! : _folder.Text?.Trim() ?? "";
            if (folder.Length == 0) throw new ArgumentException("请选择输出目录。");
            ToolExecution.Complete(this, new PersonClipRequest(edits.Select(entry => entry.Result!).ToArray(), Path.GetFullPath(folder),
                _sourceFolder.IsChecked == true, preset, ToolExecution.StartImmediately(this)));
        }
        catch (Exception error) { _status.Text = error.Message; }
    }
}
