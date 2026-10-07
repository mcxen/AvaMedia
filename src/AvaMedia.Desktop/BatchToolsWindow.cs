using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class BatchVideoEntry : Observable
{
    private string _path;
    private bool _include = true;
    private string _newName = "";
    private string _status = "待处理";
    public string Path => _path;
    public string Name => System.IO.Path.GetFileName(_path);
    public bool Include { get => _include; set => Set(ref _include, value); }
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string? LastSheet { get; set; }
    public BatchVideoEntry(string path) => _path = path;
    public void Renamed(string path) { _path = path; NewName = ""; Raise(nameof(Path)); Raise(nameof(Name)); }
}

/// <summary>Dedicated batch panel; does not start or change the conversion queue.</summary>
public sealed class BatchToolsWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<BatchVideoEntry> _entries = [];
    private readonly ListBox _list;
    private readonly StackPanel _importBar;
    private readonly StackPanel _renamePanel;
    private readonly StackPanel _sheetPanel;
    private readonly TextBlock _summary;
    private readonly TextBlock _progressText;
    private readonly TextBox _output;
    private readonly Image _preview;
    private readonly Button _generate;
    private readonly Button _rename;
    private readonly Button _undo;
    private readonly Button _stop;
    private readonly CheckBox _recursive;
    private readonly TextBox _pattern = Ui.Input("{name}_{index}");
    private readonly TextBox _prefix = Ui.Input();
    private readonly TextBox _suffix = Ui.Input();
    private readonly TextBox _find = Ui.Input();
    private readonly TextBox _replace = Ui.Input();
    private readonly TextBox _firstIndex = Ui.Input("1");
    private readonly TextBox _digits = Ui.Input("3");
    private readonly TextBox _columns = Ui.Input("3");
    private readonly TextBox _rows = Ui.Input("3");
    private readonly TextBox _cellWidth = Ui.Input("320");
    private readonly TextBox _cellHeight = Ui.Input("180");
    private readonly TextBox _sheets = Ui.Input("1");
    private readonly TextBox _start = Ui.Input("0");
    private readonly TextBox _end = Ui.Input("0");
    private readonly ComboBox _format = Ui.Combo(["jpg", "png"], "jpg");
    private readonly CheckBox _timestamps = new() { Content = "显示每帧时间戳", IsChecked = true };
    private RenameItem[]? _renamePlan;
    private CancellationTokenSource? _operation;
    private bool _renaming;
    private bool _importing;
    private bool _closed;
    private readonly string _journal;
    public event Action<IReadOnlyList<RenameItem>>? Renamed;

    public BatchToolsWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? initial = null,string? journalPath=null)
    {
        _engine = engine;_journal=journalPath??System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "batch-rename.json");
        Title = "批量视频 · 重命名 / 多宫格截图";
        Width = 1260; Height = 860; MinWidth = 1000; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(16) };
        _importBar = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        var addFiles = new Button { Content = "添加视频…" };
        addFiles.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = Localization.Text("选择多个视频"), AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Localization.Text("视频")) { Patterns = VideoFormats.InputExtensions.Select(extension => "*." + extension).ToArray() }, FilePickerFileTypes.All]
            });
            AddPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        };
        _importBar.Children.Add(addFiles);
        var addFolders = new Button { Content = "添加文件夹…" };
        addFolders.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = Localization.Text("选择视频文件夹"), AllowMultiple = true });
            await AddFolders(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
        };
        _importBar.Children.Add(addFolders);
        _recursive = new() { Content = "包含子文件夹", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        _importBar.Children.Add(_recursive);
        _importBar.Children.Add(Ui.Button("移除选中", () =>
        {
            foreach (var row in _list?.SelectedItems?.Cast<BatchVideoEntry>().ToArray() ?? []) _entries.Remove(row);
            InvalidatePlan();
        }));
        _importBar.Children.Add(Ui.Button("全选", () => { foreach (var row in _entries) row.Include = true; }));
        _importBar.Children.Add(Ui.Button("反选", () => { foreach (var row in _entries) row.Include = !row.Include; }));
        root.Children.Add(_importBar);
        _summary = new() { Margin = new(0, 12), Classes = { "caption" }, Text = "尚未添加视频" };
        Grid.SetRow(_summary, 1); root.Children.Add(_summary);

        var body = new Grid { ColumnDefinitions = new("*,380"), ColumnSpacing = 16 };
        var filesArea = new Grid { RowDefinitions = new("26,*") };
        var header = new Grid { ColumnDefinitions = new("34,*,*,160"), Classes = { "table-header" } };
        var h1 = Ui.Text("视频文件"); Grid.SetColumn(h1, 1); header.Children.Add(h1);
        var h2 = Ui.Text("新名称预览"); Grid.SetColumn(h2, 2); header.Children.Add(h2);
        var h3 = Ui.Text("状态"); Grid.SetColumn(h3, 3); header.Children.Add(h3); filesArea.Children.Add(header);
        _list = new() { ItemsSource = _entries, SelectionMode = SelectionMode.Multiple, BorderThickness = new(1) };
        _list.ItemTemplate = new FuncDataTemplate<BatchVideoEntry>((entry, _) =>
        {
            var g = new Grid { ColumnDefinitions = new("28,*,*,160"), Margin = new(0, 6), ColumnSpacing = 5 };
            var check = new CheckBox { MinHeight = 22 };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(BatchVideoEntry.Include)) { Mode = BindingMode.TwoWay }); g.Children.Add(check);
            Add(1, nameof(BatchVideoEntry.Name)); Add(2, nameof(BatchVideoEntry.NewName)); Add(3, nameof(BatchVideoEntry.Status));
            ToolTip.SetTip(g, entry?.Path); return g;
            void Add(int column, string property)
            {
                var t = new TextBlock { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                if (property is nameof(BatchVideoEntry.Name) or nameof(BatchVideoEntry.NewName)) Localization.SetIsUserText(t, true);
                t.Bind(TextBlock.TextProperty, new Binding(property)); Grid.SetColumn(t, column); g.Children.Add(t);
            }
        });
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is BatchVideoEntry entry && entry.LastSheet is { } path) ShowPreview(path); };
        Grid.SetRow(_list, 1); filesArea.Children.Add(_list); body.Children.Add(filesArea);
        var tabs = new TabControl(); Grid.SetColumn(tabs, 1);
        _renamePanel = new() { Spacing = 9, Margin = new(9) };
        AddRow(_renamePanel, "命名模板", _pattern); AddRow(_renamePanel, "前缀", _prefix); AddRow(_renamePanel, "后缀", _suffix);
        AddRow(_renamePanel, "查找文本", _find); AddRow(_renamePanel, "替换为", _replace); AddRow(_renamePanel, "起始序号", _firstIndex); AddRow(_renamePanel, "序号位数", _digits);
        _renamePanel.Children.Add(new TextBlock { Text = "模板可使用 {name} 原文件名、{index} 序号、{parent} 父文件夹名。保留扩展名。\n\n示例：片段_{index} → 片段_001.mp4", TextWrapping = TextWrapping.Wrap, Classes = { "caption" }, Margin = new(0, 8) });
        var previewRename = new Button { Content = "预览新名称", HorizontalAlignment = HorizontalAlignment.Stretch };
        previewRename.Click += async (_, _) => await PreviewRename(); _renamePanel.Children.Add(previewRename);
        _rename = new() { Content = "执行重命名", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        _rename.Click += async (_, _) => await ApplyRename(); _renamePanel.Children.Add(_rename);
        _undo = new() { Content = "撤销上一次重命名", IsEnabled = File.Exists(_journal), HorizontalAlignment = HorizontalAlignment.Stretch };
        _undo.Click += async (_, _) => await UndoRename(); _renamePanel.Children.Add(_undo);
        tabs.Items.Add(new TabItem { Header = "批量重命名", Content = new ScrollViewer { Content = _renamePanel } });

        _sheetPanel = new() { Spacing = 9, Margin = new(9) };
        var preset = Ui.Combo(["2 × 2", "3 × 3", "4 × 4", "5 × 4", "自定义"], "3 × 3");
        preset.SelectionChanged += (_, _) => { if (preset.SelectedItem is string value && value != "自定义") { var p = value.Split('×'); _columns.Text = p[0].Trim(); _rows.Text = p[1].Trim(); } };
        AddRow(_sheetPanel, "宫格预设", preset);
        AddRow(_sheetPanel, "列数 / 行数", Pair(_columns, _rows)); AddRow(_sheetPanel, "长边 / 短边上限", Pair(_cellWidth, _cellHeight)); AddRow(_sheetPanel, "每视频拼图数", _sheets);
        AddRow(_sheetPanel, "开始 / 结束秒", Pair(_start, _end)); AddRow(_sheetPanel, "图片格式", _format); _sheetPanel.Children.Add(_timestamps);
        _sheetPanel.Children.Add(new TextBlock { Text = "结束为 0 表示视频末尾", Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        _output = Ui.Input(outputFolder); _sheetPanel.Children.Add(Ui.Text("输出文件夹")); _sheetPanel.Children.Add(_output);
        var browse = new Button { Content = "选择输出目录…", HorizontalAlignment = HorizontalAlignment.Stretch };
        browse.Click += async (_, _) => { if (await Ui.Folder(this, "选择截图目录") is { } path) _output.Text = path; }; _sheetPanel.Children.Add(browse);
        _generate = new() { Content = "批量生成多宫格截图", HorizontalAlignment = HorizontalAlignment.Stretch };
        _generate.Click += async (_, _) => await Generate(); _sheetPanel.Children.Add(_generate);
        _preview = new() { Height = 165, Stretch = Stretch.Uniform };
        _sheetPanel.Children.Add(new Border { Classes = { "media-preview" }, Child = _preview, Margin = new(0, 5) });
        var open = new Button { Content = "打开输出文件夹", HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += async (_, _) => { try { var folder = System.IO.Path.GetFullPath(_output.Text ?? ""); Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch (Exception ex) { await Ui.Message(this, "打开失败", ex.Message); } }; _sheetPanel.Children.Add(open);
        tabs.Items.Add(new TabItem { Header = "多宫格截图", Content = new ScrollViewer { Content = _sheetPanel } }); body.Children.Add(tabs);
        tabs.SelectionChanged += (_, _) => Controls.WindowArtwork.SetKind(this, tabs.SelectedIndex == 1 ? "frames" : "gear");
        Grid.SetRow(body, 2); root.Children.Add(body);
        var bottom = new Grid { ColumnDefinitions = new("*,Auto,Auto"), Margin = new(0, 14, 0, 0), ColumnSpacing = 12 };
        _progressText = new() { Text = "就绪", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        bottom.Children.Add(_progressText); _stop = Ui.DialogButton("停止", () => _operation?.Cancel()); _stop.IsEnabled = false; Grid.SetColumn(_stop, 1); bottom.Children.Add(_stop);
        var close = Ui.DialogButton("关闭", Close); Grid.SetColumn(close, 2); bottom.Children.Add(close); Grid.SetRow(bottom, 3); root.Children.Add(bottom); Content = root;
        foreach (var box in new[] { _pattern, _prefix, _suffix, _find, _replace, _firstIndex, _digits }) box.TextChanged += (_, _) => InvalidatePlan();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _operation is null && !_renaming && !_importing ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) => { if (_operation is null && !_renaming && !_importing) await AddFolders(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, e) => { if (_renaming) { e.Cancel = true; return; } _closed = true; _operation?.Cancel(); (_preview.Source as Bitmap)?.Dispose(); };
        if (initial is not null) AddPaths(initial);
    }

    private static Control Pair(Control first, Control second)
    { var grid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8 }; grid.Children.Add(first); Grid.SetColumn(second, 1); grid.Children.Add(second); return grid; }
    private static void AddRow(Panel parent, string label, Control input)
    { var row = new Grid { ColumnDefinitions = new("115,*"), ColumnSpacing = 8 }; row.Children.Add(Ui.Text(label)); Grid.SetColumn(input, 1); row.Children.Add(input); parent.Children.Add(row); }

    private void AddPaths(IEnumerable<string> paths)
    {
        try
        {
            var existing = new HashSet<string>(_entries.Select(e => e.Path), BatchVideoTools.PathComparer);
            foreach (var path in BatchVideoTools.CollectVideos(paths, false))
            {
                if (!existing.Add(path)) continue;
                var row = new BatchVideoEntry(path); row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BatchVideoEntry.Include)) InvalidatePlan(); }; _entries.Add(row);
            }
            InvalidatePlan();
        }
        catch (Exception ex) { _progressText.Text = Localization.Format($"导入失败：{ex.Message}"); }
    }
    private async Task AddFolders(IEnumerable<string> paths)
    {
        if (_importing || _operation is not null || _renaming) return;
        var snapshot = paths.ToArray(); var recursive = _recursive.IsChecked == true; _importing = true; SetBusy(true); _progressText.Text = "正在查找视频…";
        try { var files = await Task.Run(() => BatchVideoTools.CollectVideos(snapshot, recursive)); if (!_closed) AddPaths(files); }
        catch (Exception ex) { if (!_closed) await Ui.Message(this, "导入失败", ex.Message); }
        finally { _importing = false; if (!_closed) { SetBusy(false); _progressText.Text = "就绪"; } }
    }
    private void InvalidatePlan()
    {
        _renamePlan = null; if (_rename is not null) _rename.IsEnabled = false;
        foreach (var row in _entries) row.NewName = "";
        Localization.SetText(_summary,$"{_entries.Count} 个视频，已勾选 {_entries.Count(e => e.Include)} 个。重命名与截图只处理勾选项。");
    }
    private async Task PreviewRename()
    {
        try
        {
            var files = _entries.Where(e => e.Include).Select(e => e.Path).ToArray(); if (files.Length == 0) throw new ArgumentException("请先勾选视频。");
            var rules = new RenameRules(_pattern.Text ?? "", _prefix.Text ?? "", _suffix.Text ?? "", _find.Text ?? "", _replace.Text ?? "", Integer(_firstIndex), Integer(_digits));
            _renamePlan = BatchVideoTools.PreviewRename(files, rules);
            var targets = _renamePlan.ToDictionary(i => i.Source, i => System.IO.Path.GetFileName(i.Target), BatchVideoTools.PathComparer);
            foreach (var row in _entries) row.NewName = targets.GetValueOrDefault(row.Path) ?? "";
            _rename.IsEnabled = _renamePlan.Any(i => i.Source != i.Target); _progressText.Text = "预览完成";
        }
        catch (Exception ex) { InvalidatePlan(); await Ui.Message(this, "重命名预览失败", ex.Message); }
    }
    private async Task ApplyRename()
    {
        if (_renamePlan is null) return; var plan = _renamePlan;
        SetBusy(true); _renaming = true;
        try { var result = await Task.Run(() => BatchVideoTools.ApplyRename(plan, _journal)); UpdatePaths(result); Localization.SetText(_progressText,$"已重命名 {result.Length} 个视频。"); }
        catch (Exception ex) { await Ui.Message(this, "重命名失败", ex.Message); }
        finally { _renaming = false; SetBusy(false); InvalidatePlan(); _undo.IsEnabled = File.Exists(_journal); }
    }
    private async Task UndoRename()
    {
        SetBusy(true); _renaming = true;
        try { var result = await Task.Run(() => BatchVideoTools.UndoRename(_journal)); UpdatePaths(result); Localization.SetText(_progressText,$"已还原 {result.Length} 个文件名。"); _undo.IsEnabled = false; }
        catch (Exception ex) { await Ui.Message(this, "撤销失败", ex.Message); }
        finally { _renaming = false; SetBusy(false); InvalidatePlan(); }
    }
    private void UpdatePaths(IReadOnlyList<RenameItem> mappings)
    {
        var map = mappings.ToDictionary(i => i.Source, i => i.Target, BatchVideoTools.PathComparer);
        foreach (var entry in _entries) if (map.TryGetValue(entry.Path, out var target)) { entry.Renamed(target); entry.Status = "已重命名"; }
        Renamed?.Invoke(mappings);
    }
    private async Task Generate()
    {
        var selected = _entries.Where(e => e.Include).ToArray(); ContactSheetOptions options; string folder;
        try
        {
            if (selected.Length == 0) throw new ArgumentException("请先勾选视频。");
            options = new(Integer(_columns), Integer(_rows), Integer(_cellWidth), Integer(_cellHeight), Integer(_sheets), (string?)_format.SelectedItem ?? "jpg", _timestamps.IsChecked == true, Number(_start), Number(_end));
            BatchVideoTools.ValidateContactSheet(options); if (string.IsNullOrWhiteSpace(_output.Text)) throw new ArgumentException("请选择输出目录。"); folder = System.IO.Path.GetFullPath(_output.Text);
        }
        catch (Exception ex) { await Ui.Message(this, "截图参数错误", ex.Message); return; }
        _operation = new(); var token = _operation.Token; SetBusy(true); _stop.IsEnabled = true; int success = 0, failed = 0;
        try
        {
            for (var i = 0; i < selected.Length; i++)
            {
                token.ThrowIfCancellationRequested(); var entry = selected[i]; entry.Status = "正在抽帧…";
                var progress = new Progress<ContactSheetProgress>(p => { if (!_closed) { entry.Status = Localization.Format($"生成中 {p.Percent:0}%"); _progressText.Text = $"{entry.Name} · {p.Message}"; } });
                try
                {
                    var outputs = await BatchVideoTools.GenerateContactSheets(_engine, entry.Path, folder, options, progress, token);
                    if (_closed) return; entry.LastSheet = outputs.LastOrDefault(); entry.Status = Localization.Format($"已生成 {outputs.Length} 张"); success++;
                    if (entry.LastSheet is { } path) ShowPreview(path);
                }
                catch (OperationCanceledException) { if (!_closed) entry.Status = "已停止"; throw; }
                catch (Exception ex) { if (!_closed) { entry.Status = Localization.Format($"失败：{ex.Message}"); ToolTip.SetTip(_list, ex.Message); } failed++; }
            }
            if (!_closed) Localization.SetText(_progressText,$"批量截图完成：成功 {success} 个视频，失败 {failed} 个。");
        }
        catch (OperationCanceledException) { if (!_closed) Localization.SetText(_progressText,$"已停止。已完成 {success} 个视频，生成的图片已保留。"); }
        finally { _operation.Dispose(); _operation = null; if (!_closed) { SetBusy(false); _stop.IsEnabled = false; } }
    }
    private void ShowPreview(string path)
    {
        try { var old = _preview.Source as Bitmap; using var stream = File.OpenRead(path); _preview.Source = Bitmap.DecodeToWidth(stream, 700); old?.Dispose(); }
        catch (Exception ex) { _progressText.Text = Localization.Format($"图片预览失败：{ex.Message}"); }
    }
    private void SetBusy(bool busy) { _importBar.IsEnabled = !busy; _renamePanel.IsEnabled = !busy; _sheetPanel.IsEnabled = !busy; _list.IsEnabled = !busy; _generate.IsEnabled = !busy; }
    private static int Integer(TextBox input) => int.Parse(input.Text ?? "", CultureInfo.InvariantCulture);
    private static double Number(TextBox input) => double.Parse(input.Text ?? "", CultureInfo.InvariantCulture);
}
