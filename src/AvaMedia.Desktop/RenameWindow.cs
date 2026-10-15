using static AvaMedia.Desktop.Classification.ClassifyUi;
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
    private readonly ListBox _ruleList = new() { Classes = { "ac" }, Padding = new(0) };
    private readonly StackPanel _importBar = new() { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
    private readonly StackPanel _renamePanel = new() { [!StackPanel.SpacingProperty] = Resource("CtGroupGap") };
    private readonly StackPanel _ruleEditor = new() { [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
    private readonly StackPanel _ruleActions = new() { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
    private readonly Grid _ruleTools = new() { ColumnDefinitions = new("*,Auto,Auto"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
    private readonly CheckBox _selectAll = new() { Classes = { "ac" } };
    private readonly ComboBox _sort = new() { Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Left };
    private static readonly string[] SortLabels = ["导入顺序", "名称升序", "名称降序", "修改时间"];
    private readonly CheckBox _recursive = new() { Classes = { "ac" }, Content = "包含子文件夹", IsChecked = true };
    private readonly TextBlock _summary = Ui.ComponentText("", "caption");
    private readonly TextBlock _progressText = Ui.ComponentStatus("就绪");
    private readonly Button _rename, _undo, _stop, _addRule, _rulesMenu, _moveUp, _moveDown, _removeRule;
    private readonly TextBox _pattern = Ui.ComponentInput("{name}_{index}");
    private readonly string _journal;
    private readonly Func<IEnumerable<string>, IDisposable>? _reserveFiles;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _previewCancellation, _operation;
    private RenameItem[]? _renamePlan;
    private bool _closed, _renaming, _importing, _syncingTemplate, _updatingSortLabels, _syncingFileSelection;
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
        ApplyToolStyles(this);
        Classes.Add("rename-tool");
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AvaMedia.Desktop/"))
        { Source = new("avares://AvaMedia.Desktop/Styles/RenameStyles.axaml") });
        _engine = engine; _settings = settings ?? new(); _manageModels = manageModels;
        _reserveFiles = reserveFiles;
        _journal = journalPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "batch-rename.json");
        Title = "批量重命名"; Width = 1280; Height = 780; MinWidth = 1060; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Controls.WindowArtwork.SetKind(this, "gear");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), [!Avalonia.Controls.Control.MarginProperty] = Resource("RenameWindowPadding"), [!Grid.RowSpacingProperty] = Resource("AcGap") };
        _importBar.Children.Add(ToolFilePicker.ImportActions(this, async () =>
        {
            try
            {
                var files = await ToolFilePicker.OpenFilesAsync(this, new() { Title = Localization.Text("选择图片或视频"), AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType(Localization.Text("图片和视频")) { Patterns = VideoFormats.InputExtensions.Concat(new[] { "jpg", "jpeg", "png", "webp", "bmp", "tif", "tiff", "gif", "ico", "avif", "heic", "heif" }).Select(extension => "*." + extension).ToArray() }, FilePickerFileTypes.All] });
                await AddFolders(files.Select(file => file.TryGetLocalPath()).OfType<string>());
            }
            catch (Exception error) { await ShowErrorAsync("导入失败", error); }
        }, async () =>
        {
            try { var folders = await ToolFilePicker.OpenFoldersAsync(this, new() { AllowMultiple = true }); await AddFolders(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>()); }
            catch (Exception error) { await ShowErrorAsync("导入失败", error); }
        }));
        _importBar.Children.Add(_recursive);
        _importBar.Children.Add(Ui.Button("移除选中项", () =>
        {
            foreach (var row in _list.SelectedItems?.Cast<MediaFileEntry>().ToArray() ?? []) _entries.Remove(row);
            InvalidatePlan();
        }));
        var fileMenu = Ui.Button("文件操作…", () => { });
        fileMenu.Click += (_, _) => ShowFileMenu(fileMenu); _importBar.Children.Add(fileMenu);
        RefreshSortLabels();
        _sort.Bind(WidthProperty, Resource("RenameSortWidth"));
        Avalonia.Automation.AutomationProperties.SetName(_sort, "文件排序");
        _sort.SelectionChanged += (_, _) => { if (!_updatingSortLabels && _sort.SelectedIndex >= 0) SortFiles(_sort.SelectedIndex); }; _importBar.Children.Add(_sort);
        root.Children.Add(_importBar);
        var body = new Grid { ColumnDefinitions = new("Auto,*"), [!Grid.ColumnSpacingProperty] = Resource("AcGap") };
        var filesArea = new Grid { RowDefinitions = new("Auto,Auto,*"), [!Grid.RowSpacingProperty] = Resource("CtDetailGap") };
        filesArea.Children.Add(_summary);
        var header = FileColumns(); header.Classes.Add("rename-file-header");
        Avalonia.Automation.AutomationProperties.SetName(_selectAll, "全选参与重命名的文件");
        ToolTip.SetTip(_selectAll, "全选参与重命名的文件");
        _selectAll.IsCheckedChanged += (_, _) =>
        {
            if (_syncingFileSelection) return;
            _semanticApplying = true;
            try { foreach (var entry in _entries) entry.Include = _selectAll.IsChecked == true; }
            finally { _semanticApplying = false; }
            InvalidatePlan();
        };
        header.Children.Add(_selectAll);
        foreach (var (column, label) in new[] { (1, "类型"), (2, "原文件名"), (3, "新名称预览"), (4, "状态") })
        { var text = Ui.ComponentText(label, "caption"); Grid.SetColumn(text, column); header.Children.Add(text); }
        Grid.SetRow(header, 1); filesArea.Children.Add(header);
        _list.ItemsSource = _entries;
        _list.Classes.Add("surface");
        _list.Classes.Add("ac");
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
            { new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch), new Setter(PaddingProperty, new Thickness(0)), new Setter(MarginProperty, new Thickness(0)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(0)) } });
        _list.ItemTemplate = new FuncDataTemplate<MediaFileEntry>((entry, _) =>
        {
            var row = FileColumns();
            var check = new CheckBox { Classes = { "ac" }}; check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            check.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(MediaFileEntry.Name)));
            foreach (var (column, property) in new[] { (1, nameof(MediaFileEntry.Kind)), (2, nameof(MediaFileEntry.Name)), (3, nameof(MediaFileEntry.NewName)), (4, nameof(MediaFileEntry.Status)) })
            {
                if (column == 3)
                {
                    var preview = new RenameNamePreview();
                    preview.Bind(RenameNamePreview.OriginalNameProperty, new Binding(nameof(MediaFileEntry.Name)));
                    preview.Bind(RenameNamePreview.ProposedNameProperty, new Binding(nameof(MediaFileEntry.NewName)));
                    Grid.SetColumn(preview, column); row.Children.Add(preview); continue;
                }
                var text = Ui.ComponentText("", column == 2 ? null : "caption");
                if (column == 2) { text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; }
                if (column is 2 or 3) { Localization.SetIsUserText(text, true); text.Bind(ToolTip.TipProperty, new Binding(property)); }
                text.Bind(TextBlock.TextProperty, new Binding(property));
                if (column == 4)
                {
                    text.Bind(ToolTip.TipProperty, new Binding(nameof(MediaFileEntry.Details)));
                    var state = new StackPanel { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("CtDetailGap") };
                    state.Children.Add(FileStatus(entry!)); state.Children.Add(text); Grid.SetColumn(state, column); row.Children.Add(state);
                }
                else { Grid.SetColumn(text, column); row.Children.Add(text); }
            }
            if (_settings.EnableBetaFeatures)
            {
                var keyword = Ui.ComponentInput(); keyword.Watermark = Localization.Text("匹配关键词");
                Localization.SetIsUserText(keyword, true); keyword.Bind(TextBox.TextProperty, new Binding(nameof(MediaFileEntry.Keyword)) { Mode = BindingMode.TwoWay });
                keyword.Bind(IsVisibleProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = _semantic });
                row.RowDefinitions = new("Auto,Auto"); row.Bind(Grid.RowSpacingProperty, Resource("CtDetailGap")); Grid.SetRow(keyword, 1); Grid.SetColumn(keyword, 2); Grid.SetColumnSpan(keyword, 2); row.Children.Add(keyword);
            }
            return new AvaMedia.Controls.ListRow { Child = row, Classes = { "rename-file-row" }, [!Border.BackgroundProperty] = Resource("UiTransparent") };
        });
        Grid.SetRow(_list, 2); filesArea.Children.Add(_list); Grid.SetColumn(filesArea, 1); body.Children.Add(filesArea);
        var empty = new AvaMedia.Controls.EmptyState { Text = Localization.Text("选择需要重命名的文件"), IsVisible = _entries.Count == 0 };
        _entries.CollectionChanged += (_, _) => empty.IsVisible = _entries.Count == 0;
        Grid.SetRow(empty, 2); filesArea.Children.Add(empty);
        var side = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), [!Grid.RowSpacingProperty] = Resource("AcRowGap") };
        _ruleTools.Children.Add(new AvaMedia.Controls.SectionHeader { Text = Localization.Text("命名规则") });
        _addRule = Ui.Button("添加规则…", () => ShowRuleCatalog()); Grid.SetColumn(_addRule, 1); _ruleTools.Children.Add(_addRule);
        _rulesMenu = RuleIcon("ellipsis", "规则菜单", () => ShowRulesMenu()); Grid.SetColumn(_rulesMenu, 2); _ruleTools.Children.Add(_rulesMenu);
        side.Children.Add(_ruleTools);
        _ruleList.Bind(MinHeightProperty, Resource("RenameRuleListMinHeight"));
        _ruleList.Bind(MaxHeightProperty, Resource("RenameRuleListMaxHeight"));
        Avalonia.Automation.AutomationProperties.SetName(_ruleList, "规则顺序");
        _ruleList.ItemsSource = _rules;
        _ruleList.ItemTemplate = new FuncDataTemplate<RenameStep>((step, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
            var enabled = new CheckBox { Classes = { "ac" }}; enabled.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(RenameStep.Enabled)) { Mode = BindingMode.TwoWay }); row.Children.Add(enabled);
            var title = Ui.ComponentText("", "caption"); title.Bind(TextBlock.TextProperty, new Binding(nameof(RenameStep.Title))); Grid.SetColumn(title, 1); row.Children.Add(title); return row;
        });
        _ruleList.SelectionChanged += (_, _) => { BuildRuleEditor(); UpdateRuleActions(); }; Grid.SetRow(_ruleList, 1); side.Children.Add(_ruleList);
        _moveUp = RuleIcon("arrow-up", "上移", () => MoveRule(-1));
        _moveDown = RuleIcon("arrow-down", "下移", () => MoveRule(1));
        _removeRule = RuleIcon("trash-2", "删除规则", RemoveRule);
        foreach (var button in new[] { _moveUp, _moveDown, _removeRule }) _ruleActions.Children.Add(button);
        Grid.SetRow(_ruleActions, 2); side.Children.Add(_ruleActions);
        _renamePanel.Children.Add(_ruleEditor); InitializeSemanticRename();
        var scroll = new ScrollViewer { Content = _renamePanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 3); side.Children.Add(scroll);
        var ruleCard = Ui.ComponentCard(side); ruleCard.Bind(WidthProperty, Resource("RenameRulesWidth")); body.Children.Add(ruleCard);
        Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
        Grid.SetColumn(_progressText, 1); footer.Children.Add(_progressText);
        _stop = Ui.DialogButton("停止", () => { _operation?.Cancel(); _previewCancellation?.Cancel(); }); _stop.IsVisible = false;
        _undo = Ui.Button("撤销上一次重命名", async () => await UndoRename()); _undo.IsEnabled = BatchRename.CanUndo(_journal);
        _rename = Ui.DialogButton("执行重命名", async () => await ApplyRename()); _rename.Classes.Add("primary"); _rename.IsEnabled = false;
        foreach (var (column, button) in new[] { (0, _undo), (2, _stop), (3, _rename) }) { Grid.SetColumn(button, column); footer.Children.Add(button); }
        var feedback = new StackPanel { [!StackPanel.SpacingProperty] = Resource("CtDetailGap") }; feedback.Children.Add(_semanticStage); feedback.Children.Add(footer); Grid.SetRow(feedback, 2); root.Children.Add(feedback);
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
    { control=Ui.ComponentParameter(control,label); var row = new Grid { RowDefinitions = new("Auto,Auto"), [!Grid.RowSpacingProperty] = Resource("CtDetailGap") }; row.Children.Add(Ui.ComponentText(label, "caption")); Grid.SetRow(control, 1); row.Children.Add(control); panel.Children.Add(row); return row; }
}
