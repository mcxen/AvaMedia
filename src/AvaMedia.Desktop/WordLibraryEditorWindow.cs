using static AvaMedia.Desktop.Classification.ClassifyUi;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal sealed class WordLibraryEditorWindow : Window
{
    private readonly ObservableCollection<WordCandidate> _entries;
    private readonly ListBox _list = new();
    private readonly TextBox _name, _label = Ui.ComponentInput(), _category = Ui.ComponentInput(), _description = Ui.ComponentInput();
    private readonly TextBlock _error = Ui.ComponentText("", "error");
    private readonly WrapPanel _tags = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private int _editingIndex = -1;

    public WordLibraryEditorWindow(WordLibrary? library)
    {
        ApplyToolStyles(this);
        Title = "编辑词库"; Width = 980; Height = 700; MinWidth = 850; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _entries = new(library?.Entries ?? []);
        _name = Ui.ComponentInput(library is null ? "我的词库" : library.BuiltIn ? library.Name + " 副本" : library.Name);
        Localization.SetIsUserText(_name,true);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), [!Grid.RowSpacingProperty] = Resource("AcRowGap"), [!Avalonia.Controls.Control.MarginProperty] = Resource("CtWindowMargin") };
        var title = new StackPanel { [!StackPanel.SpacingProperty] = Resource("CtDetailGap") }; title.Children.Add(Ui.ComponentText("词库名称")); title.Children.Add(_name); root.Children.Add(title);
        var body = new Grid { ColumnDefinitions = new("*,*"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
        var left = new Grid { RowDefinitions = new("Auto,*,Auto"), [!Grid.RowSpacingProperty] = Resource("AcRowGap") };
        left.Children.Add(Ui.ComponentText("词条")); _list.ItemsSource = _entries;
        _list.ItemTemplate = new FuncDataTemplate<WordCandidate>((entry, _) =>
        {
            var row = new StackPanel { Spacing = 1, Margin = new(2, 1) };
            var label=Ui.ComponentText(entry?.Label??"");var category=Ui.ComponentText(entry?.Category??"","caption");Localization.SetIsUserText(label,true);Localization.SetIsUserText(category,true);row.Children.Add(label);row.Children.Add(category);return row;
        });
        Grid.SetRow(_list, 1); left.Children.Add(_list);
        var empty = new AvaMedia.Controls.EmptyState { Text = Localization.Text("暂无词条"), IsVisible = _entries.Count == 0 };
        Grid.SetRow(empty, 1); left.Children.Add(empty);
        _entries.CollectionChanged += (_, _) => empty.IsVisible = _entries.Count == 0;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
        actions.Children.Add(Ui.Button("添加词条", () =>
        {
            var suffix = 1; while (_entries.Any(entry => entry.Label == "新词条 " + suffix)) suffix++;
            var entry = new WordCandidate("新词条 " + suffix, "自定义", "新词条", []); _entries.Add(entry); _list.SelectedItem = entry;
        }));
        actions.Children.Add(Ui.Button("删除词条", () => { _editingIndex = -1; if (_list.SelectedItem is WordCandidate entry) _entries.Remove(entry); if (_entries.Count > 0) _list.SelectedIndex = 0; else { _label.Text = _category.Text = _description.Text = ""; _selectedTags.Clear(); RefreshTags(); } }));
        Grid.SetRow(actions, 2); left.Children.Add(actions); body.Children.Add(left);
        var fields = new StackPanel { IsEnabled = _entries.Count > 0, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
        foreach (var (caption, input) in new[] { ("中文名称", _label), ("类别", _category), ("语义描述", _description) })
        { Localization.SetIsUserText(input, true); fields.Children.Add(Ui.ComponentText(caption)); fields.Children.Add(input); }
        var categories = new WrapPanel();
        foreach (var value in _entries.Select(entry => entry.Category).Distinct().Take(12))
        { var button = Ui.Button(value, () => _category.Text = value); button.Margin = new(0,0,4,4); categories.Children.Add(button); }
        fields.Children.Add(categories);
        fields.Children.Add(Ui.ComponentText("识别标签")); fields.Children.Add(_tags);
        var choices = new ComboBox { Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160, MaxWidth = 280 };
        choices.ItemTemplate = new FuncDataTemplate<string>((tag, _) => Ui.ComponentText(tag is null ? "" : WordLibraryCatalog.TagLabel(tag)));
        choices.ItemsSource = WordLibraryCatalog.JoyTags.Order().ToArray(); fields.Children.Add(choices);
        fields.Children.Add(Ui.Button("添加识别标签", () =>
        {
            if (choices.SelectedItem is not string tag || _selectedTags.Count >= 32) return;
            _selectedTags.Add(tag); RefreshTags(); SaveEntry();
        }));
        fields.Children.Add(Ui.Button("保存词条", () => SaveEntry()));
        Grid.SetColumn(fields,1); body.Children.Add(new ScrollViewer { Content = fields, [Grid.ColumnProperty] = 1 });
        Grid.SetRow(body,1); root.Children.Add(body); Grid.SetRow(_error,2); root.Children.Add(_error);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
        footer.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        footer.Children.Add(Ui.DialogButton("保存词库", () =>
        {
            try
            {
                if (!SaveEntry()) return; WordLibraryCatalog.Validate(_entries);
                Close(new WordLibrary(library is { BuiltIn: false } ? library.Id : Guid.NewGuid().ToString("N"), _name.Text?.Trim() ?? "", library?.Source ?? "自定义 · 本地", _entries.ToArray()));
            }
            catch (Exception error) { _error.Text = error.Message; }
        }, isDefault: true));
        Grid.SetRow(footer,3); root.Children.Add(footer); Content = root;
        _list.SelectionChanged += (_, _) =>
        {
            fields.IsEnabled = _list.SelectedItem is WordCandidate;
            if (_loading) return; var index = _list.SelectedIndex; SaveEntry(false); _editingIndex = index; LoadEntry();
        }; _list.SelectedIndex = 0;
    }

    private void LoadEntry()
    {
        if (_list.SelectedItem is not WordCandidate entry) return;
        _loading = true; _label.Text = entry.Label; _category.Text = entry.Category; _description.Text = entry.Description;
        _selectedTags.Clear(); _selectedTags.UnionWith(entry.Tags); RefreshTags(); _loading = false;
    }

    private void RefreshTags()
    {
        _tags.Children.Clear();
        foreach (var tag in _selectedTags.Order())
        {
            var button = Ui.Button(WordLibraryCatalog.TagLabel(tag) + " ×", () => { _selectedTags.Remove(tag); RefreshTags(); SaveEntry(); });
            button.Margin = new(0,0,4,4); _tags.Children.Add(button);
        }
    }

    private bool SaveEntry(bool validate = true)
    {
        if (_editingIndex < 0 || _editingIndex >= _entries.Count) return true;
        try
        {
            var entry = new WordCandidate(_label.Text?.Trim() ?? "", _category.Text?.Trim() ?? "", _description.Text?.Trim() ?? "", _selectedTags.ToArray());
            var index = _editingIndex;
            if (validate) WordLibraryCatalog.Validate(_entries.Select((value, i) => i == index ? entry : value).ToArray());
            _loading = true; var selected = _list.SelectedIndex; _entries[index] = entry; _list.SelectedIndex = selected; _loading = false; _error.Text = ""; return true;
        }
        catch (Exception error) { if (validate) _error.Text = error.Message; return false; }
    }
}
