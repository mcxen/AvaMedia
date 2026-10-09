using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public partial class MediaRouteWindow
{
    private sealed record RouteSection(string Key, TextBlock Heading, Grid Grid, List<Button> Buttons);
    private IReadOnlyList<MediaRouteOption> _routes = [];
    private MediaRouteSource[] _selected = [];
    private readonly List<RouteSection> _sections = [];
    private readonly Dictionary<Button, (int Column, int Row)> _positions = [];
    private readonly Dictionary<MediaRouteEntry, (Border Row, TextBlock Disposition)> _sourceRows = [];
    private HashSet<string> _acceptedPaths = new(VideoFolderScanner.PathComparer);
    private bool _pointerFeedback;
    private string _lastQuery = "";

    private static (string Key, string Title) SectionFor(Feature feature) => feature.Id switch
    {
        "video-compress" or "video-slim" or "mp4" or "join" or "dvd" or "repair" or "mux" => ("video", "转换与体积"),
        "clip" or "person-clip" or "crop" or "rotate" or "delogo" => ("edit", "剪辑与画面"),
        "split" or "extract-video" or "frames" or "contact-sheet" => ("extract", "提取与截图"),
        "auto-subtitle" or "video-summary" or "media-ai" or "image-ai" => ("analysis", "字幕与分析"),
        "voice-enhance" => ("audio", "音频处理"),
        _ when feature.Category == "音频" => ("audio", "音频处理"),
        _ when feature.Category == "图片" => ("image", "图片处理"),
        _ when feature.Category == "文档" => ("document", "文档处理"),
        _ => ("files", "文件工具")
    };

    private void BuildRouteSections()
    {
        if (_closed) return;
        var previous = _active?.Feature.Id;
        var query = ToolSearch.Text?.Trim() ?? "";
        if (query != _lastQuery) { RouteScroll.Offset = default; _lastQuery = query; }
        var filtered = _routes.Where(route => query.Length == 0 ||
            new[] { route.Title, route.Description, SectionFor(route.Feature).Title, route.Feature.Id,
                Localization.Text(route.Title), Localization.Text(route.Description), Localization.Text(SectionFor(route.Feature).Title) }
                .Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var majorKind = _selected.GroupBy(source => source.Kind).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key;
        string[] order = majorKind switch
        {
            MediaFileKind.Audio => ["audio", "analysis", "video", "edit", "extract", "image", "document", "files"],
            MediaFileKind.Image => ["image", "analysis", "video", "edit", "extract", "audio", "document", "files"],
            MediaFileKind.Document => ["document", "image", "video", "edit", "extract", "audio", "analysis", "files"],
            _ => ["video", "edit", "extract", "analysis", "audio", "image", "document", "files"]
        };
        _cards.Clear(); _sections.Clear(); _positions.Clear(); RouteSections.Children.Clear();
        foreach (var group in filtered.GroupBy(route => SectionFor(route.Feature)).OrderBy(group => Array.IndexOf(order, group.Key.Key)))
        {
            var heading = Ui.Text(group.Key.Title); heading.FontWeight = FontWeight.SemiBold; heading.Classes.Add("route-group");
            var grid = new Grid(); grid.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("UiSpacingSmall"));
            grid.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("UiSpacingSmall"));
            var section = new StackPanel(); section.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("UiSpacingSmall"));
            section.Children.Add(heading); section.Children.Add(grid); RouteSections.Children.Add(section);
            List<Button> buttons = [];
            foreach (var route in group)
            {
                var card = RouteCard(route); grid.Children.Add(card); buttons.Add(card); _cards.Add((route, card));
            }
            _sections.Add(new(group.Key.Key, heading, grid, buttons));
        }
        ArrangeRouteSections();
        _active = _cards.Select(card => card.Route).FirstOrDefault(route => route.Feature.Id == previous && route.Enabled)
            ?? _cards.Select(card => card.Route).FirstOrDefault(route => route.Enabled) ?? filtered.FirstOrDefault();
        Localization.SetText(RouteCount, $"{filtered.Length} / {_routes.Count} 个工具");
        EmptyText.IsVisible = filtered.Length == 0;
        EmptyText.Text = Localization.Text(_selected.Length == 0 ? "尚未选择文件" : "没有匹配的工具");
        ActivateRoute(_active);
    }

    private double RouteMetric(string name, double fallback) => this.TryFindResource(name, out var value) && value is double metric && metric > 0 ? metric : fallback;

    private void ArrangeRouteSections()
    {
        if (_closed) return;
        var gap = RouteMetric("UiSpacingSmall", 8);
        var width = RouteMetric("UiRouteCardWidth", 180);
        var columns = Math.Max(1, (int)Math.Floor((RouteScroll.Viewport.Width + gap) / (width + gap)));
        if (RouteScroll.Viewport.Width <= 0) columns = 4;
        _positions.Clear(); var rowOffset = 0;
        foreach (var section in _sections)
        {
            if (section.Grid.ColumnDefinitions.Count != columns)
            {
                section.Grid.ColumnDefinitions.Clear();
                for (var column = 0; column < columns; column++) section.Grid.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            }
            var rows = (section.Buttons.Count + columns - 1) / columns;
            while (section.Grid.RowDefinitions.Count > rows) section.Grid.RowDefinitions.RemoveAt(section.Grid.RowDefinitions.Count - 1);
            while (section.Grid.RowDefinitions.Count < rows) section.Grid.RowDefinitions.Add(new(GridLength.Auto));
            for (var index = 0; index < section.Buttons.Count; index++)
            {
                Grid.SetRow(section.Buttons[index], index / columns); Grid.SetColumn(section.Buttons[index], index % columns);
                _positions[section.Buttons[index]] = (index % columns, rowOffset + index / columns);
            }
            rowOffset += rows;
        }
    }

    private Button RouteCard(MediaRouteOption route)
    {
        var button = new Button { Classes = { "route-card" }, IsEnabled = route.Enabled };
        AutomationProperties.SetName(button, Localization.Text(route.Title));
        AutomationProperties.SetHelpText(button, Localization.Join(" · ", [Localization.Text(route.Description),
            route.Enabled ? Localization.Format($"接收 {route.Files.Length} 项 · 跳过 {route.SkippedCount} 项") : Localization.Text(route.DisabledReason)]));
        var body = new Grid { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto"), RowSpacing = 4 };
        body.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("UiSpacingSmall"));
        var icon = new FeatureIcon { Kind = route.Feature.Icon, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(WidthProperty, new DynamicResourceExtension("UiIconLarge")); icon.Bind(HeightProperty, new DynamicResourceExtension("UiIconLarge"));
        body.Children.Add(icon);
        var title = Ui.Text(route.Title); title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.Wrap;
        title.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1); body.Children.Add(title);
        var summary = !route.Enabled ? Ui.Text(route.DisabledReason, "caption") : route.SkippedCount > 0
            ? Ui.FormattedText($"接收 {route.Files.Length} 项 · 跳过 {route.SkippedCount} 项", "caption") : Ui.Text(route.Description, "caption");
        summary.TextWrapping = TextWrapping.Wrap; summary.MaxLines = 2; summary.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetRow(summary, 1); Grid.SetColumnSpan(summary, 2); body.Children.Add(summary); button.Content = body;
        button.PointerEntered += (_, _) => ActivateRoute(route, true);
        button.PointerExited += (_, _) => { _pointerFeedback = false; UpdateFlowTarget(); };
        button.GotFocus += (_, _) => ActivateRoute(route);
        button.KeyDown += ToolKeyDown;
        button.Click += (_, _) => OpenRoute(route);
        return button;
    }

    private string FileSummary(IEnumerable<MediaRouteSource> sources) => Localization.Join(" · ", sources.GroupBy(source => source.Kind)
        .Select(group => Localization.Format($"{Localization.Key(MediaRouteEntry.KindName(group.Key))} × {group.Count()}")));

    private void ActivateRoute(MediaRouteOption? route, bool pointerFeedback = false)
    {
        _active = route; _pointerFeedback = pointerFeedback;
        _acceptedPaths = route is { Enabled: true } ? route.Files.ToHashSet(VideoFolderScanner.PathComparer) : new(VideoFolderScanner.PathComparer);
        foreach (var card in _cards) card.Button.Classes.Set("active", card.Route == route);
        foreach (var section in _sections) section.Heading.Classes.Set("active", route is not null && section.Key == SectionFor(route.Feature).Key);
        OpenButton.IsEnabled = route?.Enabled == true;
        if (route is null)
        {
            DestinationTitle.Text = Localization.Text(_selected.Length == 0 ? "尚未选择文件" : "没有匹配的工具");
            DestinationSummary.Text = ""; OpenButton.Content = Localization.Text("进入工具");
        }
        else
        {
            var matching = route.Files.ToHashSet(VideoFolderScanner.PathComparer);
            Localization.SetText(DestinationTitle, $"{FileSummary(_selected.Where(source => matching.Contains(source.Path)))} → {Localization.Key(route.Title)}");
            DestinationSummary.Text = !route.Enabled ? Localization.Text(route.DisabledReason) : Localization.Join(" · ", route.SkippedCount > 0
                ? [Localization.Text(route.Description), Localization.Format($"跳过 {FileSummary(_selected.Where(source => !_acceptedPaths.Contains(source.Path)))}")]
                : [Localization.Text(route.Description)]);
            OpenButton.Content = Localization.Text("进入工具");
        }
        UpdateSourceFeedback(); UpdateFlowTarget();
    }

    private void UpdateSourceFeedback()
    {
        foreach (var entry in _entries) entry.SetDisposition(!entry.Include || _active is not { Enabled: true } ? "" : _acceptedPaths.Contains(entry.Source.Path) ? "带入" : "跳过");
        foreach (var (entry, controls) in _sourceRows)
        {
            var accepted = entry.Include && _acceptedPaths.Contains(entry.Source.Path);
            controls.Row.Classes.Set("accepted", accepted); controls.Disposition.Classes.Set("accepted", accepted);
        }
    }

    private void UpdateFlowTarget()
    {
        if (_closed) return;
        List<MediaRoutePort> ports = [];
        if (RouteScroll.Viewport.Height > 8 && RouteScroll.TranslatePoint(default, RouteFlow) is { } viewport)
            foreach (var section in _sections)
                if (section.Heading.TranslatePoint(default, RouteFlow) is { } point && section.Grid.TranslatePoint(default, RouteFlow) is { } gridPoint)
                {
                    var bottom = viewport.Y + RouteScroll.Viewport.Height;
                    if (gridPoint.Y + section.Grid.Bounds.Height > viewport.Y && point.Y < bottom)
                    {
                        var y = Math.Clamp(point.Y + section.Heading.Bounds.Height / 2, viewport.Y + 4, bottom - 4);
                        ports.Add(new(section.Key, new(RouteFlow.Bounds.Width - 2, y)));
                    }
                }
        List<MediaRouteInputPort> inputs = [];
        if (SourceList.TranslatePoint(default, RouteFlow) is { } sourceViewport)
            foreach (var (entry, controls) in _sourceRows)
                if (entry.Include && controls.Row.TranslatePoint(default, RouteFlow) is { } point)
                {
                    var top = Math.Max(point.Y, sourceViewport.Y);
                    var bottom = Math.Min(point.Y + controls.Row.Bounds.Height, sourceViewport.Y + SourceList.Bounds.Height);
                    if (bottom - top > 8) inputs.Add(new(entry.Source.Path, new(0, (top + bottom) / 2), _acceptedPaths.Contains(entry.Source.Path)));
                }
        if (!RouteFlow.Ports.SequenceEqual(ports)) RouteFlow.Ports = ports;
        if (!RouteFlow.Inputs.SequenceEqual(inputs)) RouteFlow.Inputs = inputs;
        RouteFlow.ActiveKey = _active is null ? "" : SectionFor(_active.Feature).Key;
        RouteFlow.AcceptedCount = _acceptedPaths.Count;
        RouteFlow.Highlighted = _active is { Enabled: true };
        RouteFlow.AnimateFeedback = _pointerFeedback;
    }

    private void ToolKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None || sender is not Button button || !_positions.TryGetValue(button, out var current)) return;
        var enabled = _cards.Where(card => card.Button.IsEnabled).Select(card => card.Button).ToArray();
        Button? target = args.Key switch
        {
            Key.Home => enabled.FirstOrDefault(), Key.End => enabled.LastOrDefault(),
            Key.Left => enabled.Where(item => _positions[item].Row == current.Row && _positions[item].Column < current.Column)
                .OrderByDescending(item => _positions[item].Column).FirstOrDefault(),
            Key.Right => enabled.Where(item => _positions[item].Row == current.Row && _positions[item].Column > current.Column)
                .OrderBy(item => _positions[item].Column).FirstOrDefault(),
            Key.Up => enabled.Where(item => _positions[item].Row < current.Row).OrderByDescending(item => _positions[item].Row)
                .ThenBy(item => Math.Abs(_positions[item].Column - current.Column)).FirstOrDefault(),
            Key.Down => enabled.Where(item => _positions[item].Row > current.Row).OrderBy(item => _positions[item].Row)
                .ThenBy(item => Math.Abs(_positions[item].Column - current.Column)).FirstOrDefault(),
            _ => null
        };
        if (args.Key is Key.Home or Key.End or Key.Left or Key.Right or Key.Up or Key.Down) args.Handled = true;
        if (target is not null) { target.Focus(NavigationMethod.Directional); target.BringIntoView(); }
    }

    private void SearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && !string.IsNullOrEmpty(ToolSearch.Text)) { ToolSearch.Clear(); args.Handled = true; }
        else if (args.Key == Key.Down && _cards.FirstOrDefault(card => card.Button.IsEnabled).Button is { } first)
        { args.Handled = true; first.Focus(NavigationMethod.Directional); first.BringIntoView(); }
    }

    private void RoutingKeyDown(object? sender, KeyEventArgs args)
    {
        if (_pointerFeedback) { _pointerFeedback = false; UpdateFlowTarget(); }
        if (args.Handled) return;
        if (args.Key == Key.F && (args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { args.Handled = true; ToolSearch.Focus(); ToolSearch.SelectAll(); }
    }
}
