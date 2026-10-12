using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AvaMedia.FileUi;

public sealed partial class FileBrowserView
{
    private sealed record MediaRow(FileBrowserEntry[] Entries);
    private readonly HashSet<FileBrowserEntry> _chosen = [];
    private readonly HashSet<FileBrowserCell> _cells = [];
    private FileBrowserEntry[] _visibleEntries = [];
    private Dictionary<FileBrowserEntry, int> _entryIndexes = [];
    private FileBrowserEntry? _focusEntry;
    private int _anchor, _columns = 4;
    private bool _gridMode = true, _recent, _visibleQueued;
    private FileBrowserKind _kind;
    private Dictionary<FileBrowserKind, int> _kindCounts = [];
    private FileBrowserHistory _recentHistory = new([], [], []);
    private readonly StackPanel _sidebar = new() { Spacing = 2 };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly StackPanel _recentFolders = new() { Spacing = 8, Margin = new(16, 8) };
    private readonly StackPanel _inspector = new() { Spacing = 4, Margin = new(16) };
    private readonly Border _inspectorHost = new();
    private readonly Border _listHeader = new();
    private Border? _footerHost;
    private Grid? _mediaRoot, _navigationBar;
    private Button _listMode = null!, _largeMode = null!;
    private VisibleMediaWork<FileBrowserEntry, FileBrowserMedia?>? _mediaWork;
    private FileBrowserCell? _inspectorCell;
    private readonly Dictionary<string, TextBlock> _infoValues = [];

