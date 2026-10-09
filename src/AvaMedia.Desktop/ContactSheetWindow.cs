using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>Dedicated batch panel; does not start or change the conversion queue.</summary>
public sealed class ContactSheetWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<MediaFileEntry> _entries = [];
    private readonly ListBox _list;
    private readonly StackPanel _importBar;
    private readonly StackPanel _sheetPanel;
    private readonly TextBlock _summary;
    private readonly TextBlock _progressText;
    private readonly TextBox _output;
    private readonly Image _preview;
    private readonly Button _generate;
    private readonly Button _stop;
    private readonly CheckBox _recursive;
    private readonly NumericUpDown _columns = Ui.Number(3,1,10,1);
    private readonly NumericUpDown _rows = Ui.Number(3,1,10,1);
    private readonly NumericUpDown _cellWidth = Ui.Number(320,64,1920,1);
    private readonly NumericUpDown _cellHeight = Ui.Number(180,64,1080,1);
    private readonly NumericUpDown _sheets = Ui.Number(1,1,100,1);
    private readonly NumericUpDown _start = Ui.Number(0,0,86400,0.1);
    private readonly NumericUpDown _end = Ui.Number(0,0,86400,0.1);
    private readonly ComboBox _format = Ui.Combo(["jpg", "png"], "jpg");
    private readonly CheckBox _timestamps = new() { Content = "显示每帧时间戳", IsChecked = true };
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _previewWork;
    private readonly TimeRangePicker _range = new();
    private string? _rangeSource;
    private bool _importing;
    private bool _closed;

    public ContactSheetWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? initial = null)
    {
        _engine = engine;
        Title = "多宫格截图";
        Width = 1120; Height = 760; MinWidth = 920; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(20) };
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
            foreach (var row in _list?.SelectedItems?.Cast<MediaFileEntry>().ToArray() ?? []) _entries.Remove(row);
            InvalidatePlan();
        }));
        _importBar.Children.Add(Ui.Button("全选", () => { foreach (var row in _entries) row.Include = true; }));
        _importBar.Children.Add(Ui.Button("反选", () => { foreach (var row in _entries) row.Include = !row.Include; }));
        root.Children.Add(_importBar);
        _summary = new() { Margin = new(0, 12), Classes = { "caption" }, Text = "尚未添加视频" };
        Grid.SetRow(_summary, 1); root.Children.Add(_summary);

        var body = new Grid { ColumnDefinitions = new("*,380"), ColumnSpacing = 16 };
        var filesArea = new Grid { RowDefinitions = new("Auto,*") };
        var header = new Grid { ColumnDefinitions = new("28,*,160"), ColumnSpacing = 5, Margin = new(1, 0), Classes = { "table-header" } };
        header.Bind(MinHeightProperty, new DynamicResourceExtension("UiTableHeaderHeight"));
        var h1 = Ui.Text("视频文件"); Grid.SetColumn(h1, 1); header.Children.Add(h1);
        var h3 = Ui.Text("状态"); Grid.SetColumn(h3, 2); header.Children.Add(h3); filesArea.Children.Add(header);
        _list = new() { ItemsSource = _entries, SelectionMode = SelectionMode.Multiple, Padding = new(0), BorderThickness = new(1) };
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) }
        });
        _list.ItemTemplate = new FuncDataTemplate<MediaFileEntry>((entry, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("28,*,160"), Margin = new(0, 6), ColumnSpacing = 5 };
            var check = new CheckBox { MinHeight = 22 };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            foreach (var (column, property) in new[] { (1, nameof(MediaFileEntry.Name)), (2, nameof(MediaFileEntry.Status)) })
            {
                var text = Ui.Text("", "caption"); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
                if (column == 1) Localization.SetIsUserText(text, true);
                text.Bind(TextBlock.TextProperty, new Binding(property)); Grid.SetColumn(text, column); row.Children.Add(text);
            }
            row.Bind(ToolTip.TipProperty, new Binding(nameof(MediaFileEntry.Details))); return row;
        });
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is MediaFileEntry entry && entry.LastSheet is { } path) ShowPreview(path); else SchedulePreview(); };
        Grid.SetRow(_list, 1); filesArea.Children.Add(_list); body.Children.Add(filesArea);
        _sheetPanel = new() { Spacing = 9, Margin = new(9) };
        var saved = new Storage().LoadToolOptions<ContactSheetOptions>("contact-sheet");
        if(saved is not null){_columns.Value=saved.Columns;_rows.Value=saved.Rows;_cellWidth.Value=saved.CellWidth;_cellHeight.Value=saved.CellHeight;_sheets.Value=saved.SheetsPerVideo;_format.SelectedItem=saved.Format;_timestamps.IsChecked=saved.Timestamps;}
        var preset = Ui.Combo(["2 × 2", "3 × 3", "4 × 4", "5 × 4", "自定义"], saved is null ? "3 × 3" : new[]{"2 × 2","3 × 3","4 × 4","5 × 4"}.Contains($"{saved.Columns} × {saved.Rows}")?$"{saved.Columns} × {saved.Rows}":"自定义");
        preset.SelectionChanged += (_, _) => { if (preset.SelectedItem is string value && value != "自定义") { var p = value.Split('×'); _columns.Value = int.Parse(p[0].Trim()); _rows.Value = int.Parse(p[1].Trim()); } };
        AddRow(_sheetPanel, "宫格预设", preset);
        var customGrid=AddRow(_sheetPanel, "列数 / 行数", Pair(_columns, _rows));customGrid.IsVisible=preset.SelectedItem as string=="自定义";
        preset.SelectionChanged+=(_,_)=>customGrid.IsVisible=preset.SelectedItem as string=="自定义";
        var advanced=new StackPanel { Spacing=9 };
        ToolTip.SetTip(_end,Localization.Text("0 = 视频结尾"));
        AddRow(advanced, "单格宽 / 高（像素）", Pair(_cellWidth, _cellHeight)); AddRow(_sheetPanel, "每视频拼图数", _sheets);
        AddRow(advanced, "截图区间", _range);
        advanced.Children.Add(Ui.Button("使用整个视频",()=>{_rangeSource=null;_start.Value=_end.Value=0;SchedulePreview();}));
        _range.Changed+=()=>{_start.Value=(decimal)_range.Start;_end.Value=(decimal)_range.End;}; AddRow(advanced, "图片格式", _format); advanced.Children.Add(_timestamps);
        _sheetPanel.Children.Add(new Expander { Header="更多选项",Content=advanced,HorizontalAlignment=HorizontalAlignment.Stretch });
        _output = Ui.Input(outputFolder);_output.IsReadOnly=true;
        var outputRow=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};outputRow.Children.Add(_output);
        var browse = new Button { Content = "浏览…", Classes={"field-action"} };
        browse.Click += async (_, _) => { if (await Ui.Folder(this, "选择截图目录") is { } path) _output.Text = path; };Grid.SetColumn(browse,1);outputRow.Children.Add(browse);AddRow(_sheetPanel,"保存位置",outputRow);
        _generate = new() { Content = "生成截图", Classes={"primary","dialog-action"} };
        _generate.Click += async (_, _) => await Generate();
        _preview = new() { Height = 230, Stretch = Stretch.Uniform };
        _sheetPanel.Children.Add(new Border { Classes = { "media-preview" }, Child = _preview, Margin = new(0, 5) });
        var open = new Button { Content = "打开输出文件夹", HorizontalAlignment = HorizontalAlignment.Stretch };
        open.Click += async (_, _) => { try { var folder = System.IO.Path.GetFullPath(_output.Text ?? ""); Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch (Exception ex) { await Ui.Message(this, "打开失败", ex.Message); } }; _sheetPanel.Children.Add(open);
        var settingsView=new ScrollViewer{Content=_sheetPanel,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetColumn(settingsView,1);body.Children.Add(settingsView);
        Grid.SetRow(body, 2); root.Children.Add(body);
        var bottom = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), Margin = new(0, 14, 0, 0), ColumnSpacing = 12 };
        _progressText = new() { Text = "就绪", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        bottom.Children.Add(_progressText); _stop = Ui.DialogButton("停止", () => _operation?.Cancel()); _stop.IsEnabled = false;_stop.IsVisible=false;Grid.SetColumn(_stop, 1); bottom.Children.Add(_stop);
        var close = Ui.DialogButton("关闭", Close); Grid.SetColumn(close, 2); bottom.Children.Add(close);
        var execute=_generate;execute.IsDefault=true;Grid.SetColumn(execute,3);bottom.Children.Add(execute);
        Grid.SetRow(bottom, 3); root.Children.Add(bottom); Content = root;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = _operation is null && !_importing ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) => { if (_operation is null && !_importing) await AddFolders(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, e) => { _previewWork?.Cancel();_previewWork?.Dispose(); _closed = true; _operation?.Cancel(); (_preview.Source as Bitmap)?.Dispose(); };
        if (initial is not null) AddPaths(initial);
        foreach(var number in new[]{_columns,_rows,_cellWidth,_cellHeight,_sheets,_start,_end})number.ValueChanged+=(_,_)=>SchedulePreview();
        _timestamps.IsCheckedChanged+=(_,_)=>SchedulePreview();
        Opened+=(_,_)=>{if(_entries.Count>0)_list.SelectedIndex=0;};
        InvalidatePlan();
        Controls.WindowArtwork.SetKind(this, "frames");
    }

    private static Control Pair(Control first, Control second)
    { var grid = new Grid { RowDefinitions = new("Auto,Auto"), RowSpacing = 6 }; grid.Children.Add(Ui.Parameter(first,"调整数值")); var adjusted=Ui.Parameter(second,"调整数值"); Grid.SetRow(adjusted, 1); grid.Children.Add(adjusted); return grid; }
    private static Grid AddRow(Panel parent, string label, Control input)
    { input=Ui.Parameter(input,label); var row = new Grid { ColumnDefinitions = new("115,*"), ColumnSpacing = 8 }; row.Children.Add(Ui.Text(label)); Grid.SetColumn(input, 1); row.Children.Add(input); parent.Children.Add(row);return row; }

    private void AddPaths(IEnumerable<string> paths)
    {
        try
        {
            var existing = new HashSet<string>(_entries.Select(e => e.Path), BatchRename.PathComparer);
            foreach (var path in BatchVideoTools.CollectVideos(paths, false))
            {
                if (!existing.Add(path)) continue;
                var row = new MediaFileEntry(path); row.PropertyChanged += (_, e) =>
                { if (e.PropertyName == nameof(MediaFileEntry.Include)) InvalidatePlan(); }; _entries.Add(row);
            }
            InvalidatePlan();
        }
        catch (Exception ex) { _progressText.Text = Localization.Format($"导入失败：{ex.Message}"); }
    }
    private async Task AddFolders(IEnumerable<string> paths)
    {
        if (_importing || _operation is not null) return;
        var snapshot = paths.ToArray(); var recursive = _recursive.IsChecked == true; _importing = true; SetBusy(true); _progressText.Text = "正在查找视频…";
        try { var files = await Task.Run(() => BatchVideoTools.CollectVideos(snapshot, recursive)); if (!_closed) AddPaths(files); }
        catch (Exception ex) { if (!_closed) await Ui.Message(this, "导入失败", ex.Message); }
        finally { _importing = false; if (!_closed) { SetBusy(false); _progressText.Text = "就绪"; } }
    }
    private void InvalidatePlan()
    {
        Localization.SetText(_summary, $"{_entries.Count} 个视频 · 勾选 {_entries.Count(entry => entry.Include)} 个");
        if (_generate is not null) _generate.IsEnabled = _operation is null && !_importing && _entries.Any(entry => entry.Include);
    }
    private async Task Generate()
    {
        var selected = _entries.Where(e => e.Include).ToArray(); ContactSheetOptions options; string folder;
        try
        {
            if (selected.Length == 0) throw new ArgumentException("请先勾选视频。");
            options = new(Integer(_columns), Integer(_rows), Integer(_cellWidth), Integer(_cellHeight), Integer(_sheets), (string?)_format.SelectedItem ?? "jpg", _timestamps.IsChecked == true, Number(_start), string.IsNullOrWhiteSpace(_end.Text)?0:Number(_end));
            BatchVideoTools.ValidateContactSheet(options);new Storage().SaveToolOptions("contact-sheet",options with { StartSeconds=0,EndSeconds=0 }); if (string.IsNullOrWhiteSpace(_output.Text)) throw new ArgumentException("请选择输出目录。"); folder = System.IO.Path.GetFullPath(_output.Text);
        }
        catch (Exception ex) { await Ui.Message(this, "截图参数错误", ex.Message); return; }
        _previewWork?.Cancel();_operation = new(); var token = _operation.Token; SetBusy(true); _stop.IsEnabled = true; int success = 0, failed = 0;
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
    private void SchedulePreview()
    {
        if(_preview is null||_closed||_operation is not null)return;
        _previewWork?.Cancel();_previewWork?.Dispose();_previewWork=new();var token=_previewWork.Token;
        var entry=_list.SelectedItem as MediaFileEntry??_entries.FirstOrDefault();if(entry is null)return;
        var options=new ContactSheetOptions(Integer(_columns),Integer(_rows),Integer(_cellWidth),Integer(_cellHeight),Integer(_sheets),(string?)_format.SelectedItem??"jpg",_timestamps.IsChecked==true,Number(_start),Number(_end));
        async Task Render()
        {
            try
            {
                await Task.Delay(350,token);
            if(_rangeSource!=entry.Path)
            {
                var info=await _engine.Probe(entry.Path,token);token.ThrowIfCancellationRequested();
                _range.SetRange(Number(_start),Number(_end)>0?Number(_end):info.Duration,info.Duration);_rangeSource=entry.Path;
            }var data=await BatchVideoTools.PreviewContactSheetAsync(_engine,entry.Path,options,token);token.ThrowIfCancellationRequested();
                if(_closed)return;using var stream=new MemoryStream(data);var bitmap=new Bitmap(stream);var old=_preview.Source as Bitmap;_preview.Source=bitmap;old?.Dispose();
            }
            catch(OperationCanceledException){}catch(Exception error){if(!token.IsCancellationRequested&&!_closed)_progressText.Text=Localization.Format($"预览读取失败：{error.Message}");}
        }
        _=Render();
    }
    private void ShowPreview(string path)
    {
        try { var old = _preview.Source as Bitmap; using var stream = File.OpenRead(path); _preview.Source = Bitmap.DecodeToWidth(stream, 700); old?.Dispose(); }
        catch (Exception ex) { _progressText.Text = Localization.Format($"图片预览失败：{ex.Message}"); }
    }
    private void SetBusy(bool busy)
    {
        _importBar.IsEnabled = _sheetPanel.IsEnabled = _list.IsEnabled = !busy;
        _generate.IsEnabled = !busy && _entries.Any(entry => entry.Include); _stop.IsVisible = busy && _operation is not null;
    }
    private static int Integer(NumericUpDown input) => (int)(input.Value??input.Minimum);
    private static double Number(NumericUpDown input) => (double)(input.Value??input.Minimum);
}
