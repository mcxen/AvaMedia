using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>Virtualized browsing and local editing, shared by options and both rename tools.</summary>
public sealed class WordLibraryWindow : Window
{
    private readonly WordLibraryStore _store = new();
    private readonly WordLibraryTarget? _target;
    private readonly bool _includeSemantic;
    private readonly ComboBox _libraries = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _category = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _search = new() { Watermark = "搜索名称、描述或标签", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _selectedOnly = new() { Content = "仅已选" };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = Ui.Text("", "caption");
    private readonly TextBlock _source = Ui.Text("", "caption");
    private readonly HashSet<SelectedWord> _selected = [];
    private WordLibrary[] _catalog = [];
    private WordRow[] _rows = [];
    private bool _updating, _loaded;
    private string _categoryValue = "";

    private sealed record CategoryChoice(string Value, int Count)
    {
        public override string ToString() => Localization.Text(Value.Length == 0 ? "全部类别" : Value) + " · " + Count;
    }

    private sealed class WordRow : Observable
    {
        private bool _selected;
        public required WordCandidate Entry { get; init; }
        public required SelectedWord Key { get; init; }
        public bool Supported { get; init; }
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
    }

    public WordLibraryWindow(WordLibraryTarget? target = null, bool includeSemantic = false)
    {
        _target = target; _includeSemantic = includeSemantic;
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        Title = includeSemantic ? "选择标签组" : target is null ? "词库管理" : "选择候选词 · " + (target == WordLibraryTarget.JoyTag ? "JoyTag" : "语义匹配");
        Width = 930; Height = 710; MinWidth = 760; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 10, Margin = new(20) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(Ui.Button("新建词库…", async () => await EditAsync(null)));
        tools.Children.Add(Ui.Button("编辑 / 复制…", async () => await EditAsync(_libraries.SelectedItem as WordLibrary)));
        tools.Children.Add(Ui.Button("导入…", async () => await ImportAsync()));
        tools.Children.Add(Ui.Button("导出…", async () => await ExportAsync()));
        tools.Children.Add(Ui.Button("删除词库", async () => await DeleteAsync())); root.Children.Add(tools);
        var filters = new Grid { ColumnDefinitions = new("230,180,*,Auto"), ColumnSpacing = 8 };
        filters.Children.Add(_libraries); Grid.SetColumn(_category, 1); filters.Children.Add(_category);
        Grid.SetColumn(_search, 2); filters.Children.Add(_search); Grid.SetColumn(_selectedOnly, 3); filters.Children.Add(_selectedOnly);
        _selectedOnly.IsVisible = target is not null; Grid.SetRow(filters, 1); root.Children.Add(filters);
        var information = new StackPanel { Spacing = 5 }; information.Children.Add(_source);
        if (target is not null)
        {
            information.Children.Add(Ui.Text(includeSemantic ? "勾选标签，或全选当前列表；语义候选在下次分析时识别。" : target == WordLibraryTarget.JoyTag
                ? "仅可选择 JoyTag 支持的标签；内容分级候选可用于视频语义匹配。"
                : "自定义描述参与语义匹配；内容分级为候选结果，需人工确认。", "caption"));
            var selection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            selection.Children.Add(Ui.Button("全选当前列表", () => SelectVisible(true)));
            selection.Children.Add(Ui.Button("取消当前选择", () => SelectVisible(false)));
            selection.Children.Add(Ui.Button("清空全部", () => { _selected.Clear(); RefreshRows(); })); information.Children.Add(selection);
        }
        Grid.SetRow(information, 2); root.Children.Add(information);
        _list.ItemTemplate = new FuncDataTemplate<WordRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new("28,190,*"), ColumnSpacing = 8, Margin = new(2, 5) };
            var check = new CheckBox { IsEnabled = row.Supported, IsVisible = target is not null };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(WordRow.Selected)) { Mode = BindingMode.TwoWay }); grid.Children.Add(check);
            var label = new TextBlock { Text = WordLibraryCatalog.CandidateLabel(row.Entry), TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, label.Text);
            Localization.SetIsUserText(label, true); Grid.SetColumn(label, 1); grid.Children.Add(label);
            var description = new TextBlock { Text = row.Entry.Category + " · " + row.Entry.Description
                + (row.Entry.Tags.Length > 0 ? " · " + string.Join('+', row.Entry.Tags) : " · 语义匹配"),
                TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "caption" } };
            Localization.SetIsUserText(description, true); ToolTip.SetTip(description, description.Text);
            Grid.SetColumn(description, 2); grid.Children.Add(description); return grid;
        });
        Grid.SetRow(_list, 3); root.Children.Add(_list);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; footer.Children.Add(_count);
        var close = Ui.DialogButton(target is null ? "关闭" : "应用选择", Close); Grid.SetColumn(close, 1); footer.Children.Add(close);
        Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
        _libraries.SelectionChanged += (_, _) => RefreshRows();
        _category.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _categoryValue = (_category.SelectedItem as CategoryChoice)?.Value ?? "";
            Filter();
        };
        _search.TextChanged += (_, _) => Filter(); _selectedOnly.IsCheckedChanged += (_, _) => Filter();
        Localization.Changed += WordLanguageChanged;
        Closed += (_, _) => Localization.Changed -= WordLanguageChanged;
        Opened += async (_, _) =>
        {
            try
            {
                var view = _store.View(target);
                if (target is not null) _selected.UnionWith(_store.Selection(target.Value));
                _categoryValue = view?.Category ?? "";
                Reload(view?.LibraryId); _loaded = true;
            }
            catch (Exception error) { await Ui.Message(this, "词库读取失败", error.Message); }
        };
        Closing += async (_, args) =>
        {
            if (!_loaded || _libraries.SelectedItem is not WordLibrary library) return;
            try
            {
                var view = new WordLibraryView(library.Id, _categoryValue);
                if (target is not null)
                {
                    var valid = _catalog.SelectMany(item => item.Entries.Where(entry => _includeSemantic || entry.Supports(target.Value))
                        .Select(entry => new SelectedWord(item.Id, entry.Label))).ToHashSet();
                    _store.SaveSelection(target.Value, _selected.Where(valid.Contains).ToArray(), view);
                }
                else _store.SaveView(target, view);
            }
            catch (Exception error) { args.Cancel = true; await Ui.Message(this, "词库保存失败", error.Message); }
        };
    }
    private void WordLanguageChanged(object? sender, EventArgs args) => RefreshRows();
    private void Reload(string? id = null)
    {
        id ??= (_libraries.SelectedItem as WordLibrary)?.Id;
        _catalog = _store.Libraries(); _libraries.ItemsSource = _catalog;
        _libraries.SelectedItem = _catalog.FirstOrDefault(library => library.Id == id) ?? _catalog[0];
    }
    private void RefreshRows()
    {
        if (_libraries.SelectedItem is not WordLibrary library) return;
        _updating = true;
        try
        {
            var previousCategory = _categoryValue;
            _rows = library.Entries.Select(entry => new WordRow { Entry = entry, Key = new(library.Id, entry.Label),
                Supported = _target is null || _includeSemantic || entry.Supports(_target.Value), Selected = _selected.Contains(new(library.Id, entry.Label)) }).ToArray();
            foreach (var row in _rows) row.PropertyChanged += (_, change) =>
            {
                if (_updating || change.PropertyName != nameof(WordRow.Selected)) return;
                if (row.Selected && row.Supported) _selected.Add(row.Key); else _selected.Remove(row.Key);
                if (_selectedOnly.IsChecked == true) Filter(); else UpdateCount();
            };
            var choices = new[] { new CategoryChoice("", library.Entries.Length) }
                .Concat(library.Entries.GroupBy(entry => entry.Category).Select(group => new CategoryChoice(group.Key, group.Count()))).ToArray();
            _category.ItemsSource = choices;
            var category = choices.FirstOrDefault(choice => choice.Value == previousCategory) ?? choices[0];
            _category.SelectedItem = category; _categoryValue = category.Value; _source.Text = library.Source;
        }
        finally { _updating = false; }
        Filter();
    }
    private WordRow[] VisibleRows()
    {
        var category = (_category.SelectedItem as CategoryChoice)?.Value; var search = _search.Text?.Trim() ?? "";
        return _rows.Where(row => (string.IsNullOrEmpty(category) || row.Entry.Category == category)
            && (_selectedOnly.IsChecked != true || row.Selected)
            && (search.Length == 0 || WordLibraryCatalog.CandidateLabel(row.Entry).Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Entry.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Entry.Tags.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }
    private void Filter()
    { if (!_updating) { _list.ItemsSource = VisibleRows(); UpdateCount(); } }
    private void UpdateCount() => _count.Text = _target is null ? Localization.Format($"当前显示 {VisibleRows().Length} 个词")
        : Localization.Format($"已选 {_selected.Count} 个词 · 当前显示 {VisibleRows().Length} · 上限 {WordLibraryCatalog.MaximumCandidates}");
    private void SelectVisible(bool value)
    {
        _updating = true;
        try
        {
            foreach (var row in VisibleRows().Where(row => row.Supported))
            { row.Selected = value; if (value) _selected.Add(row.Key); else _selected.Remove(row.Key); }
        }
        finally { _updating = false; }
        Filter();
    }
    private async Task EditAsync(WordLibrary? library)
    {
        var editable = library is { BuiltIn: false };
        var name = Ui.Input(library is null ? "我的词库" : editable ? library.Name : library.Name + " 副本");
        var text = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
            Text = library is null ? "动物\t猫\tA photo of a cat.\tcat" : WordLibraryCatalog.ToText(library) };
        Localization.SetIsUserText(name, true); Localization.SetIsUserText(text, true);
        var editor = new Window { Title = "编辑词库", Width = 820, Height = 620, MinWidth = 650, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 10, Margin = new(20) };
        root.Children.Add(name);
        var hint = Ui.Text("每行一个词；或用 Tab 分隔：类别、名称、语义描述、JoyTag 标签（组合用 +）。最多 20000 个词。", "caption");
        Grid.SetRow(hint, 1); root.Children.Add(hint); Grid.SetRow(text, 2); root.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(Ui.DialogButton("取消", editor.Close));
        actions.Children.Add(Ui.DialogButton("保存词库", async () =>
        {
            try
            {
                var entries = WordLibraryCatalog.ParseText(text.Text ?? "");
                var updated = new WordLibrary(editable ? library!.Id : Guid.NewGuid().ToString("N"), name.Text?.Trim() ?? "", library?.Source ?? "自定义 · 本地", entries);
                _store.Save(updated); Reload(updated.Id); editor.Close();
            }
            catch (Exception error) { await Ui.Message(editor, "词库保存失败", error.Message); }
        }));
        Grid.SetRow(actions, 3); root.Children.Add(actions); editor.Content = root; await editor.ShowDialog(this);
    }
    private async Task ImportAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("导入词库"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Localization.Text("词库")) { Patterns = ["*.json", "*.txt", "*.tsv"] }] });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var data = new MemoryStream(); var buffer = new byte[65536]; int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            { if (data.Length + read > 8 * 1024 * 1024) throw new ArgumentException("词库文件不能超过 8 MB。"); data.Write(buffer, 0, read); }
            var content = System.Text.Encoding.UTF8.GetString(data.ToArray()).TrimStart('\uFEFF');
            var imported = files[0].Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize<WordLibrary>(content) ?? throw new ArgumentException("JSON 词库无效。")
                : new WordLibrary("", Path.GetFileNameWithoutExtension(files[0].Name), "", WordLibraryCatalog.ParseText(content));
            var library = imported with { Id = Guid.NewGuid().ToString("N"), BuiltIn = false,
                Source = "导入 · " + (string.IsNullOrWhiteSpace(imported.Source) ? files[0].Name : imported.Source) };
            _store.Save(library); Reload(library.Id);
        }
        catch (Exception error) { await Ui.Message(this, "词库导入失败", error.Message); }
    }
    private async Task ExportAsync()
    {
        if (_libraries.SelectedItem is not WordLibrary library) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("导出词库"), SuggestedFileName = "word-library.json", DefaultExtension = "json" });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, library, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error) { await Ui.Message(this, "词库导出失败", error.Message); }
    }
    private async Task DeleteAsync()
    {
        if (_libraries.SelectedItem is not WordLibrary library) return;
        try
        {
            if (library.BuiltIn) throw new ArgumentException("内置词库可复制，不能删除。");
            _store.Delete(library.Id); _selected.RemoveWhere(word => word.LibraryId == library.Id); Reload();
        }
        catch (Exception error) { await Ui.Message(this, "词库删除失败", error.Message); }
    }
}
