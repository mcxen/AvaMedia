using System.IO.Enumeration;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace AvaMedia.FileUi;

/// <summary>Embeddable file browser, independent of Window, native engines and local paths.</summary>
public sealed partial class FileBrowserView : UserControl, IDisposable
{
    private readonly IFileBrowserSource _source;
    private readonly FileBrowserOptions _options;
    private readonly Func<string, string> _text;
    private readonly FileTreeView _places;
    private readonly ListBox _entries = new();
    private readonly PathInput _address = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selectionCount = new();
    private readonly ComboBox _types = new() { MinWidth = 140, MaxWidth = 300 };
    private readonly CheckBox _hidden = new();
    private Button _back = null!, _up = null!, _select = null!;
    private readonly Stack<FileBrowserItem> _history = new();
    private CancellationTokenSource? _navigation;
    private FileBrowserDirectory? _directory;
    private bool _disposed, _loading;
    public event Action<IReadOnlyList<FileBrowserItem>>? Selected;
    public event Action? Cancelled;
    public Func<Task<IReadOnlyList<FileBrowserItem>>>? PickExternalAsync { get; set; }
    public FileBrowserItem? CurrentFolder => _directory?.Folder;

    public FileBrowserView(IFileBrowserSource source, FileBrowserOptions options, Func<string, string>? text = null, IFileBrowserGlyphs? glyphs = null)
    {
        _source = source; _options = options; _places = new(source) { Width = 210 }; _text = text ?? (value => value);
        FileBrowserTheme.Install(this);
        FileBrowserTheme.Bind(this, FontSizeProperty, "BodyFont");
        _entries.SelectionMode = options.Multiple ? SelectionMode.Multiple : SelectionMode.Single;
        _entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        if (options.Compact) { BuildCompact(glyphs); return; }
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new(12) };
        var navigation = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 6 };
        _back = Button("后退", async () => { if (_history.TryPop(out var folder)) await NavigateAsync(folder, false); });
        _up = Button("上一级", async () => { if (_directory?.Parent is { } parent) await NavigateAsync(parent); });
        navigation.Children.Add(_back); Grid.SetColumn(_up, 1); navigation.Children.Add(_up);
        Grid.SetColumn(_address, 2); navigation.Children.Add(_address);
        var refresh = Button("刷新", async () => { if (CurrentFolder is { } folder) await NavigateAsync(folder, false); });
        Grid.SetColumn(refresh, 3); navigation.Children.Add(refresh); root.Children.Add(navigation);
        _address.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await OpenAddressAsync(); } };
        _hidden.Content = _text("显示隐藏文件"); _hidden.IsCheckedChanged += (_, _) => Filter();
        var filter = new Grid();
        filter.Children.Add(_hidden); Grid.SetRow(filter, 1); root.Children.Add(filter);
        var body = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        _places.FolderSelected += async folder => await NavigateAsync(folder);
        body.Children.Add(_places);
        var files = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 2 };
        var header = Row(_text("名称"), _text("大小"), _text("修改时间")); header.Classes.Add("table-header"); files.Children.Add(header);
        _entries.ItemTemplate = new FuncDataTemplate<FileBrowserEntry>((entry, _) => entry is null ? new Border() :
            Row((entry.IsFolder ? "▸ " : "") + entry.Name, entry.IsFolder ? _text("文件夹") : Size(entry.Size), entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? ""));
        _entries.DoubleTapped += async (_, args) =>
        {
            if (args.Source is not Control control || !control.GetVisualAncestors().Prepend(control).OfType<ListBoxItem>().Any()) return;
            args.Handled = true; await OpenSelectionAsync();
        };
        _entries.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await OpenSelectionAsync(); } };
        _entries.SelectionChanged += (_, _) => UpdateSelection();
        ScrollViewer.SetHorizontalScrollBarVisibility(_entries, ScrollBarVisibility.Disabled);
        Grid.SetRow(_entries, 1); files.Children.Add(_entries); Grid.SetColumn(files, 1); body.Children.Add(files); Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = new Grid { RowDefinitions = new("Auto,Auto"), RowSpacing = 6 };
        var feedback = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        feedback.Children.Add(_status); Grid.SetColumn(_selectionCount, 1); feedback.Children.Add(_selectionCount); footer.Children.Add(feedback);
        var actions = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var types = options.FileTypes is { Count: > 0 } ? options.FileTypes : [FilePickerFileTypes.All];
        _types.ItemsSource = types; _types.SelectedItem = options.SelectedFileType ?? types[0]; _types.IsVisible = !options.Folders;
        _types.ItemTemplate = new FuncDataTemplate<FilePickerFileType>((type, _) => Label(_text(type?.Name ?? "")));
        _types.SelectionChanged += (_, _) => Filter(); actions.Children.Add(_types);
        var external = Button("系统选择…", async () =>
        {
            if (PickExternalAsync is not { } pick) return;
            var selected = await pick(); if (!_disposed && selected.Count > 0) Selected?.Invoke(selected);
        }); external.IsVisible = !options.Embedded; Grid.SetColumn(external, 1); actions.Children.Add(external);
        var cancel = Button("取消", () => { Cancelled?.Invoke(); return Task.CompletedTask; }); cancel.IsVisible = !options.Embedded; Grid.SetColumn(cancel, 2); actions.Children.Add(cancel);
        _select = Button(options.Embedded ? "添加所选项" : options.Folders ? "选择文件夹" : "打开", () => { Confirm(); return Task.CompletedTask; }); _select.Classes.Add("primary");
        Grid.SetColumn(_select, 3); actions.Children.Add(_select); Grid.SetRow(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        PropertyChanged += (_, args) => { if (args.Property == BoundsProperty) _places.Width = Bounds.Width < 650 ? 150 : 210; };
        AutomationProperties.SetName(_address, _text("文件夹地址"));
        AutomationProperties.SetName(_places, _text("常用位置")); AutomationProperties.SetName(_entries, _text("文件列表")); AutomationProperties.SetName(_types, _text("文件类型"));
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Cancelled?.Invoke(); }
            else if (args.Key == Key.L && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            { args.Handled = true; _address.Focus(); _address.SelectAll(); }
            else if (args.Key == Key.Up && args.KeyModifiers == KeyModifiers.Alt && _directory?.Parent is { } parent)
            { args.Handled = true; await NavigateAsync(parent); }
        };
        UpdateSelection();
    }

    public async Task InitializeAsync()
    {
        try
        {
            var locations = await _source.GetLocationsAsync(CancellationToken.None);
            if (_disposed) return;
            _places.SetLocations(locations); SetCompactLocations(locations);
            if ((_options.StartFolder ?? locations.FirstOrDefault()?.Folder) is { } folder) await NavigateAsync(folder, false);
        }
        catch (Exception error) { ShowError(error); }
    }

    public async Task NavigateAsync(FileBrowserItem folder, bool remember = true)
    {
        _navigation?.Cancel(); _navigation?.Dispose();
        var work = _navigation = new(); var token = work.Token;
        _loading = true; _entries.IsEnabled = false; UpdateSelection(); _status.Text = _text("正在读取…");
        try
        {
            var directory = await _source.ReadAsync(folder, token);
            if (_disposed || token.IsCancellationRequested) return;
            if (remember && _directory is { } previous && previous.Folder.Address != folder.Address) _history.Push(previous.Folder);
            _directory = directory; _address.Text = _source.DisplayAddress(folder);
            _loading = false; Filter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) { RestoreAddress(); ShowError(error); }
        }
        finally
        {
            if (!_disposed && ReferenceEquals(_navigation, work)) { _loading = false; _entries.IsEnabled = true; UpdateSelection(); }
        }
    }

    private async Task OpenAddressAsync()
    {
        try
        {
            var item = await _source.ResolveAsync(_address.Text?.Trim() ?? "", CancellationToken.None);
            if (_disposed) return;
            if (item is { IsFolder: true } folder) await NavigateAsync(folder);
            else if (!_options.Folders && item is { IsFolder: false } file && Matches(file.Name)) Selected?.Invoke([file]);
            else { RestoreAddress(); _status.Text = _text("找不到可选择的文件或文件夹。"); }
        }
        catch (Exception error) { RestoreAddress(); ShowError(error); }
    }

    private void RestoreAddress() => _address.Text = _directory is { } directory ? _source.DisplayAddress(directory.Folder) : "";

    private async Task OpenSelectionAsync()
    {
        if (_loading) return;
        if (_entries.SelectedItem is FileBrowserEntry { Item: { IsFolder: true } folder }) await NavigateAsync(folder);
        else Confirm();
    }

    private void Filter()
    {
        if (_disposed || _directory is null || _loading) return;
        _entries.ItemsSource = _directory.Entries.Where(entry => (_hidden.IsChecked == true || !entry.Hidden)
            && (entry.IsFolder || !_options.Folders && Matches(entry.Name)))
            .OrderByDescending(entry => entry.IsFolder).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _status.Text = ""; UpdateSelection(); CompactDirectoryChanged();
    }

    private bool Matches(string name) => _types.SelectedItem is not FilePickerFileType { Patterns: { Count: > 0 } patterns }
        || patterns.Any(pattern => pattern is "*" or "*.*" || FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    private FileBrowserItem[] Selection() => _entries.SelectedItems?.OfType<FileBrowserEntry>()
        .Where(entry => _options.AllowFolders || entry.IsFolder == _options.Folders).Select(entry => entry.Item).ToArray() ?? [];

    private void UpdateSelection()
    {
        _back.IsEnabled = !_loading && _history.Count > 0; _up.IsEnabled = !_loading && _directory?.Parent is not null;
        _select.IsEnabled = !_loading && (Selection().Length > 0 || _options.Folders && CurrentFolder is not null);
        var count = Selection().Length;
        _selectionCount.Text = count == 0 ? "" : count + " " + _text("项已选择");
        if (_options.Compact) UpdateCompactSelection(count);
    }

    private void Confirm()
    {
        if (_disposed || !_select.IsEnabled) return;
        var selected = Selection();
        if (selected.Length == 0 && _options.Folders && CurrentFolder is { } folder) selected = [folder];
        if (selected.Length > 0) Selected?.Invoke(_options.Multiple ? selected : selected.Take(1).ToArray());
    }

    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = _text(label) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception error) { ShowError(error); } };
        return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private static Grid Row(string name, string size, string modified)
    {
        var row = new Grid { ColumnDefinitions = new("*,88,148"), ColumnSpacing = 8, Margin = new(6, 4) };
        var title = Label(name); ToolTip.SetTip(title, name); row.Children.Add(title);
        var length = Label(size); Grid.SetColumn(length, 1); row.Children.Add(length);
        var date = Label(modified); Grid.SetColumn(date, 2); row.Children.Add(date); return row;
    }
    private static string Size(ulong? size) => size is not { } bytes ? "" : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : bytes < 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{bytes / (1024d * 1024 * 1024):0.#} GB";
    private void ShowError(Exception error) { if (!_disposed) _status.Text = error.Message; }
    public void Dispose() { _disposed = true; _navigation?.Cancel(); _navigation?.Dispose(); _places.Dispose(); }
}
