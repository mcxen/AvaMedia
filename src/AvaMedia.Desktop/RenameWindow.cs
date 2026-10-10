using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class RenameWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<MediaFileEntry> _entries = [];
    private readonly ObservableCollection<RenameStep> _rules = [];
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Padding = new(0) };
    private readonly ListBox _ruleList = new() { MinHeight = 120, MaxHeight = 180, Padding = new(0) };
    private readonly StackPanel _importBar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _renamePanel = new() { Spacing = 10 };
    private readonly StackPanel _ruleEditor = new() { Spacing = 8 };
    private readonly StackPanel _catalog = new() { Spacing = 8 };
    private readonly WrapPanel _ruleActions = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _sort = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private static readonly string[] SortLabels = ["导入顺序", "名称升序", "名称降序", "修改时间"];
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹", IsChecked = true };
    private readonly TextBlock _summary = Ui.Text("", "caption");
    private readonly TextBlock _progressText = Ui.Status("就绪");
    private readonly Button _rename, _undo, _stop, _previewButton;
    private readonly TextBox _pattern = Ui.Input("{name}_{index}");
    private readonly string _journal;
    private readonly Func<IEnumerable<string>, IDisposable>? _reserveFiles;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewCancellation, _operation;
    private RenameItem[]? _renamePlan;
    private bool _closed, _renaming, _importing, _syncingTemplate, _updatingSortLabels;
    private int _revision;
    public event Action<IReadOnlyList<RenameItem>>? Renamed;

    private sealed class RenameStep(string label, RenameOperation operation) : Observable
    {
        private RenameOperation _operation = operation;
        private int _number;
        public string Label { get; } = label;
        public RenameOperation Operation { get => _operation; set { if (Set(ref _operation, value)) { Raise(nameof(Enabled)); Raise(nameof(Title)); } } }
        public bool Enabled { get => Operation.Enabled; set => Operation = Operation with { Enabled = value }; }
        public int Number { get => _number; set { if (Set(ref _number, value)) Raise(nameof(Title)); } }
        public string Title => $"{Number}. {Localization.Text(Label)}";
        public void RefreshLanguage() => Raise(nameof(Title));
    }

    public RenameWindow(IMediaEngine engine, AppSettings? settings = null, IEnumerable<string>? initial = null,
        string? journalPath = null, Func<Window, Task>? manageModels = null, Func<IEnumerable<string>, IDisposable>? reserveFiles = null)
    {
        _engine = engine; _settings = settings ?? new(); _manageModels = manageModels;
        _reserveFiles = reserveFiles;
        _journal = journalPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "batch-rename.json");
        Title = "批量重命名"; Width = 1280; Height = 780; MinWidth = 1060; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Controls.WindowArtwork.SetKind(this, "gear");
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(16), RowSpacing = 10 };
        _importBar.Children.Add(Ui.Button("添加图片 / 视频…", async () =>
        {
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择图片或视频"), AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType(Localization.Text("图片和视频")) { Patterns = VideoFormats.InputExtensions.Concat(new[] { "jpg", "jpeg", "png", "webp", "bmp", "tif", "tiff", "gif", "ico", "avif", "heic", "heif" }).Select(extension => "*." + extension).ToArray() }, FilePickerFileTypes.All] });
                await AddFolders(files.Select(file => file.TryGetLocalPath()).OfType<string>());
            }
            catch (Exception error) { await ShowErrorAsync("导入失败", error); }
        }));
        _importBar.Children.Add(Ui.Button("添加文件夹…", async () =>
        {
            try { var folders = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true }); await AddFolders(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>()); }
            catch (Exception error) { await ShowErrorAsync("导入失败", error); }
        }));
        _importBar.Children.Add(_recursive);
        _importBar.Children.Add(Ui.Button("移除选中", () =>
        {
            foreach (var row in _list.SelectedItems?.Cast<MediaFileEntry>().ToArray() ?? []) _entries.Remove(row);
            InvalidatePlan();
        }));
        _importBar.Children.Add(Ui.Button("全选", () => SelectFiles(false)));
        _importBar.Children.Add(Ui.Button("反选", () => SelectFiles(true)));
        RefreshSortLabels();
        _sort.SelectionChanged += (_, _) => { if (!_updatingSortLabels && _sort.SelectedIndex >= 0) SortFiles(_sort.SelectedIndex); }; _importBar.Children.Add(_sort);
        root.Children.Add(_importBar); Grid.SetRow(_summary, 1); root.Children.Add(_summary);
        var body = new Grid { ColumnDefinitions = new("160,*,320"), ColumnSpacing = 12 };
        InitializeCatalog(); body.Children.Add(new ScrollViewer { Content = _catalog, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var filesArea = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 4 };
        var header = new Grid { ColumnDefinitions = new("28,55,*,*,80"), ColumnSpacing = 6, Classes = { "table-header" } };
        foreach (var (column, label) in new[] { (1, "类型"), (2, "原文件名"), (3, "新名称预览"), (4, "状态") })
        { var text = Ui.Text(label, "caption"); Grid.SetColumn(text, column); header.Children.Add(text); }
        filesArea.Children.Add(header);
        _list.ItemsSource = _entries;
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters = { new Setter(PaddingProperty, new Thickness(4, 5)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.ItemTemplate = new FuncDataTemplate<MediaFileEntry>((entry, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("28,55,*,*,80"), ColumnSpacing = 6 };
            var check = new CheckBox { MinHeight = 22 }; check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            foreach (var (column, property) in new[] { (1, nameof(MediaFileEntry.Kind)), (2, nameof(MediaFileEntry.Name)), (3, nameof(MediaFileEntry.NewName)), (4, nameof(MediaFileEntry.Status)) })
            {
                var text = Ui.Text("", "caption"); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
                if (column is 2 or 3) { Localization.SetIsUserText(text, true); text.Bind(ToolTip.TipProperty, new Binding(property)); }
                text.Bind(TextBlock.TextProperty, new Binding(property)); Grid.SetColumn(text, column); row.Children.Add(text);
            }
            if (_settings.EnableBetaFeatures)
            {
                var keyword = Ui.Input(); keyword.Watermark = Localization.Text("匹配关键词"); keyword.Margin = new(0, 5, 0, 0);
                Localization.SetIsUserText(keyword, true); keyword.Bind(TextBox.TextProperty, new Binding(nameof(MediaFileEntry.Keyword)) { Mode = BindingMode.TwoWay });
                keyword.Bind(IsVisibleProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = _semantic });
                row.RowDefinitions = new("Auto,Auto"); Grid.SetRow(keyword, 1); Grid.SetColumn(keyword, 2); Grid.SetColumnSpan(keyword, 2); row.Children.Add(keyword);
            }
            row.Bind(ToolTip.TipProperty, new Binding(nameof(MediaFileEntry.Details))); return row;
        });
        Grid.SetRow(_list, 1); filesArea.Children.Add(_list); Grid.SetColumn(filesArea, 1); body.Children.Add(filesArea);
        var side = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8 };
        side.Children.Add(Ui.Text("规则顺序", "settingsHeading"));
        _ruleList.ItemsSource = _rules;
        _ruleList.ItemTemplate = new FuncDataTemplate<RenameStep>((step, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("28,*"), ColumnSpacing = 4 };
            var enabled = new CheckBox { MinHeight = 22 }; enabled.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(RenameStep.Enabled)) { Mode = BindingMode.TwoWay }); row.Children.Add(enabled);
            var title = Ui.Text("", "caption"); title.Bind(TextBlock.TextProperty, new Binding(nameof(RenameStep.Title))); Grid.SetColumn(title, 1); row.Children.Add(title); return row;
        });
        _ruleList.SelectionChanged += (_, _) => BuildRuleEditor(); Grid.SetRow(_ruleList, 1); side.Children.Add(_ruleList);
        foreach (var (text, action) in new (string, Action)[] { ("上移", () => MoveRule(-1)), ("下移", () => MoveRule(1)), ("删除规则", RemoveRule), ("保存规则…", async () => await SaveRulesAsync()), ("载入规则…", async () => await LoadRulesAsync()) })
        { var button = Ui.Button(text, action); button.Classes.Add("field-action"); button.Margin = new(0, 0, 5, 5); _ruleActions.Children.Add(button); }
        Grid.SetRow(_ruleActions, 2); side.Children.Add(_ruleActions);
        _renamePanel.Children.Add(_ruleEditor); InitializeSemanticRename();
        var scroll = new ScrollViewer { Content = _renamePanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 3); side.Children.Add(scroll);
        Grid.SetColumn(side, 2); body.Children.Add(side); Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(_progressText);
        _stop = Ui.DialogButton("停止", () => { _operation?.Cancel(); _previewCancellation?.Cancel(); }); _stop.IsVisible = false;
        _undo = Ui.Button("撤销上一次重命名", async () => await UndoRename()); _undo.IsEnabled = BatchRename.CanUndo(_journal);
        _previewButton = Ui.Button("预览新名称", async () => await PreviewRename());
        _rename = Ui.DialogButton("执行重命名", async () => await ApplyRename()); _rename.Classes.Add("primary"); _rename.IsEnabled = false;
        var close = Ui.DialogButton("关闭", Close);
        foreach (var (column, button) in new[] { (1, _stop), (2, _undo), (3, _previewButton), (4, close), (5, _rename) }) { Grid.SetColumn(button, column); footer.Children.Add(button); }
        var feedback = new StackPanel { Spacing = 6 }; feedback.Children.Add(_semanticActivity); feedback.Children.Add(footer); Grid.SetRow(feedback, 3); root.Children.Add(feedback);
        Content = root;
        _pattern.TextChanged += (_, _) => SyncSemanticTemplate();
        AddRule("新名称模板", new(RenameAction.Template) { Text = "{name}_{index}" });
        Localization.Changed += RenameLanguageChanged;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) => args.DragEffects = _operation is null && !_renaming && !_importing ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, args) => { if (_operation is null && !_renaming && !_importing) await AddFolders(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, args) => { if (_renaming) { args.Cancel = true; return; } _closed = true; _operation?.Cancel(); _previewCancellation?.Cancel(); _lifetime.Cancel(); };
        Closed += (_, _) => { Localization.Changed -= RenameLanguageChanged; _lifetime.Dispose(); };
        AddPaths(initial ?? []);
    }

    private void RenameLanguageChanged(object? sender, EventArgs args)
    { RefreshSortLabels(); foreach (var rule in _rules) rule.RefreshLanguage(); foreach (var entry in _entries) entry.RefreshLanguage(); BuildRuleEditor(); InvalidatePlan(); }
    private void RefreshSortLabels()
    {
        _updatingSortLabels = true;
        try { _sort.ItemsSource = SortLabels.Select(Localization.Text).ToArray(); _sort.SelectedIndex = _sortOrder; }
        finally { _updatingSortLabels = false; }
    }
    private void SelectFiles(bool invert)
    {
        _semanticApplying = true;
        try { foreach (var entry in _entries) entry.Include = !invert || !entry.Include; }
        finally { _semanticApplying = false; }
        InvalidatePlan();
    }
    private async Task ShowErrorAsync(string title, Exception error)
    { AppDiagnostics.Record(title, error); if (!_closed) { try { await Ui.Message(this, title, error.Message); } catch (Exception noticeError) { AppDiagnostics.Record("Rename error notice", noticeError); } } }
    private static Grid AddRow(Panel panel, string label, Control control)
    { control=Ui.Parameter(control,label); var row = new Grid { ColumnDefinitions = new("100,*"), ColumnSpacing = 8 }; row.Children.Add(Ui.Text(label, "caption")); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row); return row; }
}
