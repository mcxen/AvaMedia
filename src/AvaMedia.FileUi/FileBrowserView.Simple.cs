using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace AvaMedia.FileUi;

public sealed partial class FileBrowserView
{
    private readonly TextBox _plainAddress = new();

    // Restore the original address + details-list interaction (1156f5e), with flat places.
    private void BuildSimple(IFileBrowserGlyphs? glyphs)
    {
        _glyphs = glyphs;
        FileBrowserTheme.Bind(this, BackgroundProperty, "Surface");
        FileBrowserTheme.Bind(this, ForegroundProperty, "Text");
        var root = _mediaRoot = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var navigation = _navigationBar = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 6, Margin = new(12, 8) };
        _back = IconButton("chevron-left", "后退", async () => { if (_history.TryPop(out var folder)) await NavigateAsync(folder, false); });
        _up = IconButton("chevron-up", "上一级", async () => { if (!_recent && _directory?.Parent is { } parent) await NavigateAsync(parent); });
        navigation.Children.Add(_back); Grid.SetColumn(_up, 1); navigation.Children.Add(_up);
        AutomationProperties.SetName(_plainAddress, _text("文件夹地址"));
        _plainAddress.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true; _address.Text = _plainAddress.Text; await OpenAddressAsync();
            if (!_disposed) _plainAddress.Text = _recent ? _text("最近使用") : _address.Text;
        };
        Grid.SetColumn(_plainAddress, 2); navigation.Children.Add(_plainAddress);
        var refresh = IconButton("refresh-cw", "刷新", async () =>
        { if (_recent) await ShowRecentAsync(); else if (CurrentFolder is { } folder) await NavigateAsync(folder, false); });
        Grid.SetColumn(refresh, 3); navigation.Children.Add(refresh); root.Children.Add(navigation);

        var body = new Grid { ColumnDefinitions = new("Auto,*") };
        _placesColumn.Width = 160; _placesColumn.Padding = new(8);
        _placesColumn.BorderThickness = new(0, 0, 1, 0); _placesColumn.BorderBrush = Hair;
        FileBrowserTheme.Bind(_placesColumn, Border.BackgroundProperty, "Panel");
        var places = new StackPanel { Spacing = 6 };
        var title = Label(_text("常用位置")); title.Margin = new(6, 2, 0, 4);
        FileBrowserTheme.Bind(title, TextBlock.FontSizeProperty, "CaptionFont");
        FileBrowserTheme.Bind(title, TextBlock.ForegroundProperty, "Muted");
        places.Children.Add(title); places.Children.Add(_compactPlaces);
        _placesColumn.Child = new ScrollViewer { Content = places, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        body.Children.Add(_placesColumn);
        var files = new Grid { RowDefinitions = new("Auto,*") };
        var header = SimpleRow(null, _text("名称"), _text("大小"), _text("修改时间"));
        FileBrowserTheme.Bind(header, HeightProperty, "HeaderHeight");
        FileBrowserTheme.Bind(header, TextElement.ForegroundProperty, "Muted");
        files.Children.Add(new Border { Child = header, BorderBrush = Hair, BorderThickness = new(0, 0, 0, 1) });
        _entries.SelectionMode = _options.Multiple ? Avalonia.Controls.SelectionMode.Multiple : Avalonia.Controls.SelectionMode.Single;
        _entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MarginProperty, new Thickness(0)) } });
        _entries.ItemTemplate = new FuncDataTemplate<FileBrowserEntry>((entry, _) => entry is null ? new Border()
            : SimpleRow(_glyphs?.Entry(entry.Item), entry.Name, entry.IsFolder ? _text("文件夹") : Size(entry.Size),
                entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—"));
        _entries.SelectionChanged += (_, _) => UpdateSelection();
        _entries.DoubleTapped += async (_, args) =>
        {
            if (args.Source is not Control control || !control.GetVisualAncestors().Prepend(control).OfType<ListBoxItem>().Any()) return;
            args.Handled = true; await OpenSelectionAsync();
        };
        _entries.KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Enter) { args.Handled = true; await OpenSelectionAsync(); }
            else if (_options.Multiple && args.Key == Key.A && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            { args.Handled = true; _entries.SelectAll(); }
        };
        AutomationProperties.SetName(_entries, _text("文件列表"));
        Grid.SetRow(_entries, 1); files.Children.Add(_entries);
        Grid.SetRow(_empty, 1); files.Children.Add(_empty);
        Grid.SetColumn(files, 1); body.Children.Add(files); Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Grid { RowDefinitions = new("Auto,Auto"), RowSpacing = 6, Margin = new(12, 8) };
        var feedback = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        feedback.Children.Add(_status); Grid.SetColumn(_selectionCount, 1); feedback.Children.Add(_selectionCount); footer.Children.Add(feedback);
        var actions = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        actions.Children.Add(_hidden); _types.IsVisible = !_options.Folders;
        Grid.SetColumn(_types, 1); actions.Children.Add(_types);
        var external = Button("系统选择…", async () =>
        {
            if (PickExternalAsync is not { } pick) return;
            var selected = await pick(); if (!_disposed && selected.Count > 0) Selected?.Invoke(selected);
        });
        Grid.SetColumn(external, 2); actions.Children.Add(external);
        var cancel = Button("取消", () => { Cancelled?.Invoke(); return Task.CompletedTask; }); Grid.SetColumn(cancel, 3); actions.Children.Add(cancel);
        _select = Button(_options.Folders ? "选择文件夹" : "打开", () => { Confirm(); return Task.CompletedTask; }); _select.Classes.Add("primary");
        Grid.SetColumn(_select, 4); actions.Children.Add(_select); Grid.SetRow(actions, 1); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        AutomationProperties.SetName(_types, _text("文件类型"));
    }

    private static Grid SimpleRow(Control? glyph, string name, string size, string modified)
    {
        var row = new Grid { ColumnDefinitions = new("22,*,80,132"), ColumnSpacing = 8, Margin = new(8, 0) };
        FileBrowserTheme.Bind(row, HeightProperty, "TextRowHeight");
        if (glyph is not null) { glyph.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(glyph); }
        var label = Label(name); label.TextTrimming = TextTrimming.CharacterEllipsis; label.Classes.Add("raw"); ToolTip.SetTip(label, name);
        Grid.SetColumn(label, 1); row.Children.Add(label);
        var bytes = Label(size); bytes.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(bytes, 2); row.Children.Add(bytes);
        var date = Label(modified); Grid.SetColumn(date, 3); row.Children.Add(date);
        return row;
    }
}
