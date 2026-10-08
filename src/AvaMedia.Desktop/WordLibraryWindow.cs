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
    private readonly ComboBox _libraries = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _category = new() { MinWidth = 140 };
    private readonly TextBox _search = new() { Watermark = "搜索名称、描述或标签", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _selectedOnly = new() { Content = "仅已选" };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = Ui.Text("", "caption");
    private readonly TextBlock _source = Ui.Text("", "caption");
    private readonly HashSet<SelectedWord> _selected = [];
    private WordLibrary[] _catalog = [];
    private WordRow[] _rows = [];
    private bool _updating;
    public bool Applied { get; private set; }

    private sealed class WordRow : Observable
    {
        private bool _selected;
        public required WordCandidate Entry { get; init; }
        public required SelectedWord Key { get; init; }
        public bool Supported { get; init; }
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
    }

    public WordLibraryWindow(WordLibraryTarget? target = null)
    {
        _target = target;
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        Title = target is null ? "词库管理 · Beta" : "选择候选词 · " + (target == WordLibraryTarget.JoyTag ? "JoyTag" : "语义匹配");
        Width = 930; Height = 710; MinWidth = 760; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 10, Margin = new(20) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(Ui.Button("新建词库…", async () => await EditAsync(null)));
        tools.Children.Add(Ui.Button("编辑 / 复制…", async () => await EditAsync(_libraries.SelectedItem as WordLibrary)));
        tools.Children.Add(Ui.Button("导入…", async () => await ImportAsync()));
        tools.Children.Add(Ui.Button("导出…", async () => await ExportAsync()));
        tools.Children.Add(Ui.Button("删除词库", async () => await DeleteAsync())); root.Children.Add(tools);
        var filters = new Grid { ColumnDefinitions = new("230,150,*,Auto"), ColumnSpacing = 8 };
        filters.Children.Add(_libraries); Grid.SetColumn(_category, 1); filters.Children.Add(_category);
        Grid.SetColumn(_search, 2); filters.Children.Add(_search); Grid.SetColumn(_selectedOnly, 3); filters.Children.Add(_selectedOnly);
        _selectedOnly.IsVisible = target is not null; Grid.SetRow(filters, 1); root.Children.Add(filters);
        var information = new StackPanel { Spacing = 5 }; information.Children.Add(_source);
        if (target is not null)
        {
            information.Children.Add(Ui.Text(target == WordLibraryTarget.JoyTag
                ? "仅可选择 JoyTag 支持的标签；内容分级候选可用于视频语义匹配。"
                : "自定义描述参与语义匹配；内容分级为候选结果，需人工确认。", "caption"));
            var selection = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            selection.Children.Add(Ui.Button("选择当前类别 / 搜索结果", () => SelectVisible(true)));
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
            var label = new TextBlock { Text = row.Entry.Label, TextTrimming = TextTrimming.CharacterEllipsis };
            Localization.SetIsUserText(label, true); Grid.SetColumn(label, 1); grid.Children.Add(label);
            var description = new TextBlock { Text = row.Entry.Category + " · " + row.Entry.Description
                + (row.Entry.Tags.Length > 0 ? " · " + string.Join('+', row.Entry.Tags) : " · 语义匹配"),
                TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "caption" } };
            Localization.SetIsUserText(description, true); ToolTip.SetTip(description, description.Text);
            Grid.SetColumn(description, 2); grid.Children.Add(description); return grid;
        });
        Grid.SetRow(_list, 3); root.Children.Add(_list);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 }; footer.Children.Add(_count);
        var cancel = Ui.DialogButton("关闭", Close); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        if (target is not null)
        {
            var apply = Ui.DialogButton("使用所选候选词", async () =>
            {
                try
                {
                    var valid = _catalog.SelectMany(library => library.Entries.Where(entry => entry.Supports(target.Value))
                        .Select(entry => new SelectedWord(library.Id, entry.Label))).ToHashSet();
                    _store.SaveSelection(target.Value, _selected.Where(valid.Contains).ToArray()); Applied = true; Close();
                }
                catch (Exception error) { await Ui.Message(this, "词库保存失败", error.Message); }
            });
            apply.IsDefault = true; Grid.SetColumn(apply, 2); footer.Children.Add(apply);
        }
        Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
        _libraries.SelectionChanged += (_, _) => RefreshRows();
        _category.SelectionChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter(); _selectedOnly.IsCheckedChanged += (_, _) => Filter();
        Opened += async (_, _) =>
        {
            try { if (target is not null) _selected.UnionWith(_store.Selection(target.Value)); Reload(); }
            catch (Exception error) { await Ui.Message(this, "词库读取失败", error.Message); }
        };
    }
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
            _rows = library.Entries.Select(entry => new WordRow { Entry = entry, Key = new(library.Id, entry.Label),
                Supported = _target is null || entry.Supports(_target.Value), Selected = _selected.Contains(new(library.Id, entry.Label)) }).ToArray();
            foreach (var row in _rows) row.PropertyChanged += (_, change) =>
            {
                if (_updating || change.PropertyName != nameof(WordRow.Selected)) return;
                if (row.Selected && row.Supported) _selected.Add(row.Key); else _selected.Remove(row.Key);
                if (_selectedOnly.IsChecked == true) Filter(); else UpdateCount();
            };
            _category.ItemsSource = new[] { "全部类别" }.Concat(library.Entries.Select(entry => entry.Category).Distinct()).ToArray();
            _category.SelectedIndex = 0; _source.Text = library.Source;
        }
        finally { _updating = false; }
        Filter();
    }
    private WordRow[] VisibleRows()
    {
        var category = _category.SelectedItem as string; var search = _search.Text?.Trim() ?? "";
        return _rows.Where(row => (category is null or "全部类别" || row.Entry.Category == category)
            && (_selectedOnly.IsChecked != true || row.Selected)
            && (search.Length == 0 || row.Entry.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
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
