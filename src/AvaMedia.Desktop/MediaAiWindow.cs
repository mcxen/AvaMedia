using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class MediaAiWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<BatchVideoEntry> _entries = [];
    private readonly Dictionary<string, MediaTagResult> _results = new(BatchVideoTools.PathComparer);
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly StackPanel _imports = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _parameters = new() { Spacing = 9 };
    private readonly TextBox _keywords = new() { AcceptsReturn = true, Height = 78, Watermark = "黑长发，眼镜" };
    private readonly TextBox _pattern = Ui.Input("{keyword}_{index}");
    private readonly NumericUpDown _threshold = new() { Minimum = .05m, Maximum = .95m, Value = .4m, Increment = .05m };
    private readonly NumericUpDown _frames = new() { Minimum = 1, Maximum = 32, Value = 8, Increment = 1 };
    private readonly CheckBox _gpu = new() { Content = "自动适配 GPU" };
    private readonly CheckBox _reuse = new() { Content = "复用相似画面", IsChecked = true };
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹", IsChecked = true };
    private readonly TextBlock _status = Ui.Text("就绪", "caption");
    private readonly TextBlock _modelStatus = Ui.Text("读取模型状态…", "caption");
    private readonly Button _analyze;
    private readonly Button _rename;
    private readonly Button _undo;
    private readonly Button _stop;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "ai-rename.json");
    private CancellationTokenSource? _operation;
    private RenameItem[]? _plan;
    private bool _closed, _renaming, _busy;
    public event Action<IReadOnlyList<RenameItem>>? Renamed;

    public MediaAiWindow(IMediaEngine engine, AppSettings settings, IEnumerable<string>? initial, Func<Window, Task> manageModels)
    {
        _engine = engine; _settings = settings; _gpu.IsChecked = false;
        Title = "媒体 AI 标签 · Beta"; Width = 1120; Height = 740; MinWidth = 920; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Controls.WindowArtwork.SetKind(this, "image");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(20), RowSpacing = 12 };
        _imports.Children.Add(Ui.Button("添加图片 / 视频…", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择图片或视频"), AllowMultiple = true });
            AddPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }));
        _imports.Children.Add(Ui.Button("添加文件夹…", async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true });
            await AddFoldersAsync(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        }));
        _imports.Children.Add(_recursive);
        _imports.Children.Add(Ui.Button("移除选中", () =>
        {
            foreach (var entry in _list.SelectedItems?.Cast<BatchVideoEntry>().ToArray() ?? []) { _results.Remove(entry.Path); _entries.Remove(entry); }
            InvalidatePlan();
        }));
        root.Children.Add(_imports);
        _list.ItemsSource = _entries;
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.ItemTemplate = new FuncDataTemplate<BatchVideoEntry>((entry, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("28,*,200"), Margin = new(0, 7), ColumnSpacing = 8 };
            var check = new CheckBox(); check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(BatchVideoEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            var content = new StackPanel { Spacing = 4 };
            foreach (var property in new[] { nameof(BatchVideoEntry.Name), nameof(BatchVideoEntry.Details), nameof(BatchVideoEntry.NewName) })
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
                Localization.SetIsUserText(text, true); text.Bind(TextBlock.TextProperty, new Binding(property)); content.Children.Add(text);
            }
            Grid.SetColumn(content, 1); row.Children.Add(content);
            var edits = new StackPanel { Spacing = 5 };
            var status = Ui.Text("", "caption"); status.Bind(TextBlock.TextProperty, new Binding(nameof(BatchVideoEntry.Status))); edits.Children.Add(status);
            var label = Ui.Input(); label.Watermark = Localization.Text("命名标签"); Localization.SetIsUserText(label, true);
            label.Bind(TextBox.TextProperty, new Binding(nameof(BatchVideoEntry.Keyword)) { Mode = BindingMode.TwoWay }); edits.Children.Add(label);
            Grid.SetColumn(edits, 2); row.Children.Add(edits); return row;
        });
        var body = new Grid { ColumnDefinitions = new("*,310"), ColumnSpacing = 16 }; body.Children.Add(_list);
        _parameters.Children.Add(Ui.Text("JoyTag · 本地推理", "caption"));
        _parameters.Children.Add(_modelStatus);
        _parameters.Children.Add(Ui.Button("模型管理…", async () =>
        {
            await manageModels(this);
            if (_closed) return;
            if (!_settings.EnableBetaFeatures) { Close(); return; }
            await RefreshModelAsync();
        }));
        AddRow("标签阈值", _threshold); AddRow("视频采样帧数", _frames);
        _parameters.Children.Add(_gpu); _parameters.Children.Add(_reuse);
        _analyze = Ui.Button("分析标签", async () => await AnalyzeAsync()); _analyze.IsEnabled = false; _parameters.Children.Add(_analyze);
        _parameters.Children.Add(Ui.Text("关键词", "caption")); Localization.SetIsUserText(_keywords, true); _parameters.Children.Add(_keywords);
        _parameters.Children.Add(Ui.Text("逗号或换行分隔；组合用 +，如 黑长发=black_hair+long_hair。", "caption"));
        _parameters.Children.Add(Ui.Button("筛选匹配", async () => { try { Match(); } catch (Exception error) { await Ui.Message(this, "标签筛选失败", error.Message); } }));
        _parameters.Children.Add(Ui.Text("标签分数可能误判；视频按采样平均分筛选。", "caption"));
        AddRow("命名模板", _pattern); Localization.SetIsUserText(_pattern, true);
        _parameters.Children.Add(Ui.Button("预览新名称", async () => await PreviewAsync()));
        _rename = Ui.Button("执行重命名", async () => await RenameAsync(false)); _rename.IsEnabled = false; _parameters.Children.Add(_rename);
        _undo = Ui.Button("撤销上次重命名", async () => await RenameAsync(true)); _undo.IsVisible = CanUndo(); _parameters.Children.Add(_undo);
        _parameters.Children.Add(Ui.Button("导出标签 JSON…", async () => await ExportAsync()));
        var scroll = new ScrollViewer { Content = _parameters }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_status);
        _stop = Ui.Button("停止", () => _operation?.Cancel()); _stop.IsVisible = false; Grid.SetColumn(_stop, 1); footer.Children.Add(_stop);
        var close = Ui.DialogButton("关闭", Close); Grid.SetColumn(close, 2); footer.Children.Add(close); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _keywords.TextChanged += (_, _) => InvalidatePlan(); _pattern.TextChanged += (_, _) => InvalidatePlan();
        _threshold.PropertyChanged += (_, change) => { if (change.Property == NumericUpDown.ValueProperty || change.Property == NumericUpDown.TextProperty) InvalidatePlan(); };
        Opened += async (_, _) => await RefreshModelAsync();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _busy ? DragDropEffects.None : DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, async (_, e) => { if (!_busy) await AddFoldersAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, e) => { if (_renaming) { e.Cancel = true; return; } _closed = true; _operation?.Cancel(); _lifetime.Cancel(); };
        AddPaths(initial ?? []);
    }
    private void AddRow(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new("110,*"), ColumnSpacing = 8 }; row.Children.Add(Ui.Text(label));
        Grid.SetColumn(control, 1); row.Children.Add(control); _parameters.Children.Add(row);
    }
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_closed) return;
        foreach (var path in paths.Where(File.Exists).Where(MediaTagService.Supports).Select(Path.GetFullPath).Distinct(BatchVideoTools.PathComparer))
        {
            if (_entries.Any(entry => BatchVideoTools.PathComparer.Equals(entry.Path, path))) continue;
            var entry = new BatchVideoEntry(path) { Details = "", Status = "待分析" };
            entry.PropertyChanged += (_, change) => { if (change.PropertyName is nameof(BatchVideoEntry.Include) or nameof(BatchVideoEntry.Keyword)) InvalidatePlan(); };
            _entries.Add(entry);
        }
        InvalidatePlan();
    }
    private async Task AddFoldersAsync(IEnumerable<string> paths)
    {
        if (_busy || _closed) return;
        var recursive = _recursive.IsChecked == true;
        SetBusy(true);
        try
        {
            var files = await Task.Run(() => paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true })
                : [path]).Where(MediaTagService.Supports).ToArray(), _lifetime.Token);
            AddPaths(files);
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
            _modelStatus.Text = Localization.Text(installed ? "JoyTag 已下载" : "请先下载 JoyTag · 约 366 MB"); _analyze.IsEnabled = installed;
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
    private async Task AnalyzeAsync()
    {
        if (_busy || !_settings.EnableBetaFeatures) return;
        var paths = _entries.Where(entry => entry.Include).Select(entry => entry.Path).ToArray();
        if (paths.Length == 0) { await Ui.Message(this, "AI 标签", "请添加并勾选图片或视频。"); return; }
        MediaTagOptions options;
        try
        {
            var frames = Number(_frames); if (frames != Math.Truncate(frames)) throw new ArgumentException("采样帧数须为整数。");
            options = new((int)frames, _gpu.IsChecked == true, _reuse.IsChecked == true); options.Validate(); Number(_threshold);
        }
        catch (Exception error) { await Ui.Message(this, "参数错误", error.Message); return; }
        foreach (var entry in _entries.Where(entry => paths.Contains(entry.Path, BatchVideoTools.PathComparer)))
        { _results.Remove(entry.Path); entry.Status = "待分析"; entry.Details = ""; entry.Keyword = ""; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; SetBusy(true); InvalidatePlan();
        _status.Text = Localization.Text(options.PreferGpu ? "加载 JoyTag · 首次 GPU 编译可能较慢…" : "加载 JoyTag…");
        var progress = new Progress<MediaTagProgress>(update =>
        {
            if (_closed) return;
            var entry = _entries.FirstOrDefault(entry => BatchVideoTools.PathComparer.Equals(entry.Path, update.Path)); if (entry is null) return;
            if (update.Result is { } result) { _results[result.Path] = result; ShowResult(entry, result); }
            else { entry.Status = Localization.Text("失败"); entry.Details = update.Error ?? ""; entry.Include = false; }
            _status.Text = $"{update.Completed} / {update.Total}";
        });
        try
        {
            var results = await new MediaTagService(_engine).AnalyzeAsync(paths, options, progress, operation.Token);
            if (_closed) return;
            foreach (var result in results) _results[result.Path] = result;
            _status.Text = Localization.Format($"完成 {results.Count} / {paths.Length} 个文件");
            try { Match(); }
            catch (Exception error) { await Ui.Message(this, "标签筛选失败", error.Message); }
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("已停止，已完成结果已保留"); }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "分析失败", error.Message); }
        finally { _operation = null; if (!_closed) { SetBusy(false); InvalidatePlan(); } }
    }
    private void ShowResult(BatchVideoEntry entry, MediaTagResult result)
    {
        entry.Status = $"{result.Backend} · {result.InferredFrames}/{result.SampledFrames}";
        var threshold = Number(_threshold);
        entry.Details = string.Join(" · ", result.Scores.Where(score => score.Score >= threshold).OrderByDescending(score => score.Score)
            .Take(20).Select(score => $"{score.Tag} {score.Score:0.00}"));
    }
    private void Match()
    {
        var vocabulary = _results.Values.FirstOrDefault()?.Scores.Select(score => score.Tag) ?? [];
        var queries = MediaTagService.ParseQueries(_keywords.Text ?? "", vocabulary); var threshold = Number(_threshold);
        foreach (var entry in _entries)
        {
            if (!_results.TryGetValue(entry.Path, out var result)) { entry.Include = false; continue; }
            ShowResult(entry, result); entry.Keyword = MediaTagService.MatchLabel(result, queries, threshold); entry.Include = entry.Keyword.Length > 0;
        }
        InvalidatePlan();
    }
    private void InvalidatePlan()
    {
        _plan = null; _rename.IsEnabled = false; foreach (var entry in _entries) entry.NewName = "";
    }
    private async Task PreviewAsync()
    {
        if (_busy) return;
        try
        {
            var selected = _entries.Where(entry => entry.Include).ToArray();
            if (selected.Length == 0) throw new ArgumentException("请勾选要重命名的文件。");
            foreach (var entry in selected) if (_results.TryGetValue(entry.Path, out var result)) MediaTagService.ValidateSource(result);
            _plan = BatchVideoTools.PreviewRename(selected.Select(entry => entry.Path), new(_pattern.Text ?? ""),
                selected.ToDictionary(entry => entry.Path, entry => entry.Keyword.Trim(), BatchVideoTools.PathComparer));
            foreach (var item in _plan) _entries.First(entry => BatchVideoTools.PathComparer.Equals(entry.Path, item.Source)).NewName = Path.GetFileName(item.Target);
            _rename.IsEnabled = _plan.Any(item => item.Source != item.Target);
        }
        catch (Exception error) { InvalidatePlan(); await Ui.Message(this, "重命名预览失败", error.Message); }
    }
    private async Task RenameAsync(bool undo)
    {
        if (_busy || !undo && _plan is null) return;
        var plan = _plan; _renaming = true; SetBusy(true);
        try
        {
            var mappings = await Task.Run(() => undo ? BatchVideoTools.UndoRename(_journal) : BatchVideoTools.ApplyRename(plan!, _journal));
            foreach (var mapping in mappings)
            {
                _results.Remove(mapping.Source);
                var entry = _entries.FirstOrDefault(entry => BatchVideoTools.PathComparer.Equals(entry.Path, mapping.Source));
                if (entry is not null) { entry.Renamed(mapping.Target); entry.Status = Localization.Text("已重命名"); entry.Keyword = ""; }
            }
            Renamed?.Invoke(mappings); _status.Text = Localization.Format($"已更新 {mappings.Length} 个文件名"); _undo.IsVisible = CanUndo();
        }
        catch (Exception error) { await Ui.Message(this, "重命名失败", error.Message); }
        finally { _renaming = false; SetBusy(false); InvalidatePlan(); }
    }
    private async Task ExportAsync()
    {
        if (_results.Count == 0) return;
        try
        {
            var threshold = Number(_threshold);
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("导出标签"), SuggestedFileName = "ai-tags.json", DefaultExtension = "json" });
            if (file is null) return;
            var report = new { Model = ModelCatalog.JoyTagId, Threshold = threshold, Results = _results.Values.Select(result => new
            { result.Path, result.Backend, result.SampledFrames, result.InferredFrames, Tags = result.Scores.Where(score => score.Score >= threshold).OrderByDescending(score => score.Score) }) };
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error) { await Ui.Message(this, "导出失败", error.Message); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; _imports.IsEnabled = !busy; _parameters.IsEnabled = !busy; _list.IsEnabled = !busy;
        _stop.IsVisible = busy && _operation is not null;
    }
    private bool CanUndo()
    {
        try { return JsonSerializer.Deserialize<BatchVideoTools.RenameJournal>(File.ReadAllText(_journal))?.State == "completed"; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
}
