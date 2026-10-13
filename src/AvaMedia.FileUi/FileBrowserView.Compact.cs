using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AvaMedia.FileUi;

/// <summary>
/// Compact presentation: icon navigation, breadcrumb, flat place list and checkbox rows.
/// </summary>
public sealed partial class FileBrowserView
{
    private readonly StackPanel _compactPlaces = new() { Spacing = 2 };
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _compactCount = new() { VerticalAlignment = VerticalAlignment.Center, [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbCaptionFont") };
    private readonly Border _placesColumn = new() { Width = 132, Padding = new(8, 8, 6, 8) };
    private IReadOnlyList<FileBrowserLocation> _locations = [];
    private IFileBrowserGlyphs? _glyphs;
    private bool _syncingSelection;

    /// <summary>Host actions shown in the compact footer next to the add button.</summary>
    public StackPanel FooterActions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private bool _showPlaces = true, _showFooter = true;
    public bool ShowPlaces { get => _showPlaces; set { _showPlaces = value; UpdateResponsivePanels(); } }
    public bool ShowFooter { get => _showFooter; set { _showFooter = value; if (_footerHost is not null) _footerHost.IsVisible = value; } }
    public Button AddButton => _select;
    public event Action<int, int>? CompactSelectionChanged;

    private void BuildCompact(IFileBrowserGlyphs? glyphs)
    {
        _glyphs = glyphs;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        // Navigation row
        var navigation = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 6, Margin = new(12, 8, 12, 8) };
        _back = IconButton("chevron-left", "后退", async () => { if (_history.TryPop(out var folder)) await NavigateAsync(folder, false); });
        _up = IconButton("chevron-up", "上一级", async () => { if (_directory?.Parent is { } parent) await NavigateAsync(parent); });
        navigation.Children.Add(_back); Grid.SetColumn(_up, 1); navigation.Children.Add(_up);
        var crumbHost = new Border { [!Border.CornerRadiusProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbCrumbRadius"), Padding = new(10, 5), Child = new ScrollViewer
        { Content = _crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        FileBrowserTheme.Bind(crumbHost, Border.BackgroundProperty, "Fill"); Grid.SetColumn(crumbHost, 2); navigation.Children.Add(crumbHost);
        var refresh = IconButton("refresh-cw", "刷新", async () => { if (CurrentFolder is { } folder) await NavigateAsync(folder, false); });
        Grid.SetColumn(refresh, 3); navigation.Children.Add(refresh);
        root.Children.Add(navigation);

        var body = new Grid { ColumnDefinitions = new("Auto,*") };
        body.Children.Add(new Border { BorderBrush = Hair, BorderThickness = new(0, 1, 0, 0) });
        var placesStack = new StackPanel { Spacing = 4 };
        placesStack.Children.Add(new TextBlock { Text = _text("常用位置"), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Muted, Margin = new(6, 2, 0, 4) });
        placesStack.Children.Add(_compactPlaces);
        _placesColumn.Child = new ScrollViewer { Content = placesStack };
        _placesColumn.BorderBrush = Hair; _placesColumn.BorderThickness = new(0, 1, 1, 0); FileBrowserTheme.Bind(_placesColumn, Border.BackgroundProperty, "Panel");
        body.Children.Add(_placesColumn);
        var files = new Grid { RowDefinitions = new("Auto,*") };
        var header = new Grid { ColumnDefinitions = new("34,*,Auto"), Margin = new(0, 0, 14, 0), [!Grid.HeightProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbHeaderHeight") };
        var nameHeader = new TextBlock { Text = _text("名称"), [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbCaptionFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new(22, 0, 0, 0) };
        Grid.SetColumn(nameHeader, 1); header.Children.Add(nameHeader);
        var sizeHeader = new TextBlock { Text = _text("大小"), [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbCaptionFont"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sizeHeader, 2); header.Children.Add(sizeHeader);
        files.Children.Add(new Border { Child = header, BorderBrush = Hair, BorderThickness = new(0, 1, 0, 1) });
        _entries.SelectionMode = _options.Multiple ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Single;
        _entries.ItemTemplate = new FuncDataTemplate<FileBrowserEntry>((entry, _) => entry is null ? new Border() : CompactRow(entry));
        _entries.SelectionChanged += async (_, args) =>
        {
            if (_syncingSelection) return;
            var folder = args.AddedItems.OfType<FileBrowserEntry>().FirstOrDefault(entry => entry.IsFolder);
            if (folder is null) return;
            _syncingSelection = true;
            try { _entries.SelectedItems?.Remove(folder); } finally { _syncingSelection = false; }
            await NavigateAsync(folder.Item);
        };
        _entries.SelectionChanged += (_, _) => UpdateSelection();
        ScrollViewer.SetHorizontalScrollBarVisibility(_entries, ScrollBarVisibility.Disabled);
        Grid.SetRow(_entries, 1); files.Children.Add(_entries);
        Grid.SetColumn(files, 1); body.Children.Add(files);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(12, 8), ColumnSpacing = 8 };
        _select = new Button { Content = _text("添加所选项"), Classes = { "primary" } };
        if (_glyphs is not null) _select.Content = Line(_glyphs.Glyph("plus-white"), _text("添加所选项"));
        _select.Click += (_, _) => Confirm();
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; left.Children.Add(_select); left.Children.Add(FooterActions);
        footer.Children.Add(left);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        FileBrowserTheme.Bind(_status, TextBlock.FontSizeProperty, "CaptionFont"); _status.Foreground = Muted; _status.TextWrapping = TextWrapping.NoWrap; _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _compactCount.Foreground = Muted; status.Children.Add(_status); status.Children.Add(_compactCount);
        Grid.SetColumn(status, 2); footer.Children.Add(status);
        var footerBorder = _footerHost = new Border { Child = footer, BorderBrush = Hair, BorderThickness = new(0, 1, 0, 0) };
        Grid.SetRow(footerBorder, 2); root.Children.Add(footerBorder);
        Content = root;
        PropertyChanged += (_, args) => { if (args.Property == BoundsProperty) UpdateResponsivePanels(); };
        AutomationProperties.SetName(_entries, _text("文件列表"));
        UpdateSelection();
    }

    private IBrush Hair => this.TryFindResource("FbLine", out var value) && value is IBrush brush ? brush : Brushes.Transparent;
    private IBrush Muted => this.TryFindResource("FbMuted", out var value) && value is IBrush brush ? brush : Brushes.Transparent;

    private Button IconButton(string glyph, string label, Func<Task> action)
    {
        var button = new Button { Content = _glyphs?.Glyph(glyph) ?? (object)_text(label), [!Avalonia.Controls.Button.WidthProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbActionHeight"), [!Avalonia.Controls.Button.HeightProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbActionHeight"), Padding = new(0), MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        button.Classes.Add("ac"); button.Classes.Add("icon"); button.Classes.Add("borderless");
        FileBrowserTheme.Bind(button, Avalonia.Controls.Button.BackgroundProperty, "ToolbarFill");
        FileBrowserTheme.Bind(button, Avalonia.Controls.Button.BorderThicknessProperty, "ToolbarBorder");
        FileBrowserTheme.Bind(button, Avalonia.Controls.Button.CornerRadiusProperty, "Radius");
        AutomationProperties.SetName(button, _text(label)); ToolTip.SetTip(button, _text(label));
        button.Click += async (_, _) => { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { ShowError(error); } };
        return button;
    }
    private static StackPanel Line(Control glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(glyph); panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private Control CompactRow(FileBrowserEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new("34,22,*,Auto"), [!Grid.HeightProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbTextRowHeight"), Margin = new(0, 0, 14, 0), Background = Brushes.Transparent };
        if (!entry.IsFolder)
        {
            var check = new CheckBox { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, Focusable = false };
            check.Bind(ToggleButton.IsCheckedProperty, new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(ListBoxItem) } });
            row.Children.Add(check);
        }
        if (_glyphs is not null) { var glyph = _glyphs.Entry(entry.Item); Grid.SetColumn(glyph, 1); row.Children.Add(glyph); }
        var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(6, 0, 8, 0), [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbBodyFont") };
        name.Classes.Add("raw"); ToolTip.SetTip(name, entry.Name); Grid.SetColumn(name, 2); row.Children.Add(name);
        Control trailing = entry.IsFolder
            ? (Control?)_glyphs?.Glyph("chevron-right-muted") ?? new TextBlock { Text = _text("文件夹"), [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbCaptionFont"), VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = Size(entry.Size), [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbCaptionFont"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(trailing, 3); row.Children.Add(trailing);
        return row;
    }

    private void SetCompactLocations(IReadOnlyList<FileBrowserLocation> locations)
    {
        if (!_options.Compact) return;
        // Pseudo file systems are not useful places for media; keep the list short like a Finder sidebar.
        static bool System(string address) => address.Replace('\\', '/') is var path
            && new[] { "/proc", "/sys", "/dev", "/run", "/snap", "/boot" }.Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));
        _locations = locations.Where(location => !System(location.Folder.Address)).Take(8).ToArray(); RenderPlaces();
    }
    private void RenderPlaces()
    {
        _compactPlaces.Children.Clear();
        var current = _directory?.Folder.Address ?? "";
        var active = _locations.Where(location => current.StartsWith(location.Folder.Address, StringComparison.Ordinal))
            .OrderByDescending(location => location.Folder.Address.Length).FirstOrDefault();
        foreach (var location in _locations)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            if (_glyphs is not null) content.Children.Add(_glyphs.Place(location));
            var label = new TextBlock { Text = location.Name, VerticalAlignment = VerticalAlignment.Center, [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbBodyFont"), TextTrimming = TextTrimming.CharacterEllipsis };
            label.Classes.Add("raw"); content.Children.Add(label);
            var button = new Button { Content = content, Classes = { "nav" } };
            if (ReferenceEquals(location, active)) button.Classes.Add("active");
            ToolTip.SetTip(button, _source.DisplayAddress(location.Folder));
            button.Click += async (_, _) => { try { await NavigateAsync(location.Folder); } catch (Exception error) { ShowError(error); } };
            _compactPlaces.Children.Add(button);
        }
    }

    private void CompactDirectoryChanged()
    {
        if (!_options.Compact || _directory is null) return;
        RenderPlaces(); RenderPathBreadcrumbs();
    }
    private void RenderPathBreadcrumbs()
    {
        if (_directory is null) return;
        _crumbs.Children.Clear();
        var address = _source.DisplayAddress(_directory.Folder);
        var root = _locations.Where(location => address.StartsWith(location.Folder.Address, StringComparison.Ordinal))
            .OrderByDescending(location => location.Folder.Address.Length).FirstOrDefault();
        var crumbs = new List<(string Name, string Address)>();
        var separator = address.Contains('\\') && !address.Contains('/') ? '\\' : '/';
        string rest;
        if (root is not null)
        {
            crumbs.Add((root.Name, root.Folder.Address));
            rest = address[root.Folder.Address.Length..];
        }
        else rest = address;
        var prefix = root?.Folder.Address.TrimEnd(separator) ?? "";
        foreach (var part in rest.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            prefix = prefix.Length == 0 && address.StartsWith(separator) ? separator + part : prefix + (prefix.EndsWith(separator) || prefix.Length == 0 ? "" : separator.ToString()) + part;
            crumbs.Add((part, prefix));
        }
        if (crumbs.Count == 0) crumbs.Add((address, address));
        if (_glyphs is not null) _crumbs.Children.Add(_glyphs.Glyph("hard-drive-muted"));
        for (var index = 0; index < crumbs.Count; index++)
        {
            var (name, target) = crumbs[index]; var last = index == crumbs.Count - 1;
            if (index > 0) { var divider = Label("›"); FileBrowserTheme.Bind(divider, TextBlock.ForegroundProperty, "Muted"); _crumbs.Children.Add(divider); }
            var text = new TextBlock { Text = name, [!TextBlock.FontSizeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FbBodyFont"), FontWeight = last ? FontWeight.Bold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
            text.Classes.Add("raw");
            if (last) { _crumbs.Children.Add(text); continue; }
            var crumb = new Button { Content = text, Padding = new(2, 0), MinHeight = 0, Background = Brushes.Transparent };
            crumb.Classes.Add("plain");
            crumb.Click += async (_, _) => { try { if (await _source.ResolveAsync(target, _lifetime.Token) is { IsFolder: true } folder) await NavigateAsync(folder); } catch (Exception error) { ShowError(error); } };
            _crumbs.Children.Add(crumb);
        }
    }

    private void UpdateCompactSelection(int count)
    {
        var total = (_entries.ItemsSource as IEnumerable<FileBrowserEntry>)?.Count(entry => !entry.IsFolder) ?? 0;
        _compactCount.Text = string.Format(_text("勾选 {0} / {1} 项"), count, total);
        CompactSelectionChanged?.Invoke(count, total);
    }
    /// <summary>Selects every file in the current folder (compact mode convenience).</summary>
    public void ClearSelection() { _entries.SelectedItems?.Clear(); _chosen.Clear(); UpdateSelection(); }
}
