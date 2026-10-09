using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed record PersonClipRequest(IReadOnlyList<QuickClipInput> Inputs, string OutputFolder, bool OutputToSource);

public sealed class PersonClipWindow : Window
{
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public PersonClipRange[] Excluded { get; set; } = [];
    }
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly Func<Window, string?, Task> _manageModels;
    private readonly List<Entry> _entries = [];
    private readonly ListBox _files = new() { Name = "PersonClipFiles" };
    private readonly ListBox _ranges = new() { Name = "PersonClipExcludedRanges", Height = 200 };
    private readonly TextBox _start = new() { Name = "PersonClipExcludeStart", Text = "00:00:00.000" };
    private readonly TextBox _end = new() { Name = "PersonClipExcludeEnd", Text = "00:00:00.000" };
    private readonly NumericUpDown _fps = Number(.25m, 16, 2, .25m);
    private readonly NumericUpDown _threshold = Number(.1m, .9m, .35m, .05m);
    private readonly NumericUpDown _padding = Number(0, 30, .5m, .1m);
    private readonly NumericUpDown _gap = Number(0, 30, 1, .5m);
    private readonly NumericUpDown _minimum = Number(0, 30, .5m, .1m);
    private readonly NumericUpDown _darkThreshold = Number(1, 32, 8, 1);
    private readonly CheckBox _uncertain = new() { Content = "保留不确定片段" };
    private readonly CheckBox _embedding = new() { Content = "使用 EmbeddingGemma 2 语义辅助", IsEnabled = false };
    private readonly CheckBox _gpu = new() { Content = "自动适配 GPU", IsChecked = true };
    private readonly CheckBox _reuseFrames = new() { Content = "复用相似画面", IsChecked = true };
    private readonly CheckBox _dark = new() { Content = "快速排除黑灯画面", IsChecked = true };
    private readonly CheckBox _blank = new() { Content = "快速排除无画面", IsChecked = true };
    private readonly CheckBox _sourceFolder = new() { Content = "输出至源文件目录" };
    private readonly TextBox _folder = new() { Name = "PersonClipOutputFolder" };
    private readonly TextBlock _modelStatus = Ui.Text("", "caption");
    private readonly TextBlock _status = Ui.Text("");
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
        bool editing = false, bool? outputToSource = null)
    {
        _engine = engine; _settings = settings; _manageModels = manageModels;
        Title = "保留有人片段 · Beta"; Width = 920; Height = 740; MinWidth = 760; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var defaults = initial?.Detection ?? new();
        _fps.Value = (decimal)defaults.FramesPerSecond; _threshold.Value = (decimal)defaults.Threshold;
        _padding.Value = (decimal)defaults.PaddingSeconds; _gap.Value = (decimal)defaults.MergeGapSeconds;
        _minimum.Value = (decimal)defaults.MinimumSeconds; _darkThreshold.Value = (decimal)defaults.DarkLumaThreshold;
        _uncertain.IsChecked = defaults.KeepUncertain; _embedding.IsChecked = defaults.UseEmbedding;
        _gpu.IsChecked = initial is null ? settings.AutoDetectGpu : defaults.PreferGpu;
        _reuseFrames.IsChecked = defaults.ReuseSimilarFrames; _dark.IsChecked = defaults.SkipDarkFrames; _blank.IsChecked = defaults.SkipBlankFrames;
        _detectionMode.SelectedIndex = (int)defaults.DetectionMode;
        _folder.Text = outputFolder ?? settings.OutputFolder; _sourceFolder.IsChecked = outputToSource ?? settings.OutputToSource;
        _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _sourceFolder.IsCheckedChanged += (_, _) => _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _format.SelectedItem = initial?.ExportPreset ?? QuickClipBatch.DefaultPreset;
        _format.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => Ui.Text(QuickClipBatch.PresetLabel(value ?? "")));
        ToolTip.SetTip(_format, Localization.Text("原格式导出的区间边界受关键帧限制；精确排除区间请选择 MP4、MKV 或 TS。"));
        ToolTip.SetTip(_fps, Localization.Text("降低采样频率会减少计算，短暂出现的人物可能漏检。"));
        _submit = Ui.DialogButton(editing ? "保存修改" : "加入任务列表", Submit); _submit.Name = "QueuePersonClips"; _submit.IsEnabled = false;
        _submit.IsDefault = true; _submit.Classes.Add("primary");
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto,Auto,Auto"), Margin = new(16), RowSpacing = 12 };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(Ui.Button("添加视频…", async () => await AddFilesAsync()));
        tools.Children.Add(Ui.Button("移除", () => { if (_files.SelectedIndex >= 0) { _entries.RemoveAt(_files.SelectedIndex); RefreshFiles(); } }));
        tools.Children.Add(Ui.Button("模型管理…", async () => await OpenModelsAsync(null))); layout.Children.Add(tools);
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
        AddField(parameters, "联合策略", _detectionMode);
        _detectionMode.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => Ui.Text(value ?? ""));
        foreach (var (label, input) in new[] { ("每秒采样帧数", _fps), ("检测阈值", _threshold), ("前后保留秒数", _padding),
            ("合并间隔秒数", _gap), ("最短片段秒数", _minimum) }) AddField(parameters, label, input);
        parameters.Children.Add(_dark); AddField(parameters, "黑灯亮度阈值", _darkThreshold); parameters.Children.Add(_blank);
        _darkThreshold.IsEnabled = _dark.IsChecked == true;
        _dark.IsCheckedChanged += (_, _) => _darkThreshold.IsEnabled = _dark.IsChecked == true;
        parameters.Children.Add(_uncertain); parameters.Children.Add(_embedding); parameters.Children.Add(_gpu); parameters.Children.Add(_reuseFrames); parameters.Children.Add(_modelStatus);
        var left = new Grid { RowDefinitions = new("130,*"), RowSpacing = 12 }; left.Children.Add(_files);
        var scroll = new ScrollViewer { Content = parameters }; Grid.SetRow(scroll, 1); left.Children.Add(scroll);
        var body = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 }; body.Children.Add(left);
        _rangePanel.Children.Add(Ui.Text("免检测区间", "settingsHeading")); _rangePanel.Children.Add(_ranges);
        _ranges.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PersonClipRange>((range, _) => Ui.Text(range is null ? "" : MediaTime.Format(range.Start) + " – " + MediaTime.Format(range.End)));
        AddField(_rangePanel, "开始时间", _start); AddField(_rangePanel, "结束时间", _end);
        var rangeActions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { Ui.Button("添加区间", () => SaveRange(false)), Ui.Button("保存修改", () => SaveRange(true)),
            Ui.Button("移除区间", RemoveRange), Ui.Button("从视频标记…", async () => await MarkRangeAsync()) })
        { button.Margin = new(0, 0, 8, 8); rangeActions.Children.Add(button); }
        _rangePanel.Children.Add(rangeActions);
        var rangeScroll = new ScrollViewer { Content = _rangePanel }; Grid.SetColumn(rangeScroll, 1); body.Children.Add(rangeScroll);
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var output = new Grid { ColumnDefinitions = new("Auto,140,Auto,*,Auto"), ColumnSpacing = 8 };
        output.Children.Add(Ui.Text("输出格式")); Grid.SetColumn(_format, 1); output.Children.Add(_format);
        var folderLabel = Ui.Text("保存位置"); Grid.SetColumn(folderLabel, 2); output.Children.Add(folderLabel); Grid.SetColumn(_folder, 3); output.Children.Add(_folder);
        var browse = Ui.Button("浏览…", async () => { if (await Ui.Folder(this, "保存位置") is { } folder) { _folder.Text = folder; _sourceFolder.IsChecked = false; } });
        Grid.SetColumn(browse, 4); output.Children.Add(browse); Grid.SetRow(output, 2); layout.Children.Add(output);
        var footer = new StackPanel { Spacing = 6 }; footer.Children.Add(_sourceFolder); footer.Children.Add(_status); Grid.SetRow(footer, 3); layout.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null))); actions.Children.Add(_submit); Grid.SetRow(actions, 4); layout.Children.Add(actions); Content = layout;
        _files.SelectionChanged += (_, _) => RefreshRanges();
        _ranges.SelectionChanged += (_, _) => { if (_ranges.SelectedItem is PersonClipRange range) { _start.Text = MediaTime.Format(range.Start); _end.Text = MediaTime.Format(range.End); } };
        if (paths is not null) AddPaths(paths);
        if (initial is not null && _entries.Count == 1) { _entries[0].Excluded = PersonClipExclusions.Normalize(defaults.ExcludedRanges); RefreshRanges(); }
        Opened += async (_, _) => { try { await RefreshModelsAsync(); } catch (Exception error) { if (!_closed) _status.Text = error.Message; } };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal step) => new()
        { Minimum = min, Maximum = max, Value = value, Increment = step, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private static void AddField(StackPanel panel, string label, Control input)
    {
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
        var index = _files.SelectedIndex; _files.ItemsSource = _entries.Select(entry => Path.GetFileName(entry.Path)).ToArray();
        _files.SelectedIndex = _entries.Count == 0 ? -1 : Math.Clamp(index, 0, _entries.Count - 1); RefreshRanges(); UpdateDetectorSelection();
    }
    private void RefreshRanges()
    {
        _ranges.ItemsSource = Selected?.Excluded ?? []; _rangePanel.IsEnabled = Selected is not null;
        _start.Text = _end.Text = "00:00:00.000";
    }
    private void SaveRange(bool replace)
    {
        if (Selected is not { } entry) return;
        try
        {
            var range = new PersonClipRange(ClipSplit.ParseTime(_start.Text), ClipSplit.ParseTime(_end.Text)); range.Validate();
            var ranges = entry.Excluded.ToList();
            if (replace) { if (_ranges.SelectedIndex < 0) throw new ArgumentException("请选择要修改的区间。"); ranges[_ranges.SelectedIndex] = range; }
            else ranges.Add(range);
            entry.Excluded = PersonClipExclusions.Normalize(ranges); RefreshRanges(); _status.Text = "";
        }
        catch (Exception error) { _status.Text = error.Message; }
    }
    private void RemoveRange()
    {
        if (Selected is not { } entry || _ranges.SelectedIndex < 0) return;
        entry.Excluded = entry.Excluded.Where((_, index) => index != _ranges.SelectedIndex).ToArray(); RefreshRanges();
    }
    private async Task MarkRangeAsync()
    {
        if (Selected is not { } entry) return;
        var previous = _ranges.SelectedItem as PersonClipRange;
        var editor = new EditorWindow(_engine, entry.Path, new() { Start = previous?.Start ?? 0, End = previous?.End ?? 0 }, "person-exclusion");
        var result = await editor.ShowDialog<ConversionOptions?>(this);
        if (_closed || result is null || !_entries.Contains(entry)) return;
        var ranges = entry.Excluded.Where(range => range != previous).Append(new PersonClipRange(result.Start, result.End));
        entry.Excluded = PersonClipExclusions.Normalize(ranges); RefreshRanges();
    }
    private async Task AddFilesAsync()
    {
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
        _embedding.IsEnabled = embedding; if (!embedding) _embedding.IsChecked = false; UpdateDetectorSelection();
    }
    private async Task OpenModelsAsync(string? modelId)
    {
        try { await _manageModels(this, modelId); if (!_closed) { if (!_settings.EnableBetaFeatures) Close(null); else await RefreshModelsAsync(); } }
        catch (Exception error) { if (!_closed) _status.Text = error.Message; }
    }
    private void UpdateDetectorSelection()
    {
        if (_closed) return;
        var selected = SelectedDetectors; var missing = selected.Where(id => !_installedDetectors.Contains(id)).ToArray();
        _modelStatus.Text = selected.Length == 0 ? Localization.Text("请选择至少一种检测模型。")
            : missing.Length > 0 ? Localization.Format($"请先下载：{string.Join("、", missing.Select(id => PersonDetectorCatalog.Find(id).Name))}") : "";
        _modelStatus.IsVisible = _modelStatus.Text.Length > 0;
        _submit.IsEnabled = _settings.EnableBetaFeatures && _entries.Count > 0 && selected.Length > 0 && missing.Length == 0;
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
            var detection = new PersonClipOptions(Value(_fps), Value(_threshold), Value(_padding), Value(_gap), Value(_minimum),
                _uncertain.IsChecked == true, _embedding.IsChecked == true, _gpu.IsChecked == true, _reuseFrames.IsChecked == true,
                SelectedDetectors, (PersonDetectionMode)_detectionMode.SelectedIndex, SkipDarkFrames: _dark.IsChecked == true,
                SkipBlankFrames: _blank.IsChecked == true, DarkLumaThreshold: Value(_darkThreshold)); detection.Validate();
            var preset = _format.SelectedItem as string ?? QuickClipBatch.DefaultPreset;
            if (string.IsNullOrWhiteSpace(_folder.Text)) throw new ArgumentException("请选择输出目录。");
            var inputs = _entries.Select(entry => new QuickClipInput(entry.Path, QuickClipBatch.ResolveOptions(entry.Path, preset,
                new() { PersonClip = new() { Detection = detection with { ExcludedRanges = entry.Excluded.ToArray() }, ExportPreset = preset } }))).ToArray();
            foreach (var input in inputs) MediaEngine.Validate(new() { FeatureId = "person-clip", Inputs = [input.Path], Options = input.Options,
                Output = Path.Combine(Path.GetTempPath(), "AvaMedia-person-validation." + input.Options.Format) });
            Close(new PersonClipRequest(inputs, Path.GetFullPath(_folder.Text), _sourceFolder.IsChecked == true));
        }
        catch (Exception error) { _status.Text = error.Message; }
    }
}
