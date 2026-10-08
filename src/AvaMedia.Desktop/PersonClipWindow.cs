using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class PersonClipWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly Func<Window, string?, Task> _manageModels;
    private readonly List<string> _paths = [];
    private readonly ListBox _files = new();
    private readonly TextBox _results = new()
    {
        Name = "PersonClipResults", IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Watermark = "分析时显示检测明细，完成后显示保留片段。"
    };
    private readonly NumericUpDown _fps = Number(.25m, 16, 4, .25m);
    private readonly NumericUpDown _threshold = Number(.1m, .9m, .35m, .05m);
    private readonly NumericUpDown _padding = Number(0, 30, .5m, .1m);
    private readonly NumericUpDown _gap = Number(0, 30, 1, .5m);
    private readonly NumericUpDown _minimum = Number(0, 30, .5m, .1m);
    private readonly CheckBox _uncertain = new() { Content = "保留不确定片段", IsChecked = false };
    private readonly CheckBox _embedding = new() { Content = "使用 EmbeddingGemma 2 语义辅助", IsEnabled = false };
    private readonly CheckBox _gpu = new() { Content = "自动适配 GPU", IsChecked = true };
    private readonly CheckBox _reuseFrames = new() { Content = "复用相似画面", IsChecked = true };
    private readonly TextBlock _modelStatus = Ui.Text("读取模型状态…", "caption");
    private readonly TextBlock _status = Ui.Text("");
    private readonly Controls.AiActivityView _activity = new();
    private readonly StackPanel _parameters = new() { Spacing = 8 };
    private readonly Dictionary<string, CheckBox> _detectorBoxes = [];
    private readonly Dictionary<string, TextBlock> _detectorStates = [];
    private readonly Dictionary<string, Button> _detectorDownloads = [];
    private readonly Button _embeddingDownload;
    private readonly HashSet<string> _installedDetectors = [];
    private readonly ComboBox _detectionMode = new()
    {
        Name = "PersonDetectionMode", ItemsSource = new[] { "平衡检测", "减少漏检", "交叉确认" }, SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _add;
    private readonly Button _remove;
    private readonly Button _models;
    private readonly Button _analyze;
    private readonly Button _stop;
    private readonly Button _export;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _analysis;
    private IReadOnlyList<ClipEditResult>? _edits;
    private bool _closed;

    public PersonClipWindow(IMediaEngine engine, AppSettings settings, IEnumerable<string>? paths, Func<Window, string?, Task> manageModels)
    {
        _engine = engine; _settings = settings; _manageModels = manageModels;
        _gpu.IsChecked = settings.AutoDetectGpu;
        ToolTip.SetTip(_fps, Localization.Text("降低采样频率会减少计算，短暂出现的人物可能漏检。"));
        Title = "保留有人片段 · Beta"; Width = 880; Height = 740; MinWidth = 760; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _add = Ui.Button("添加视频…", async () => await AddFilesAsync());
        _remove = Ui.Button("移除", () => { if (_files.SelectedIndex >= 0) { _paths.RemoveAt(_files.SelectedIndex); RefreshFiles(); InvalidateResult(); } });
        _models = Ui.Button("模型管理…", async () => await OpenModelsAsync(null));
        _analyze = Ui.DialogButton("分析视频", async () => await AnalyzeAsync()); _analyze.Name = "AnalyzePersonClips";
        _stop = Ui.Button("停止分析", () => _analysis?.Cancel()); _stop.Name = "StopPersonClips"; _stop.IsVisible = false;
        _export = Ui.DialogButton("编辑并导出", () => { if (_settings.EnableBetaFeatures && _edits is not null) Close(_edits); });
        _export.Name = "ExportPersonClips"; _export.IsEnabled = false;
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(16), RowSpacing = 12 };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(_add); tools.Children.Add(_remove); tools.Children.Add(_models); layout.Children.Add(tools);
        _parameters.Children.Add(Ui.Text("检测模型", "settingsHeading"));
        foreach (var detector in PersonDetectorCatalog.All)
        {
            var model = ModelCatalog.Find(detector.Id);
            var checkbox = new CheckBox { Name = "Detector_" + detector.Id, Content = detector.Name,
                IsChecked = detector.Id != ModelCatalog.PersonId };
            var state = Ui.Text("读取模型状态…", "caption");
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(checkbox); Grid.SetColumn(state, 1); row.Children.Add(state);
            var download = Ui.Button("下载模型…", async () => await OpenModelsAsync(detector.Id));
            download.Name = "Download_" + detector.Id; download.IsVisible = false; download.Classes.Add("field-action");
            Grid.SetColumn(download, 2); row.Children.Add(download); _detectorDownloads.Add(detector.Id, download);
            _parameters.Children.Add(row); _detectorBoxes.Add(detector.Id, checkbox); _detectorStates.Add(detector.Id, state);
            checkbox.IsCheckedChanged += (_, _) => { InvalidateResult(); UpdateDetectorSelection(); };
            ToolTip.SetTip(checkbox, $"{model.DownloadSize / 1048576d:0.00} MiB");
        }
        _parameters.Children.Add(Ui.Text("联合策略")); _parameters.Children.Add(_detectionMode);
        _detectionMode.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => Ui.Text(value ?? ""));
        ToolTip.SetTip(_detectionMode, Localization.Text("平衡检测保留强证据或多模型一致的画面；减少漏检接受任一模型；交叉确认要求多数模型一致。"));
        _detectionMode.SelectionChanged += (_, _) => InvalidateResult();
        foreach (var (label, input) in new[] { ("每秒采样帧数", _fps), ("检测阈值", _threshold), ("前后保留秒数", _padding), ("合并间隔秒数", _gap), ("最短片段秒数", _minimum) })
        {
            var row = new Grid { ColumnDefinitions = new("160,*"), ColumnSpacing = 12 };
            row.Children.Add(Ui.Text(label)); Grid.SetColumn(input, 1); row.Children.Add(input); _parameters.Children.Add(row);
            input.PropertyChanged += (_, change) => { if (change.Property == NumericUpDown.ValueProperty || change.Property == NumericUpDown.TextProperty) InvalidateResult(); };
        }
        _embeddingDownload = Ui.Button("下载模型…", async () => await OpenModelsAsync(ModelCatalog.EmbeddingId));
        _embeddingDownload.IsVisible = false; _embeddingDownload.Classes.Add("field-action");
        var embeddingRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        embeddingRow.Children.Add(_embedding); Grid.SetColumn(_embeddingDownload, 1); embeddingRow.Children.Add(_embeddingDownload);
        _parameters.Children.Add(_uncertain); _parameters.Children.Add(embeddingRow); _parameters.Children.Add(_gpu); _parameters.Children.Add(_reuseFrames); _parameters.Children.Add(_modelStatus);
        _uncertain.IsCheckedChanged += (_, _) => InvalidateResult(); _embedding.IsCheckedChanged += (_, _) => InvalidateResult();
        _gpu.IsCheckedChanged += (_, _) => InvalidateResult(); _reuseFrames.IsCheckedChanged += (_, _) => InvalidateResult();
        var body = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
        var inputs = new Grid { RowDefinitions = new("140,*"), RowSpacing = 12 };
        inputs.Children.Add(_files);
        var parameters = new ScrollViewer { Content = _parameters };
        Grid.SetRow(parameters, 1); inputs.Children.Add(parameters); body.Children.Add(inputs);
        var observations = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8 };
        observations.Children.Add(_results); Grid.SetRow(_activity, 1); observations.Children.Add(_activity);
        Grid.SetColumn(observations, 1); body.Children.Add(observations); Grid.SetRow(body, 1); layout.Children.Add(body);
        Grid.SetRow(_status, 2); layout.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_stop); actions.Children.Add(_analyze); actions.Children.Add(Ui.DialogButton("取消", () => Close(null))); actions.Children.Add(_export);
        Grid.SetRow(actions, 3); layout.Children.Add(actions); Content = layout;
        if (paths is not null) AddPaths(paths);
        Opened += async (_, _) => { try { await RefreshModelsAsync(); } catch (Exception error) { if (!_closed) _status.Text = error.Message; } };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _lifetime.Dispose(); };
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal step) => new()
        { Minimum = min, Maximum = max, Value = value, Increment = step, Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private void InvalidateResult() { if (_analysis is not null) return; _edits = null; _export.IsEnabled = false; _results.Text = ""; _status.Text = ""; _activity.Update(null); }
    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (!QuickClipBatch.VideoExtensions.Contains(Path.GetExtension(path).TrimStart('.'))) continue;
            var full = Path.GetFullPath(path);
            if (!_paths.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)) _paths.Add(full);
        }
        RefreshFiles(); InvalidateResult();
    }
    private void RefreshFiles() => _files.ItemsSource = _paths.Select(Path.GetFileName).ToArray();
    private async Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择视频"), AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Localization.Text("视频")) { Patterns = QuickClipBatch.VideoExtensions.Select(extension => "*." + extension).ToArray() }] });
        AddPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }
    private async Task RefreshModelsAsync()
    {
        var store = new ModelStore();
        _installedDetectors.Clear();
        foreach (var detector in PersonDetectorCatalog.All)
        {
            var installed = await store.IsInstalledAsync(detector.Id, ct: _lifetime.Token);
            if (_closed) return;
            if (installed) _installedDetectors.Add(detector.Id);
            _detectorDownloads[detector.Id].IsVisible = !installed;
            _detectorStates[detector.Id].Text = Localization.Text(installed ? "已下载" : "未下载")
                + $" · {ModelCatalog.Find(detector.Id).DownloadSize / 1048576d:0.00} MiB";
        }
        var embedding = await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: _lifetime.Token);
        if (_closed) return;
        _embedding.IsEnabled = embedding;
        _embeddingDownload.IsVisible = !embedding;
        if (!embedding) _embedding.IsChecked = false;
        UpdateDetectorSelection();
    }
    private async Task OpenModelsAsync(string? modelId)
    {
        try
        {
            await _manageModels(this, modelId);
            if (_closed) return;
            if (!_settings.EnableBetaFeatures) Close(null); else await RefreshModelsAsync();
        }
        catch (Exception error) { if (!_closed) _status.Text = error.Message; }
    }
    private string[] SelectedDetectors => _detectorBoxes.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToArray();
    private void UpdateDetectorSelection()
    {
        if (_analysis is not null || _closed) return;
        var selected = SelectedDetectors;
        var missing = selected.Where(id => !_installedDetectors.Contains(id)).ToArray();
        _modelStatus.Text = selected.Length == 0 ? Localization.Text("请选择至少一种检测模型。")
            : missing.Length > 0 ? Localization.Format($"请先下载：{string.Join("、", missing.Select(id => PersonDetectorCatalog.Find(id).Name))}")
            : Localization.Format($"已选择 {selected.Length} 种检测模型");
        _analyze.IsEnabled = selected.Length > 0 && missing.Length == 0;
    }
    private static double Value(NumericUpDown input)
    {
        if (!decimal.TryParse(input.Text, System.Globalization.NumberStyles.Number, input.NumberFormat, out var value)
            || value < input.Minimum || value > input.Maximum) throw new ArgumentException("请输入范围内的检测参数。");
        return (double)value;
    }
    private void ShowProgress(PersonClipProgress progress, int index, IReadOnlyList<string> summaries)
    {
        var lines = summaries.ToList();
        if (lines.Count > 0) lines.Add("");
        lines.Add(Localization.Format($"视频 {index + 1}/{_paths.Count} · {Path.GetFileName(_paths[index])}"));
        lines.Add(Localization.Text(progress.Stage));
        if (progress.Activity is { } activity)
        {
            if (activity.Detail.Length > 0) lines.Add(activity.Detail);
            if (activity.Backend.Length > 0) lines.Add(activity.Backend);
        }
        if (progress.Evidence.Count > 0)
        {
            lines.Add(""); lines.Add(Localization.Text("当前画面检测"));
            foreach (var evidence in progress.Evidence)
                lines.Add(Localization.Format($"{PersonDetectorCatalog.Find(evidence.Id).Name} · 分数 {evidence.Score:0.000} / 阈值 {evidence.Threshold:0.000} · {Localization.Key(evidence.Score >= evidence.Threshold ? "达到阈值" : "低于阈值")} · {evidence.Backend}"));
        }
        if (progress.Activity is { RecentResults.Length: > 0 } observations)
        {
            lines.Add(""); lines.Add(Localization.Format($"阶段结果 · 最近 {observations.RecentResults.Length} 条"));
            lines.AddRange(observations.RecentResults);
        }
        var text = Localization.Join(Environment.NewLine, lines);
        if (_results.Text != text) _results.Text = text;
    }
    private async Task AnalyzeAsync()
    {
        if (_analysis is not null || !_settings.EnableBetaFeatures) return;
        if (_paths.Count == 0) { _status.Text = Localization.Text("请添加视频。"); return; }
        PersonClipOptions options;
        try { options = new(Value(_fps), Value(_threshold), Value(_padding), Value(_gap), Value(_minimum), _uncertain.IsChecked == true,
            _embedding.IsChecked == true, _gpu.IsChecked == true, _reuseFrames.IsChecked == true,
            SelectedDetectors, (PersonDetectionMode)_detectionMode.SelectedIndex); options.Validate(); }
        catch (Exception error) { _status.Text = error.Message; return; }
        InvalidateResult();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _analysis = cancellation;
        _add.IsEnabled = _remove.IsEnabled = _models.IsEnabled = _parameters.IsEnabled = _analyze.IsEnabled = false;
        _stop.IsVisible = true;
        _activity.Update(new("校验模型", string.Join(" + ", options.SelectedDetectors.Select(id => PersonDetectorCatalog.Find(id).Name)), DateTime.UtcNow, DateTime.UtcNow));
        try
        {
            var results = new List<ClipEditResult>();
            var summaries = new List<string>();
            for (var index = 0; index < _paths.Count; index++)
            {
                var current = index; var finished = false;
                ShowProgress(new(0, 0, "校验模型"), current, summaries);
                var progress = new Progress<PersonClipProgress>(value =>
                {
                    if (_closed || _analysis != cancellation || finished) return;
                    if (value.Activity is { } activity) _activity.Update(activity);
                    ShowProgress(value, current, summaries);
                    _status.Text = Path.GetFileName(_paths[current]) + " · " + Localization.Text(value.Stage);
                });
                var result = await new PersonClipAnalysis(_engine).AnalyzeAsync(_paths[index], options, progress, cancellation.Token);
                finished = true;
                if (result.Segments.Count > 0) results.Add(new(result.Path, result.Info, result.Segments));
                summaries.Add(Localization.Format($"{Path.GetFileName(result.Path)} · {result.Segments.Count} 个片段 · 保留 {MediaTime.Format(result.Segments.Sum(segment => segment.End - segment.Start))} · 不确定 {result.UncertainFrames}/{result.SampledFrames} 帧"));
                summaries.Add(Localization.Format($"模型计算 {result.InferredFrames} 帧 · 复用 {result.ReusedFrames} 帧 · 边界细化 {result.BoundaryFrames} 帧") + " · " + result.Backend);
                foreach (var detector in result.Detectors)
                    summaries.Add(Localization.Format($"{detector.Name} · 检测 {detector.Evaluations} 帧 · 有人 {detector.PositiveFrames} 帧")
                        + (detector.FallbackReason is null ? "" : " · " + Localization.Text("已回退 CPU")));
                for (var segmentIndex = 0; segmentIndex < result.Segments.Count; segmentIndex++)
                {
                    var segment = result.Segments[segmentIndex];
                    summaries.Add(Localization.Format($"保留片段 {segmentIndex + 1} · {MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)}"));
                }
                summaries.Add("");
                _results.Text = Localization.Join(Environment.NewLine, summaries);
            }
            if (_closed) return;
            _edits = results; _export.IsEnabled = results.Count > 0;
            _status.Text = Localization.Text(results.Count > 0 ? "分析完成" : "没有找到可保留的片段，可调整检测阈值后重试。");
            _activity.Finish(AiActivityState.Completed, "分析完成");
        }
        catch (OperationCanceledException) { if (!_closed) { _activity.Finish(AiActivityState.Cancelled, "已停止"); _status.Text = Localization.Text("分析已停止。"); } }
        catch (Exception error) { if (!_closed) { _activity.Finish(AiActivityState.Failed, "分析失败"); _status.Text = error.Message; } }
        finally
        {
            _analysis = null;
            if (!_closed)
            {
                _add.IsEnabled = _remove.IsEnabled = _models.IsEnabled = _parameters.IsEnabled = _analyze.IsEnabled = true;
                _stop.IsVisible = false;
                UpdateDetectorSelection();
            }
        }
    }
}