    private void BuildMediaPicker(IFileBrowserGlyphs? glyphs)
    {
        _glyphs = glyphs;
        FileBrowserTheme.Bind(this, BackgroundProperty, "Surface");
        FileBrowserTheme.Bind(this, ForegroundProperty, "Text");
        var root = _mediaRoot = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        var navigation = _navigationBar = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 8, Margin = new(16, 6) };
        _back = IconButton("chevron-left", "后退", async () => { if (_history.TryPop(out var folder)) await NavigateAsync(folder, false); });
        _up = IconButton("chevron-up", "上一级", async () => { if (_directory?.Parent is { } parent) await NavigateAsync(parent); });
        navigation.Children.Add(_back); Grid.SetColumn(_up, 1); navigation.Children.Add(_up);
        var path = new Grid(); path.Children.Add(new ScrollViewer { Content = _crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _address.IsVisible = false; path.Children.Add(_address); Grid.SetColumn(path, 2); navigation.Children.Add(path);
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _listMode = IconButton("list", "列表视图", () => SetGridModeAsync(false));
        _largeMode = IconButton("layout-grid", "大图视图", () => SetGridModeAsync(true));
        mode.Children.Add(_listMode); mode.Children.Add(_largeMode);
        var modeHost = new Border { Padding = new(2), Child = mode }; FileBrowserTheme.Bind(modeHost, Border.BackgroundProperty, "Fill"); FileBrowserTheme.Bind(modeHost, Border.CornerRadiusProperty, "Radius");
        Grid.SetColumn(modeHost, 3); navigation.Children.Add(modeHost); root.Children.Add(navigation);
        var filter = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(16, 8) };
        var tabHost = new Border { Padding = new(2), HorizontalAlignment = HorizontalAlignment.Left, Child = _tabs };
        FileBrowserTheme.Bind(tabHost, Border.BackgroundProperty, "Fill"); FileBrowserTheme.Bind(tabHost, Border.CornerRadiusProperty, "Radius");
        filter.Children.Add(new ScrollViewer { Content = tabHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var menu = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _types.IsVisible = _options.FileTypes is { Count: > 1 } && !_options.Folders;
        menu.Children.Add(_types);
        var more = IconButton("ellipsis", "更多选项", () => Task.CompletedTask);
        var refresh = new MenuItem { Header = _text("刷新") };
        refresh.Click += async (_, _) => { if (_recent) { await RefreshHistoryAsync(); await ShowRecentAsync(); } else if (CurrentFolder is { } folder) await NavigateAsync(folder, false); };
        var hidden = new MenuItem { Header = _text("显示隐藏文件"), ToggleType = MenuItemToggleType.CheckBox };
        hidden.Click += (_, _) => _hidden.IsChecked = hidden.IsChecked;
        var editPath = new MenuItem { Header = _text("输入路径…") }; editPath.Click += (_, _) => { _address.IsVisible = true; _address.Focus(); };
        more.ContextMenu = new ContextMenu { ItemsSource = new[] { refresh, hidden, editPath } };
        more.Click += (_, _) => more.ContextMenu.Open(more);
        menu.Children.Add(more); Grid.SetColumn(menu, 1); filter.Children.Add(menu);
        Grid.SetRow(filter, 1); root.Children.Add(filter);

        var body = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        _placesColumn.Bind(WidthProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbSidebarWidth"));
        _placesColumn.Padding = new(10, 12); _placesColumn.Child = new ScrollViewer { Content = _sidebar, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        FileBrowserTheme.Bind(_placesColumn, Border.BackgroundProperty, "Sidebar");
        body.Children.Add(_placesColumn);
        var files = new Grid { RowDefinitions = new("Auto,Auto,Auto,*") };
        files.Children.Add(_recentFolders);
        _status.FontSize = 12; _status.Margin = new(18, 10, 18, 6); FileBrowserTheme.Bind(_status, TextBlock.ForegroundProperty, "Muted");
        Grid.SetRow(_status, 1); files.Children.Add(_status);
        _listHeader.Child = ListHeader(); _listHeader.IsVisible = false;
        Grid.SetRow(_listHeader, 2); files.Children.Add(_listHeader);
        _entries.Padding = new(10, 6); _entries.BorderThickness = new(0); _entries.SelectionMode = SelectionMode.Single;
        FileBrowserTheme.Bind(_entries, BackgroundProperty, "Surface");
        _entries.ItemTemplate = new FuncDataTemplate<MediaRow>((row, _) => row is null ? null : RenderMediaRow(row));
        _entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
        {
            new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MarginProperty, new Thickness(0)),
            new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent), new Setter(ListBoxItem.FocusableProperty, false)
        } });
        // Selection and keyboard focus belong to individual cells, not the virtualized row container.
        _entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>().Template().OfType<ContentPresenter>())
            { Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) } });
        _entries.KeyDown += MediaKeyDown;
        _entries.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => QueueVisibleMedia(), RoutingStrategies.Bubble);
        _entries.LayoutUpdated += (_, _) => QueueVisibleMedia();
        _entries.SizeChanged += (_, _) =>
        {
            var columns = Math.Max(1, (int)Math.Floor((_entries.Bounds.Width - 20) / 170));
            if (_gridMode && columns != _columns) { _columns = columns; RebuildRows(); }
        };
        Grid.SetRow(_entries, 3); files.Children.Add(_entries); Grid.SetColumn(files, 1); body.Children.Add(files);
        FileBrowserTheme.Bind(_inspectorHost, WidthProperty, "InspectorWidth");
        FileBrowserTheme.Bind(_inspectorHost, Border.BackgroundProperty, "Panel"); FileBrowserTheme.Bind(_inspectorHost, Border.BorderBrushProperty, "Line");
        _inspectorHost.BorderThickness = new(1, 0, 0, 0); _inspectorHost.Child = new ScrollViewer { Content = _inspector, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_inspectorHost, 2); body.Children.Add(_inspectorHost); Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 10, Margin = new(18, 12) };
        _selectionCount.FontSize = 12.5; _selectionCount.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_selectionCount);
        var external = Button("系统选择…", async () =>
        {
            if (PickExternalAsync is { } pick && await pick() is { Count: > 0 } selected && !_disposed) Selected?.Invoke(selected);
        }); external.IsVisible = !_options.Embedded; Grid.SetColumn(external, 1); footer.Children.Add(external);
        var cancel = Button("取消", () => { Cancelled?.Invoke(); return Task.CompletedTask; }); cancel.IsVisible = !_options.Embedded;
        Grid.SetColumn(cancel, 2); footer.Children.Add(cancel);
        _select = Button("打开 0 个文件", () => { Confirm(); return Task.CompletedTask; }); _select.Classes.Add("primary");
        foreach (var button in new[] { external, cancel, _select }) { button.Classes.Add("dialog-action"); button.MinWidth = 72; FileBrowserTheme.Bind(button, Avalonia.Controls.Button.CornerRadiusProperty, "ActionRadius"); }
        Grid.SetColumn(_select, 3); footer.Children.Add(_select);
        var footerHost = _footerHost = new Border { Child = footer, BorderThickness = new(0, 1, 0, 0) };
        FileBrowserTheme.Bind(footerHost, Border.BackgroundProperty, "Panel"); FileBrowserTheme.Bind(footerHost, Border.BorderBrushProperty, "Line");
        Grid.SetRow(footerHost, 3); root.Children.Add(footerHost);
        Content = root;
        AutomationProperties.SetName(_entries, _text("文件列表")); AutomationProperties.SetName(_crumbs, _text("分级路径导航"));
        _mediaWork = new((entry, token) => Task.Run(() => _source.GetMediaAsync(entry, token), token),
            (entry, media) => Dispatcher.UIThread.Post(() =>
            {
                if (_mediaWork?.TryGetResult(entry, out var current) == true && ReferenceEquals(current, media)) ApplyMedia(entry, media);
            }, DispatcherPriority.Background));
        SizeChanged += (_, _) => UpdateResponsivePanels();
        UpdateTabs(); UpdateInspector(null);
    }

    /// <summary>Desktop hosts can place this toolbar inside their shared window chrome.</summary>
    public Control? ExtractNavigationBar()
    {
        if (_navigationBar is null || _mediaRoot is null || !_mediaRoot.Children.Remove(_navigationBar)) return null;
        FileBrowserTheme.Install(_navigationBar);
        _navigationBar.Margin = new(8, 0); return _navigationBar;
    }
    public bool GridMode => _gridMode;
    public async Task SetGridModeAsync(bool grid)
    {
        _mediaWork?.Update([]); _gridMode = grid; UpdateModeButtons(); RebuildRows();
        await _source.SetGridModeAsync(grid, _lifetime.Token);
    }
    public async Task SetMediaKindAsync(FileBrowserKind kind) { _kind = kind; await FilterAsyncCore(); }
    public void SelectAddresses(IEnumerable<string> addresses)
    {
        var wanted = addresses.ToHashSet(StringComparer.Ordinal);
        _chosen.Clear(); _focusEntry = null; foreach (var entry in _visibleEntries.Where(entry => wanted.Contains(entry.Item.Address) && CanSelect(entry)))
        { _chosen.Add(entry); _focusEntry = entry; if (!_options.Multiple) break; }
        _anchor = _focusEntry is null ? 0 : _entryIndexes[_focusEntry];
        UpdateSelection(); UpdateInspector(_focusEntry); QueueVisibleMedia();
    }
    private void UpdateModeButtons()
    {
        FileBrowserTheme.Bind(_listMode, Avalonia.Controls.Button.BackgroundProperty, _gridMode ? "Fill" : "Surface");
        FileBrowserTheme.Bind(_largeMode, Avalonia.Controls.Button.BackgroundProperty, _gridMode ? "Surface" : "Fill");
        _listHeader.IsVisible = !_gridMode;
        UpdateResponsivePanels();
    }
    private void UpdateResponsivePanels()
    {
        if (_options.Compact) { _placesColumn.IsVisible = _showPlaces && Bounds.Width >= 380; return; }
        _inspectorHost.IsVisible = Bounds.Width >= (_gridMode ? 850 : 1200);
        _placesColumn.IsVisible = _showPlaces && Bounds.Width >= (_gridMode ? 620 : 1000);
        QueueVisibleMedia();
    }
    private void RebuildRows()
    {
        if (_options.Compact || _disposed) return;
        _mediaWork?.Update([]);
        var columns = _gridMode ? _columns : 1;
        var rows = new MediaRow[(_visibleEntries.Length + columns - 1) / columns];
        for (var index = 0; index < rows.Length; index++)
        {
            var offset = index * columns;
            var entries = new FileBrowserEntry[Math.Min(columns, _visibleEntries.Length - offset)];
            Array.Copy(_visibleEntries, offset, entries, 0, entries.Length); rows[index] = new(entries);
        }
        _entries.ItemsSource = rows;
        QueueVisibleMedia();
    }
    private Control RenderMediaRow(MediaRow row)
    {
        var grid = new Grid { ColumnDefinitions = new(string.Join(',', Enumerable.Repeat("*", _gridMode ? _columns : 1))), ColumnSpacing = _gridMode ? 12 : 0 };
        for (var index = 0; index < row.Entries.Length; index++)
        {
            var cell = new FileBrowserCell(row.Entries[index], _gridMode, _glyphs, _text);
            cell.UpdateSelection(_chosen.Contains(cell.Entry), Equals(cell.Entry, _focusEntry));
            cell.AttachedToVisualTree += (_, _) => { _cells.Add(cell); QueueVisibleMedia(); };
            cell.DetachedFromVisualTree += (_, _) => { _cells.Remove(cell); cell.ReleaseImage(); QueueVisibleMedia(); };
            cell.PointerPressed += (_, args) =>
            {
                if (!args.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
                args.Handled = true; _entries.Focus(); SelectEntry(cell.Entry, args.KeyModifiers, args.ClickCount == 1);
            };
            cell.PointerEntered += (_, args) =>
            {
                if (args.GetCurrentPoint(cell).Properties.IsLeftButtonPressed && _options.Multiple)
                    SelectEntry(cell.Entry, KeyModifiers.Shift, false);
            };
            cell.DoubleTapped += async (_, args) => { args.Handled = true; try { await OpenSelectionAsync(); } catch (Exception error) { ShowError(error); } };
            Grid.SetColumn(cell, index); grid.Children.Add(cell);
        }
        return grid;
    }
    private void SelectEntry(FileBrowserEntry entry, KeyModifiers modifiers, bool toggle)
    {
        if (!_entryIndexes.TryGetValue(entry, out var index)) return;
        var range = _options.Multiple && modifiers.HasFlag(KeyModifiers.Shift);
        if (range)
        {
            if ((modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) _chosen.Clear();
            for (var i = Math.Min(index, _anchor); i <= Math.Max(index, _anchor) && i < _visibleEntries.Length; i++)
                if (CanSelect(_visibleEntries[i])) _chosen.Add(_visibleEntries[i]);
        }
        else
        {
            _anchor = index;
            if (!_options.Multiple || !toggle && (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) _chosen.Clear();
            if (CanSelect(entry)) { if (!toggle || !_chosen.Remove(entry)) _chosen.Add(entry); }
        }
        _focusEntry = entry; UpdateSelection(); UpdateInspector(entry); QueueVisibleMedia();
    }
    private async void MediaKeyDown(object? sender, KeyEventArgs args)
    {
        try
        {
            if (args.Key == Key.Enter) { args.Handled = true; await OpenSelectionAsync(); return; }
            if (args.Key == Key.Space && _focusEntry is { } focus) { args.Handled = true; SelectEntry(focus, args.KeyModifiers, true); return; }
            if (args.Key == Key.A && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && _options.Multiple)
            { args.Handled = true; _chosen.UnionWith(_visibleEntries.Where(CanSelect)); UpdateSelection(); return; }
            var step = args.Key switch { Key.Down => _gridMode ? _columns : 1, Key.Up => _gridMode ? -_columns : -1, Key.Left when _gridMode => -1, Key.Right when _gridMode => 1, _ => 0 };
            if (step == 0 && args.Key is not (Key.Home or Key.End)) return;
            args.Handled = true; if (_visibleEntries.Length == 0) return;
            var current = _focusEntry is null ? (step < 0 ? _visibleEntries.Length : -step) : _entryIndexes.GetValueOrDefault(_focusEntry, -1);
            var next = args.Key == Key.Home ? 0 : args.Key == Key.End ? _visibleEntries.Length - 1 : Math.Clamp(current + step, 0, _visibleEntries.Length - 1);
            SelectEntry(_visibleEntries[next], args.KeyModifiers, false);
            _entries.ScrollIntoView(next / (_gridMode ? _columns : 1));
        }
        catch (Exception error) { ShowError(error); }
    }
    private static string ListColumns => "26,50,*,72,106,78,128";
    private Control ListHeader()
    {
        var grid = new Grid { ColumnDefinitions = new(ListColumns), ColumnSpacing = 8, Margin = new(18, 0), Height = 28 };
        var captions = new[] { "", "", "名称", "时长", "分辨率", "大小", "修改时间" };
        for (var i = 2; i < captions.Length; i++)
        {
            var label = Label(_text(captions[i])); label.FontSize = 11.5; FileBrowserTheme.Bind(label, TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(label, i); grid.Children.Add(label);
        }
        return grid;
    }

    private async Task RefreshHistoryAsync()
    {
        _recentHistory = await _source.GetHistoryAsync(_lifetime.Token);
        if (!_disposed) RenderSidebar();
    }
    public async Task ShowRecentAsync() => await ShowHistoryAsync(false);
    public async Task ShowRecentFoldersAsync() => await ShowHistoryAsync(true);
    private async Task ShowHistoryAsync(bool folders)
    {
        var (work, token) = BeginNavigation();
        try
        {
            var history = await _source.GetHistoryAsync(token);
            // Source supplies only filesystem metadata here, never media information.
            var directory = await _source.ReadRecentAsync(folders, token);
            if (_disposed || token.IsCancellationRequested) return;
            _recentHistory = history; _recent = true; _directory = directory; _loading = false;
            await FilterAsyncCore();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) ShowError(error); }
        finally { EndNavigation(work); }
    }
    private void RenderSidebar()
    {
        if (_options.Compact) return;
        _sidebar.Children.Clear();
        void Heading(string title)
        {
            var label = Label(_text(title)); label.FontSize = 11; label.FontWeight = FontWeight.SemiBold; label.Margin = new(8, 14, 0, 4);
            FileBrowserTheme.Bind(label, TextBlock.ForegroundProperty, "Muted"); _sidebar.Children.Add(label);
        }
        void Place(string name, string glyph, FileBrowserItem? folder, Func<Task>? action = null, string? count = null)
        {
            var active = folder is null ? _recent && name == _text(_directory?.Folder.Name ?? "") : !_recent && CurrentFolder?.Address == folder.Address;
            var row = new Grid { ColumnDefinitions = new("20,*,Auto"), ColumnSpacing = 7 };
            if (_glyphs is not null) row.Children.Add(_glyphs.Glyph(glyph, active));
            var label = Label(name); label.FontSize = 13; label.TextWrapping = TextWrapping.Wrap; ToolTip.SetTip(label, name);
            Grid.SetColumn(label, 1); row.Children.Add(label);
            if (count is not null) { var number = Label(count); number.FontSize = 11; FileBrowserTheme.Bind(number, TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(number, 2); row.Children.Add(number); }
            var button = Button(name, action ?? (async () => { if (folder is not null) await NavigateAsync(folder); }));
            button.Content = row; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.MinHeight = 30; button.Padding = new(8, 0); button.BorderThickness = new(0);
            FileBrowserTheme.Bind(button, Avalonia.Controls.Button.CornerRadiusProperty, "Radius");
            FileBrowserTheme.Bind(button, Avalonia.Controls.Button.BackgroundProperty, active ? "Selected" : "Sidebar");
            FileBrowserTheme.Bind(button, Avalonia.Controls.Button.ForegroundProperty, active ? "SelectionText" : "Text");
            ToolTip.SetTip(button, folder?.Address ?? name);
            if (glyph == "star" && folder is not null)
            {
                var remove = new MenuItem { Header = _text("移除收藏") };
                remove.Click += async (_, _) => { try { await _source.SetFavoriteAsync(folder, false, _lifetime.Token); await RefreshHistoryAsync(); } catch (Exception error) { ShowError(error); } };
                button.ContextMenu = new ContextMenu { ItemsSource = new[] { remove } };
            }
            _sidebar.Children.Add(button);
        }
        Heading("最近使用");
        if (!_options.Folders) Place(_text("最近文件"), "clock", null, ShowRecentAsync, _recentHistory.Files.Count.ToString());
        Place(_text("最近文件夹"), "folder", null, ShowRecentFoldersAsync, _recentHistory.Folders.Count.ToString());
        foreach (var visit in _recentHistory.Folders.Take(4)) Place(visit.Item.Name, "folder", visit.Item);
        Heading("个人收藏");
        foreach (var folder in _recentHistory.Favorites) Place(folder.Name, "star", folder);
        var favorite = Button(CurrentFolder is { } current && _recentHistory.Favorites.Any(item => item.Address == current.Address) ? "移除当前收藏" : "收藏当前文件夹", async () =>
        {
            if (CurrentFolder is not { } folder) return;
            await _source.SetFavoriteAsync(folder, !_recentHistory.Favorites.Any(item => item.Address == folder.Address), _lifetime.Token);
            await RefreshHistoryAsync();
        });
        favorite.IsEnabled = CurrentFolder is not null; favorite.FontSize = 11.5; favorite.Margin = new(8, 4); _sidebar.Children.Add(favorite);
        Heading("位置"); foreach (var location in _locations.Where(item => !item.Device)) Place(location.Name, LocationGlyph(location.Name), location.Folder);
        Heading("设备"); foreach (var location in _locations.Where(item => item.Device)) Place(location.Name, "hard-drive", location.Folder);
    }
    private static string LocationGlyph(string name) => name switch { "个人文件夹" => "house", "桌面" => "monitor", "下载" => "download", "视频" => "video", "图片" => "image", _ => "folder" };
    private void RenderBreadcrumbs()
    {
        if (_recent) { _crumbs.Children.Clear(); _crumbs.Children.Add(Label(_text(_directory?.Folder.Name ?? "最近使用"))); return; }
        RenderPathBreadcrumbs();
    }
    private void RenderRecentFolders()
    {
        _recentFolders.Children.Clear(); _recentFolders.IsVisible = _recent && _recentHistory.Folders.Count > 0;
        if (!_recentFolders.IsVisible) return;
        var label = Label(_text("最近文件夹")); label.FontSize = 11.5; FileBrowserTheme.Bind(label, TextBlock.ForegroundProperty, "Muted"); _recentFolders.Children.Add(label);
        var row = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 10 };
        var index = 0;
        foreach (var visit in _recentHistory.Folders.Take(3))
        {
            var button = Button(visit.Item.Name, () => NavigateAsync(visit.Item)); button.MinHeight = 52; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Content = _glyphs is null ? Label(visit.Item.Name) : Line(_glyphs.Glyph("folder"), visit.Item.Name);
            ToolTip.SetTip(button, visit.Item.Address); Grid.SetColumn(button, index++); row.Children.Add(button);
        }
        _recentFolders.Children.Add(row);
    }
    private void UpdateTabs()
    {
        _tabs.Children.Clear();
        foreach (var kind in new[] { FileBrowserKind.All, FileBrowserKind.Video, FileBrowserKind.Image, FileBrowserKind.Audio, FileBrowserKind.Subtitle, FileBrowserKind.Pdf })
        {
            var count = _kindCounts.GetValueOrDefault(kind);
            var button = Button($"{FileBrowserKinds.Name(kind)} {count}", () => SetMediaKindAsync(kind));
            button.Padding = new(12, 4); button.FontSize = 12.5; button.MinHeight = 28; button.BorderThickness = new(0);
            button.IsVisible = !_options.Folders; button.FontWeight = kind == _kind ? FontWeight.SemiBold : FontWeight.Normal;
            FileBrowserTheme.Bind(button, Avalonia.Controls.Button.BackgroundProperty, kind == _kind ? "Surface" : "Fill"); FileBrowserTheme.Bind(button, Avalonia.Controls.Button.CornerRadiusProperty, "Radius");
            _tabs.Children.Add(button);
        }
    }
    private void UpdateInspector(FileBrowserEntry? entry)
    {
        if (_options.Compact) return;
        _inspectorCell?.ReleaseImage(); _inspectorCell = null; _inspector.Children.Clear(); _infoValues.Clear();
        if (entry is null) { var empty = Label(_text("选择文件以查看预览和信息")); empty.TextWrapping = TextWrapping.Wrap; FileBrowserTheme.Bind(empty, TextBlock.ForegroundProperty, "Muted"); _inspector.Children.Add(empty); return; }
        _inspectorCell = new(entry, true, _glyphs, _text, preview: true); _inspector.Children.Add(_inspectorCell);
        void Info(string name, string value)
        {
            var row = new Grid { ColumnDefinitions = new("70,*"), ColumnSpacing = 8, MinHeight = 31 };
            var key = Label(_text(name)); key.FontSize = 12; FileBrowserTheme.Bind(key, TextBlock.ForegroundProperty, "Muted"); row.Children.Add(key);
            var content = Label(value); content.FontSize = 12; content.HorizontalAlignment = HorizontalAlignment.Right; content.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(content, 1); row.Children.Add(content); _infoValues[name] = content;
            var border = new Border { Child = row, BorderThickness = new(0, 0, 0, 1) }; FileBrowserTheme.Bind(border, Border.BorderBrushProperty, "Line"); _inspector.Children.Add(border);
        }
        Info("类型", _text(entry.IsFolder ? "文件夹" : FileBrowserKinds.Name(FileBrowserKinds.FromName(entry.Name))));
        if (!entry.IsFolder) { Info("时长", "—"); Info("分辨率", "—"); Info("帧率", "—"); Info("编码", "—"); Info("大小", Size(entry.Size)); }
        Info("修改时间", entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—");
        Info("位置", entry.Item.Address);
    }
    private void QueueVisibleMedia()
    {
        if (_options.Compact || _disposed || _visibleQueued) return;
        _visibleQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _visibleQueued = false; if (_disposed || _loading || _filtering) return;
            var visible = new HashSet<FileBrowserEntry>();
            foreach (var cell in _cells)
            {
                var origin = cell.TranslatePoint(default, _entries);
                if (origin is { } point && point.Y < _entries.Bounds.Height && point.Y + cell.Bounds.Height > 0 && NeedsMedia(cell.Entry)) visible.Add(cell.Entry);
            }
            if (_inspectorHost.IsVisible && _focusEntry is { } focus && NeedsMedia(focus)) visible.Add(focus);
            _mediaWork?.Update(visible);
            foreach (var entry in visible)
                if (_mediaWork?.TryGetResult(entry, out var media) == true) ApplyMedia(entry, media);
        }, DispatcherPriority.Background);
    }
    private static bool NeedsMedia(FileBrowserEntry entry) => !entry.IsFolder && FileBrowserKinds.FromName(entry.Name) is FileBrowserKind.Video or FileBrowserKind.Image or FileBrowserKind.Audio;
    private void ApplyMedia(FileBrowserEntry entry, FileBrowserMedia? media)
    {
        if (_disposed || media is null || !_entryIndexes.ContainsKey(entry)) return;
        foreach (var cell in _cells.Where(cell => Equals(cell.Entry, entry))) cell.ApplyMedia(media);
        if (!Equals(entry, _focusEntry) || _inspectorCell is null) return;
        _inspectorCell.ApplyMedia(media);
        _infoValues["时长"].Text = FileBrowserCell.Duration(media.Duration);
        _infoValues["分辨率"].Text = FileBrowserCell.Resolution(media);
        _infoValues["帧率"].Text = media.FrameRate > 0 ? $"{media.FrameRate:0.##} fps" : "—";
        _infoValues["编码"].Text = string.Join(" · ", new[] { media.VideoCodec, media.AudioCodec }.Where(value => value.Length > 0)) is { Length: > 0 } codecs ? codecs : "—";
    }
}
