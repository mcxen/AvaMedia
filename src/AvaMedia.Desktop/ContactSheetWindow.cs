using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Controls;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>Fast sheet preview and submission of independent high-resolution export tasks.</summary>
public sealed partial class ContactSheetWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<VideoEntry> _entries = [];
    private readonly ListBox _list;
    private readonly EmptyState _emptyFiles = new() { Text = "尚未添加视频", IsHitTestVisible = false };
    private readonly StackPanel _importBar;
    private readonly StackPanel _sheetPanel;
    private readonly TextBlock _summary = Label("", secondary: true);
    private readonly TextBlock _progressText = Label("就绪", secondary: true);
    private readonly TextBox _output;
    private readonly Button _generate;
    private readonly Action<Job[]>? _enqueue;
    private readonly Func<IEnumerable<string>>? _reservedOutputs;
    private readonly ComboBox _font;
    private readonly ComboBox _exportScale;
    private readonly ToggleSwitch _avoidBlur = new() { Classes = { "ac" }, OnContent = "", OffContent = "" };
    private readonly ToggleSwitch _sharpen = new() { Classes = { "ac" }, OnContent = "", OffContent = "" };
    private readonly ToggleSwitch _videoInformation = new() { Classes = { "ac" }, IsChecked = true, OnContent = "", OffContent = "" };
    private static readonly string[] FontLabels = ["默认（跟随界面字体）", "Inter", "Noto Sans SC", "Fusion Pixel"];
    private static readonly string[] FontIds = ["default", "inter", "noto-sans-sc", "fusion-pixel"];
    private readonly CheckBox _recursive;
    private readonly NumericUpDown _columns = Ui.ComponentNumber(3, 1, 10);
    private readonly NumericUpDown _rows = Ui.ComponentNumber(3, 1, 10);
    private readonly NumericUpDown _cellWidth = Ui.ComponentNumber(320, 64, 1920);
    private readonly NumericUpDown _cellHeight = Ui.ComponentNumber(180, 64, 1080);
    private readonly NumericUpDown _sheets = Ui.ComponentNumber(1, 1, 100);
    private readonly ToggleSwitch _timestamps = new() { Classes = { "ac" }, IsChecked = true, OnContent = "", OffContent = "" };
    private readonly SegmentedControl _presets;
    private readonly SegmentedControl _formats;
    private string _format = "jpg";
    private bool _importing;
    private bool _closed;
    private bool _updating;

    public ContactSheetWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? initial = null,
        Action<Job[]>? enqueue = null, Func<IEnumerable<string>>? reservedOutputs = null)
    {
        _engine = engine; _enqueue = enqueue; _reservedOutputs = reservedOutputs;
        Title = "多宫格截图";
        Width = 1252; Height = 900; MinWidth = 1080; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("contact-sheet");
        Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/"))
            { Source = new Uri("avares://AvaMedia.Desktop/Styles/ContactSheetStyles.axaml") });
        var saved = new Storage().LoadToolOptions<ContactSheetOptions>("contact-sheet");
        if (saved is not null)
        {
            _columns.Value = saved.Columns; _rows.Value = saved.Rows;
            _cellWidth.Value = saved.CellWidth; _cellHeight.Value = saved.CellHeight;
            _sheets.Value = saved.SheetsPerVideo; _format = saved.Format; _timestamps.IsChecked = saved.Timestamps;
        }
        _font = Ui.ComponentCombo(FontLabels, FontLabels[Math.Max(0, Array.IndexOf(FontIds, saved?.Font ?? "default"))]);
        _font.Width = 190;
        _exportScale = Ui.ComponentCombo(["1×", "2×", "3×", "4×"], $"{saved?.ExportScale ?? 3}×");
        _exportScale.Width = 76;
        _sharpen.IsChecked = saved?.Sharpen ?? false;
        _avoidBlur.IsChecked = saved?.AvoidBlur ?? false;
        _videoInformation.IsChecked = saved?.VideoInformation ?? true;
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(12, 8, 12, 0), RowSpacing = 8 };
        var body = new Grid { ColumnDefinitions = new("Auto,*,Auto") }; Resource(body, Grid.ColumnSpacingProperty, "AcGap");
        root.Children.Add(body);

        var filesArea = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 6 };
        var fileHeader = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new(6, 2, 6, 0) };
        fileHeader.Children.Add(Label("视频", bold: true));
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_summary, 1); fileHeader.Children.Add(_summary); filesArea.Children.Add(fileHeader);
        _list = new ListBox { ItemsSource = _entries, SelectionMode = SelectionMode.Multiple, Padding = new(0) };
        _list.ItemTemplate = new FuncDataTemplate<VideoEntry>((entry, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("20,46,*"), ColumnSpacing = 8 };
            var check = new CheckBox { Classes = { "ac" }, VerticalAlignment = VerticalAlignment.Center };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(VideoEntry.Include)) { Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(check, "包含视频 " + entry?.Name); row.Children.Add(check);
            var image = new Image { Width = 46, Height = 28, Stretch = Stretch.UniformToFill };
            image.Bind(Image.SourceProperty, new Binding(nameof(VideoEntry.Thumbnail)));
            var thumbnail = new Border { Child = image, Classes = { "sheet-thumbnail" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(thumbnail, 1); row.Children.Add(thumbnail);
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var name = Label(""); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(name, true); name.Bind(TextBlock.TextProperty, new Binding(nameof(VideoEntry.Name)));
            name.Bind(ToolTip.TipProperty, new Binding(nameof(VideoEntry.Path))); text.Children.Add(name); SheetLabel(name);
            var details = Label("", secondary: true); details.TextWrapping = TextWrapping.NoWrap; details.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Bind(TextBlock.TextProperty, new Binding(nameof(VideoEntry.Details)));
            details.Bind(ToolTip.TipProperty, new Binding(nameof(VideoEntry.Details))); text.Children.Add(details); SheetLabel(details);
            Grid.SetColumn(text, 2); row.Children.Add(text); return row;
        });
        _list.SelectionChanged += (_, _) => SelectVideo();
        var fileContent = new Grid(); fileContent.Children.Add(_list); fileContent.Children.Add(_emptyFiles);
        Grid.SetRow(fileContent, 1); filesArea.Children.Add(fileContent);
        _importBar = new StackPanel { Spacing = 6, Margin = new(6, 0, 6, 2) };
        var imports = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var import = new Button { Content = "添加视频…", Classes = { "ac", "sheet-action" } };
        var filesItem = new MenuItem { Header = "选择视频…" };
        filesItem.Click += async (_, _) =>
        {
            var files = await ToolFilePicker.OpenFilesAsync(this, new()
            {
                Title = Localization.Text("选择多个视频"), AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Localization.Text("视频")) { Patterns = VideoFormats.InputExtensions.Select(extension => "*." + extension).ToArray() }, FilePickerFileTypes.All]
            });
            AddPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        };
        var foldersItem = new MenuItem { Header = "选择文件夹…" };
        foldersItem.Click += async (_, _) =>
        {
            var folders = await ToolFilePicker.OpenFoldersAsync(this, new() { Title = Localization.Text("选择视频文件夹"), AllowMultiple = true });
            await AddFolders(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        };
        import.Flyout = new MenuFlyout { Items = { filesItem, foldersItem } };
        imports.Children.Add(import);
        var remove = Ui.Button("移除", () =>
        {
            foreach (var row in _list.SelectedItems?.Cast<VideoEntry>().ToArray() ?? [])
            {
                _entries.Remove(row);
                row.CancelMetadata();
                if (row.Thumbnail is { } thumbnail) { row.Thumbnail = null; thumbnail.Dispose(); }
            }
            if (_entries.Count > 0 && _list.SelectedItem is null) _list.SelectedIndex = 0;
            SelectVideo(); InvalidatePlan();
        });
        remove.Classes.Add("sheet-action"); imports.Children.Add(remove);
        _importBar.Children.Add(new Border { Child = imports, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 8, 0, 0) });
        Resource((Border)_importBar.Children[0], Border.BorderBrushProperty, "UiBorder");
        _recursive = new CheckBox { Classes = { "ac" }, Content = "包含子文件夹", IsChecked = true };
        _importBar.Children.Add(_recursive); Grid.SetRow(_importBar, 2); filesArea.Children.Add(_importBar);
        var videoCard = Card(filesArea); videoCard.Bind(WidthProperty, new DynamicResourceExtension("SheetVideoWidth"));
        body.Children.Add(videoCard);

        var previewCard = CreatePreview(); Grid.SetColumn(previewCard, 1); body.Children.Add(previewCard);
        _sheetPanel = new StackPanel(); Resource(_sheetPanel, StackPanel.SpacingProperty, "AcGap");
        _sheetPanel.Bind(WidthProperty, new DynamicResourceExtension("SheetSettingsWidth"));
        var gridSettings = new StackPanel();
        _presets = Segments(["2 × 2", "3 × 3", "4 × 4", "5 × 4", "自定义"], value =>
        {
            if (_updating || value == "自定义") return;
            var parts = value.Split('×'); _updating = true;
            _columns.Value = int.Parse(parts[0].Trim()); _rows.Value = int.Parse(parts[1].Trim()); _updating = false;
            SchedulePreview();
        }); gridSettings.Children.Add(_presets);
        AddRow(gridSettings, "列数", _columns); AddRow(gridSettings, "行数", _rows);
        AddRow(gridSettings, "每个视频拼图数量", _sheets);
        _sheetPanel.Children.Add(Group("宫格", gridSettings));
        var sampling = new StackPanel();
        AddRow(sampling, "截图区间", _rangeDescription);
        _samplingDescription.Margin = new(12, 2, 12, 10); sampling.Children.Add(_samplingDescription);
        _sheetPanel.Children.Add(Group("取帧", sampling));
        var picture = new StackPanel();
        AddRow(picture, "单格宽度（像素）", _cellWidth);
        AddRow(picture, "单格高度（像素）", _cellHeight);
        AddRow(picture, "文字字体", _font);
        AddRow(picture, "显示视频信息", _videoInformation);
        AddRow(picture, "显示每帧时间戳", _timestamps);
        _sheetPanel.Children.Add(Group("画面", picture));
        var outputSettings = new StackPanel();
        var formatButtons = Segments(["JPG", "PNG"], value => { _format = value.ToLowerInvariant(); InvalidatePlan(); });
        formatButtons.Margin = new(0); formatButtons.Width = 110;
        _formats = formatButtons;
        AddRow(outputSettings, "图片格式", formatButtons);
        AddRow(outputSettings, "导出倍率", _exportScale);
        AddRow(outputSettings, "轻微锐化", _sharpen);
        AddRow(outputSettings, "避开模糊帧", _avoidBlur);
        ToolTip.SetTip(_avoidBlur, "仅在相邻帧明显更清晰时替换；时间码使用实际选择的时间，导出会稍慢。");
        ToolTip.SetTip(_exportScale, "按倍率计算每格像素，使用原视频重新精确抽帧。");
        ToolTip.SetTip(_sharpen, "高清抽帧后轻微锐化，适用于缩小后的画面。");
        var location = new StackPanel { Spacing = 6, Margin = new(12, 4, 12, 10) }; location.Children.Add(Label("保存位置", secondary: true));
        _output = Ui.ComponentInput(outputFolder); _output.IsReadOnly = true; Localization.SetIsUserText(_output, true);
        ToolTip.SetTip(_output, outputFolder);
        var outputRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 }; outputRow.Children.Add(_output);
        var browse = new Button { Content = "浏览…", Classes = { "ac", "field-action" } };
        browse.Click += async (_, _) =>
        {
            if (await Ui.Folder(this, "选择截图目录") is { } path) { _output.Text = path; ToolTip.SetTip(_output, path); }
        };
        Grid.SetColumn(browse, 1); outputRow.Children.Add(browse); location.Children.Add(outputRow); outputSettings.Children.Add(location);
        _sheetPanel.Children.Add(Group("输出", outputSettings));
        var settingsView = new ScrollViewer { Content = _sheetPanel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(settingsView, 2); body.Children.Add(settingsView);

        var bottom = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 10, MinHeight = 44, Margin = new(4, 0, 4, 8) };
        _progressText.TextWrapping = TextWrapping.Wrap; bottom.Children.Add(_progressText);
        var open = Ui.Button("打开输出文件夹", () => _ = OpenOutputFolder());
        open.Classes.Add("sheet-action"); Grid.SetColumn(open, 1); bottom.Children.Add(open);
        var close = Ui.Button("关闭", Close); close.Classes.Add("sheet-action"); close.IsCancel = true; Grid.SetColumn(close, 2); bottom.Children.Add(close);
        _generate = new Button { Content = "高清导出", IsDefault = true, Classes = { "ac", "primary", "sheet-action" } };
        _generate.Click += async (_, _) => await Generate(); Grid.SetColumn(_generate, 3); bottom.Children.Add(_generate);
        Grid.SetRow(bottom, 1); root.Children.Add(bottom); Content = WindowChrome.WrapDialog(this, root);
        InitializePreviewMotion();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = !_importing ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (!_importing) await AddFolders(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>() ?? []);
        });
        Closing += (_, _) =>
        {
            _closed = true; _previewWork?.Cancel(); _filmWork?.Cancel(); _lifetime.Cancel();
            StopPreviewMotion(); ClearPreview(); _previewSurface.Dispose(); ClearFilm();
            foreach (var entry in _entries) { entry.CancelMetadata(); entry.Thumbnail?.Dispose(); }
        };
        foreach (var number in new[] { _columns, _rows, _cellWidth, _cellHeight, _sheets })
            number.ValueChanged += (_, _) => { if (!_updating) SchedulePreview(); };
        _timestamps.IsCheckedChanged += (_, _) => { if (!_updating) SchedulePreview(); };
        _videoInformation.IsCheckedChanged += (_, _) => { if (!_updating) SchedulePreview(); };
        _font.SelectionChanged += (_, _) => { ApplySheetFonts(); SaveOptions(); SchedulePreview(); };
        _exportScale.SelectionChanged += (_, _) => { SaveOptions(); SchedulePreview(); };
        _avoidBlur.IsCheckedChanged += (_, _) => { if (!_updating) SaveOptions(); };
        _sharpen.IsCheckedChanged += (_, _) => { if (!_updating) SaveOptions(); };
        ApplySheetFonts();
        _updating = true; UpdatePreset(); _formats.SelectedItem = _format.ToUpperInvariant(); _updating = false;
        ActualThemeVariantChanged += (_, _) => { ApplySheetFonts(); if (_font.SelectedIndex == 0) SchedulePreview(); };
        if (initial is not null) AddPaths(initial);
        Opened += (_, _) => { if (_entries.Count > 0) _list.SelectedIndex = 0; else SchedulePreview(); };
        InvalidatePlan(); Controls.WindowArtwork.SetKind(this, "frames");
    }

    private static TextBlock Label(string text, bool secondary = false, bool bold = false)
    {
        var label = Ui.ComponentText(text); if (secondary) label.Classes.Add("caption");
        if (bold) label.FontWeight = FontWeight.SemiBold; return label;
    }
    private static void Resource(AvaloniaObject target, AvaloniaProperty property, string key)
        => target.Bind(property, new DynamicResourceExtension(key));
    private static Border Card(Control content)
        => new AvaMedia.Controls.Card { Child = content };
    private static StackPanel Group(string title, Control content)
    {
        var panel = new StackPanel { Spacing = 6 }; var label = Label(title, secondary: true, bold: true); label.Margin = new(4, 0);
        label.Classes.Add("ac-section"); panel.Children.Add(label); panel.Children.Add(new Border { Classes = { "ac-group" }, Child = content }); return panel;
    }
    private static void AddRow(StackPanel parent, string label, Control input)
    {
        if (parent.Children.Count > 0)
        {
            var line = new Border(); Resource(line, Border.BorderThicknessProperty, "AcHairlineThickness"); Resource(line, Border.BorderBrushProperty, "AcHairline"); parent.Children.Add(line);
        }
        AutomationProperties.SetName(input, label); input.HorizontalAlignment = HorizontalAlignment.Right;
        input.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Classes = { "sheet-row" } };
        row.Children.Add(Label(label)); Grid.SetColumn(input, 1); row.Children.Add(input); parent.Children.Add(row);
    }
    private static SegmentedControl Segments(string[] labels, Action<string> changed)
    {
        var segments = new SegmentedControl { ItemsSource = labels, Classes = { "sheet-segments" } };
        segments.SelectionChanged += (_, _) => { if (segments.SelectedItem is string value) changed(value); };
        return segments;
    }
    private void UpdatePreset()
    {
        var key = $"{Integer(_columns)} × {Integer(_rows)}";
        _presets.SelectedItem = _presets.Items.Contains(key) ? key : "自定义";
    }
    private async Task OpenOutputFolder()
    {
        try
        {
            var folder = Path.GetFullPath(_output.Text ?? ""); Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { await Ui.Message(this, "打开失败", ex.Message); }
    }
    private void AddPaths(IEnumerable<string> paths)
    {
        try
        {
            var existing = new HashSet<string>(_entries.Select(entry => entry.Path), BatchRename.PathComparer);
            foreach (var path in BatchVideoTools.CollectVideos(paths, false))
            {
                if (!existing.Add(path)) continue;
                var row = new VideoEntry(path);
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VideoEntry.Include)) InvalidatePlan(); };
                _entries.Add(row); _ = ReadMetadata(row);
            }
            if (_list.SelectedItem is null && _entries.Count > 0) _list.SelectedIndex = 0;
            InvalidatePlan();
        }
        catch (Exception ex) { _progressText.Text = Localization.Format($"导入失败：{ex.Message}"); }
    }
    private async Task AddFolders(IEnumerable<string> paths)
    {
        if (_importing) return;
        var snapshot = paths.ToArray(); var recursive = _recursive.IsChecked == true; _importing = true; SetBusy(true);
        _progressText.Text = "正在查找视频…";
        try { var files = await Task.Run(() => BatchVideoTools.CollectVideos(snapshot, recursive)); if (!_closed) AddPaths(files); }
        catch (Exception ex) { if (!_closed) await Ui.Message(this, "导入失败", ex.Message); }
        finally { _importing = false; if (!_closed) { SetBusy(false); InvalidatePlan(); } }
    }
    private ContactSheetOptions Options() => new(Integer(_columns), Integer(_rows), Integer(_cellWidth), Integer(_cellHeight),
        Integer(_sheets), _format, _timestamps.IsChecked == true, _range.Start, _wholeVideo ? 0 : _range.End,
        FontIds[Math.Max(0, _font.SelectedIndex)], Math.Max(1, _exportScale.SelectedIndex + 1), _sharpen.IsChecked == true,
        _videoInformation.IsChecked == true, SheetFontFamily("default").ToString(), _avoidBlur.IsChecked == true);
    private void InvalidatePlan()
    {
        _emptyFiles.IsVisible = _entries.Count == 0;
        var included = _entries.Count(entry => entry.Include);
        Localization.SetText(_summary, $"{_entries.Count} 个 · 勾选 {included} 个");
        if (!_importing)
            Localization.SetText(_progressText, $"将为 {included} 个视频后台导出 {Integer(_sheets)} 张 {_format.ToUpperInvariant()}，共 {included * Integer(_sheets) * Integer(_columns) * Integer(_rows)} 帧");
        if (_generate is not null) _generate.IsEnabled = !_importing && included > 0 && _enqueue is not null;
    }
    private void SetBusy(bool busy)
    {
        _importBar.IsEnabled = _sheetPanel.IsEnabled = _list.IsEnabled = !busy;
        _range.IsEnabled = !busy && _range.Duration > 0 && _previewEntry is not null;
        _wholeLink.IsEnabled = !busy && _previewEntry is not null;
        _generate.IsEnabled = !busy && _entries.Any(entry => entry.Include) && _enqueue is not null;
    }
    private static int Integer(NumericUpDown input) => (int)(input.Value ?? input.Minimum);

    private sealed class VideoEntry(string path) : Observable
    {
        private bool _include = true;
        private string _status = "等待";
        private Bitmap? _thumbnail;
        private MediaInfo? _info;
        private bool _metadataFinished;
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        public bool Include { get => _include; set => Set(ref _include, value); }
        public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(Details)); } }
        public Bitmap? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
        public MediaInfo? Info { get => _info; set { _info = value; Raise(nameof(Details)); } }
        public Task<MediaInfo>? Probe { get; set; }
        public string Details => (Info is { } info ? TimeSpan.FromSeconds(info.Duration).ToString(info.Duration >= 3600 ? @"h\:mm\:ss" : @"mm\:ss") : "时长待读取") + " · " + Status;
        public CancellationTokenSource MetadataWork { get; } = new();
        public void CancelMetadata() { if (!_metadataFinished) MetadataWork.Cancel(); }
        public void CompleteMetadata() { _metadataFinished = true; MetadataWork.Dispose(); }
    }
    private async Task Generate()
    {
        try
        {
            var selected = _entries.Where(entry => entry.Include).ToArray();
            if (selected.Length == 0) throw new ArgumentException("请先勾选视频。");
            if (_enqueue is null) throw new InvalidOperationException("请从主窗口打开多宫格截图后导出。");
            var options = Options(); BatchVideoTools.ValidateContactSheet(options);
            if (string.IsNullOrWhiteSpace(_output.Text)) throw new ArgumentException("请选择输出目录。");
            var folder = Path.GetFullPath(_output.Text);
            var reserved = (_reservedOutputs?.Invoke() ?? []).ToList();
            var jobs = new List<Job>();
            foreach (var entry in selected)
            {
                if (entry.Info is { } info) BatchVideoTools.ContactSheetImageSize(info, options);
                var name = Path.GetFileNameWithoutExtension(entry.Path) + $"-grid-{options.Columns}x{options.Rows}";
                var target = MediaEngine.UniqueOutput(folder, name, options.Format, reserved, directory: options.SheetsPerVideo > 1);
                reserved.Add(target);
                jobs.Add(new Job { FeatureId = "contact-sheet", Inputs = [entry.Path], Output = target,
                    Duration = entry.Info?.Duration ?? 0, Options = new() { ContactSheet = options } });
            }
            SaveOptions(); _enqueue(jobs.ToArray());
            Localization.SetText(_progressText, $"已加入任务列表：{jobs.Count} 个高清导出任务，可在主窗口查看进度。");
        }
        catch (Exception ex) { await Ui.Message(this, "截图参数错误", ex.Message); }
    }

    private void SaveOptions()
        => new Storage().SaveToolOptions("contact-sheet", Options() with { StartSeconds = 0, EndSeconds = 0 });

    private FontFamily SheetFontFamily(string? id = null) => (id ?? FontIds[Math.Max(0, _font.SelectedIndex)]) switch
    {
        "inter" => new FontFamily(PlatformAppearance.Inter + ", " + PlatformAppearance.Noto),
        "noto-sans-sc" => new FontFamily(PlatformAppearance.Noto),
        "fusion-pixel" => new FontFamily("avares://AvaMedia.Desktop/Assets/Fonts/Platinum#Fusion Pixel 12px Prop zh-Hans, " + PlatformAppearance.Noto),
        _ => this.TryFindResource("UiFont", out var value) && value is FontFamily family ? family : FontManager.Current.DefaultFontFamily
    };

    private void SheetLabel(TextBlock label)
    {
        label.FontFamily = SheetFontFamily();
        RenderOptions.SetTextRenderingMode(label, label.FontFamily.ToString().Contains("Fusion Pixel", StringComparison.Ordinal)
            ? TextRenderingMode.Alias : TextRenderingMode.Antialias);
        label.Classes.Add("sheet-text");
    }

    private void ApplySheetFonts()
    {
        var family = SheetFontFamily();
        var mode = family.ToString().Contains("Fusion Pixel", StringComparison.Ordinal) ? TextRenderingMode.Alias : TextRenderingMode.Antialias;
        foreach (var label in this.GetVisualDescendants().OfType<TextBlock>().Where(label => label.Classes.Contains("sheet-text"))
            .Append(_previewTitle).Append(_videoInformationLabel))
        {
            label.FontFamily = family;
            RenderOptions.SetTextRenderingMode(label, mode);
        }
    }
}
