using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private sealed record FeatureCell(Button Button, int Row, int Column, int Span);
    private sealed record FeatureGroup(CategoryHeader? Header, Grid Container, Border Body, string Title);
    private sealed record CategoryView(ScrollViewer Scroll, StackPanel Stack,
        Dictionary<string, FeatureCell> Cells, List<FeatureGroup> Groups, int Tools, int Formats);
    private readonly Dictionary<string, CategoryHeader> _categoryHeaders = [];
    private readonly Dictionary<string, Vector> _categoryOffsets = [];
    private readonly Dictionary<string, CategoryView> _categoryViews = [];
    private readonly Dictionary<string, Button> _featureButtons = [];
    private readonly ConditionalWeakTable<Button, TextBlock> _featureLabels = new();
    private readonly HashSet<string> _closedFeatureGroups = [];
    private readonly NavigationMotion _navigationMotion = new();
    private bool _updatingCategories, _featureRefreshQueued, _navigationKeyboard, _toolPanelHidden, _navigationBeta;
    private double _expandedSidebarWidth = 360;
    private string? _selectedFeatureId;
    private int _categoryRevision, _panelRevision;

    private bool AnimateNavigation => !_navigationKeyboard && Motion.CanAnimate(FeatureSidebar);

    private void InitializeCategories()
    {
        _navigationBeta = _settings.EnableBetaFeatures;
        FeatureViewHost.SizeChanged += (_, _) => RefreshFeatureMetrics();
        Categories.SizeChanged += (_, _) => MoveCategoryIndicator(false);
        FeatureSidebar.AddHandler(InputElement.KeyDownEvent, (_, _) =>
        {
            _navigationKeyboard = true; _navigationMotion.FinishAll();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        FeatureSidebar.AddHandler(InputElement.PointerPressedEvent, (_, _) => _navigationKeyboard = false,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty && !IsVisible
                || change.Property == WindowStateProperty && WindowState == WindowState.Minimized)
                _navigationMotion.FinishAll();
        };
        Closed += (_, _) => { _navigationMotion.Dispose(); Localization.Changed -= FeatureLanguageChanged; };
        Localization.Changed += FeatureLanguageChanged;
        FeatureSidebar.ZIndex = 2;
        for (var index = 0; index < Catalog.Categories.Length; index++)
        {
            var category = Catalog.Categories[index];
            Categories.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var header = new CategoryHeader(FeatureNavigation.CategoryTitle(category), FeatureNavigation.CategoryIcon(category))
                { Margin = new Thickness(0, 0, 0, 3) };
            _categoryHeaders.Add(category, header);
            Grid.SetRow(header, index); Categories.Children.Add(header);
            header.Click += (_, _) => ShowCategory(category);
            header.PropertyChanged += (_, change) =>
            {
                if (_updatingCategories || change.Property != CategoryHeader.IsExpandedProperty) return;
                if (header.IsExpanded) ShowCategory(category);
                else if (_category == category) CollapseCategory();
            };
            header.KeyDown += (_, args) => NavigateCategories(category, args);
            EnableCategoryDropNavigation(header, category);
        }
        Grid.SetRowSpan(CategoryIndicatorLayer, Catalog.Categories.Length);
        SidebarToggle.SetPanelToggle(true);
        SidebarToggle.Click += (_, _) => SetToolPanelHidden(!_toolPanelHidden);
        SidebarToggle.PropertyChanged += (_, change) =>
        {
            if (!_updatingCategories && change.Property == CategoryHeader.IsExpandedProperty)
                SetToolPanelHidden(!SidebarToggle.IsExpanded);
        };
    }

    private void ShowCategory(string category)
    {
        if (!Catalog.Categories.Contains(category)) return;
        if (_categoryHeaders.Count == 0) InitializeCategories();
        RememberCategoryOffset();
        if (_navigationBeta != _settings.EnableBetaFeatures)
        {
            _navigationMotion.FinishAll();
            foreach (var cached in _categoryViews.Values) FeatureViewHost.Children.Remove(cached.Scroll);
            _categoryViews.Clear(); _navigationBeta = _settings.EnableBetaFeatures;
        }
        var oldCategory = _category;
        _categoryViews.TryGetValue(oldCategory, out var oldView);
        _category = category;
        var view = GetCategoryView(category);
        if (_toolPanelHidden) SetToolPanelHidden(false);
        var revision = ++_categoryRevision;
        // Rapid changes retain only the current and the outgoing category.
        foreach (var (key, other) in _categoryViews)
            if (key != category && other != oldView) HideCategoryView(other);
        view.Scroll.IsVisible = true; view.Scroll.IsEnabled = true; view.Scroll.IsHitTestVisible = true;
        view.Scroll.ZIndex = 1;
        _featureButtons.Clear();
        foreach (var (id, cell) in view.Cells) _featureButtons.Add(id, cell.Button);
        UpdateSidebarSelection();
        RestoreFeatureOffset(view, revision);
        if (oldView is not null && oldView != view && oldView.Scroll.IsVisible)
        {
            oldView.Scroll.IsEnabled = false; oldView.Scroll.IsHitTestVisible = false;
            oldView.Scroll.ZIndex = 0;
            var direction = Array.IndexOf(Catalog.Categories, category) > Array.IndexOf(Catalog.Categories, oldCategory) ? 1 : -1;
            _navigationMotion.Animate(oldView.Scroll, 85, new Vector(0, -direction * 5), 0,
                enabled: AnimateNavigation, completed: () =>
                {
                    if (revision == _categoryRevision && _category != oldCategory) HideCategoryView(oldView);
                });
            _navigationMotion.Animate(view.Scroll, 150, from: new Vector(0, direction * 6), fromOpacity: 0,
                enabled: AnimateNavigation);
        }
        else
        {
            _navigationMotion.Cancel(view.Scroll);
            view.Scroll.Opacity = 1; view.Scroll.RenderTransform = null;
        }
        MoveCategoryIndicator(AnimateNavigation);
    }

    private void HideCategoryView(CategoryView view)
    {
        _navigationMotion.Cancel(view.Scroll);
        foreach (var group in view.Groups)
        {
            _navigationMotion.Finish(group.Body); _navigationMotion.Finish(group.Container);
        }
        view.Scroll.IsVisible = false; view.Scroll.IsEnabled = false; view.Scroll.IsHitTestVisible = false;
        view.Scroll.Opacity = 1; view.Scroll.RenderTransform = null;
        foreach (var (id, cell) in view.Cells) ClearFeatureDropFeedback(cell.Button, Catalog.Find(id));
    }

    private CategoryView GetCategoryView(string category)
    {
        var columns = FeatureColumns();
        if (_categoryViews.TryGetValue(category, out var existing))
        {
            if ((existing.Tools, existing.Formats) == columns) return existing;
            _categoryOffsets[category] = existing.Scroll.Offset;
            HideCategoryView(existing); FeatureViewHost.Children.Remove(existing.Scroll);
        }
        var stack = new StackPanel { Spacing = 4 };
        var scroll = new ScrollViewer { Content = stack, IsVisible = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var view = new CategoryView(scroll, stack, [], [], columns.Tools, columns.Formats);
        var logicalRow = 0;
        foreach (var section in FeatureNavigation.Sections(category, _settings.EnableBetaFeatures))
        {
            var key = category + ":" + section.Title;
            var titled = section.Title.Length > 0;
            var header = titled ? new CategoryHeader(section.Title, "", isGroup: true) { IsExpanded = !_closedFeatureGroups.Contains(key) } : null;
            if (header is not null) EnableDropNavigation(header, () => header.IsExpanded = true);
            var grid = new Grid { ColumnSpacing = 2, RowSpacing = 2 };
            for (var index = 0; index < 12; index++) grid.ColumnDefinitions.Add(new(1, GridUnitType.Star));
            var count = section.Compact ? columns.Formats : columns.Tools;
            var span = 12 / count;
            for (var index = 0; index < section.Features.Length; index++)
            {
                var feature = section.Features[index]; var row = index / count; var column = index % count;
                if (column == 0) grid.RowDefinitions.Add(new(GridLength.Auto));
                var content = new Grid { ColumnDefinitions = new(section.Compact ? "*" : "Auto,*"), ColumnSpacing = 5 };
                if (!section.Compact)
                {
                    var icon = new FeatureIcon { Kind = feature.Icon, Label = "", Width = 18, Height = 18,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                    icon.Bind(WidthProperty, new DynamicResourceExtension("UiFeatureIconHeight"));
                    icon.Bind(HeightProperty, new DynamicResourceExtension("UiFeatureIconHeight"));
                    content.Children.Add(icon);
                }
                var text = new TextBlock { Text = section.Compact ? feature.Format.ToUpperInvariant() : feature.Label,
                    FontSize = section.Compact ? 11 : 12, MaxLines = 1, TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = section.Compact ? TextAlignment.Center : TextAlignment.Left };
                if (!section.Compact) Grid.SetColumn(text, 1);
                content.Children.Add(text);
                var button = new Button { Name = "Feature_" + feature.Id.Replace('-', '_'), Content = content,
                    Classes = { "feature-menu" }, MinHeight = section.Compact ? 30 : 34, MinWidth = 0,
                    Padding = new Thickness(4, 3), BorderThickness = new Thickness(1), Margin = default,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                if (section.Compact) button.Classes.Add("feature-format");
                button.Bind(MinHeightProperty, new DynamicResourceExtension(section.Compact ? "UiFormatRowHeight" : "UiFeatureRowHeight"));
                button.Bind(ThemeProperty, new DynamicResourceExtension("UiNavigationMenuTheme"));
                button.Bind(CornerRadiusProperty, new DynamicResourceExtension("UiNavigationButtonRadius"));
                SetFeatureSelection(button, feature.Id);
                AutomationProperties.SetName(button, feature.Label);
                ToolTip.SetTip(button, feature.Label);
                if (!section.Compact) _featureLabels.Add(button, text);
                button.Click += async (_, _) =>
                {
                    _selectedFeatureId = feature.Id;
                    foreach (var active in _featureButtons) SetFeatureSelection(active.Value, active.Key);
                    await Configure(feature);
                };
                button.KeyDown += (_, args) => NavigateFeatureIcons(feature.Id, args);
                EnableFeatureDrop(button, content, feature);
                Grid.SetColumnSpan(content.Children[^1], content.ColumnDefinitions.Count);
                Grid.SetRowSpan(content.Children[^1], 1);
                Grid.SetRow(button, row); Grid.SetColumn(button, column * span); Grid.SetColumnSpan(button, span);
                grid.Children.Add(button); view.Cells.Add(feature.Id, new(button, logicalRow + row, column * span, span));
            }
            logicalRow += (section.Features.Length + count - 1) / count + 1;
            var body = new Border { Child = grid, IsVisible = header?.IsExpanded ?? true };
            var container = new Grid { RowDefinitions = new("Auto,Auto") };
            Grid.SetRow(body, 1); if (header is not null) container.Children.Add(header); container.Children.Add(body);
            var group = new FeatureGroup(header, container, body, section.Title);
            view.Groups.Add(group); stack.Children.Add(container);
            if (header is null) continue;
            if (!header.IsExpanded) container.RowDefinitions[1].Height = new GridLength(0);
            header.Click += (_, _) => header.IsExpanded = !header.IsExpanded;
            header.PropertyChanged += (_, change) =>
            {
                if (change.Property == CategoryHeader.IsExpandedProperty) ToggleFeatureGroup(view, group, key);
            };
            header.KeyDown += (_, args) =>
            {
                if (args.KeyModifiers != KeyModifiers.None) return;
                if (args.Key is Key.Left or Key.Right)
                {
                    header.IsExpanded = args.Key == Key.Right; args.Handled = true;
                }
                else if (args.Key == Key.Escape)
                {
                    _categoryHeaders[_category].Focus(NavigationMethod.Directional); args.Handled = true;
                }
            };
        }
        _categoryViews[category] = view; FeatureViewHost.Children.Add(scroll); return view;
    }

    private void FeatureLanguageChanged(object? sender, EventArgs args)
    {
        if (_closing || _categoryHeaders.Count == 0) return;
        foreach (var (category, header) in _categoryHeaders)
            header.SetTitle(Localization.Text(FeatureNavigation.CategoryTitle(category)));
        foreach (var view in _categoryViews.Values)
        {
            foreach (var group in view.Groups)
                if (group.Title.Length > 0) group.Header.SetTitle(Localization.Text(group.Title));
            foreach (var (id, cell) in view.Cells)
            {
                if (!_featureLabels.TryGetValue(cell.Button, out var label)) continue;
                var text = Localization.Text(Catalog.Find(id).Label);
                label.Text = text;
                AutomationProperties.SetName(cell.Button, text);
                ToolTip.SetTip(cell.Button, text);
            }
        }
    }

    private void SetFeatureSelection(Button button, string id)
    {
        var selected = _selectedFeatureId == id;
        button.Classes.Set("selected", selected);
        button.Bind(BackgroundProperty, new DynamicResourceExtension(selected ? "UiSelection"
            : button.Classes.Contains("feature-format") ? "UiSurface" : "UiFeaturePanel"));
        button.Bind(ForegroundProperty, new DynamicResourceExtension(selected ? "UiSelectionText" : "UiText"));
        button.Bind(BorderBrushProperty, new DynamicResourceExtension(selected ? "UiAccent"
            : button.Classes.Contains("feature-format") ? "UiTileBorder" : "UiFeaturePanel"));
    }

    private void ToggleFeatureGroup(CategoryView view, FeatureGroup group, string key)
    {
        if (group.Header is null) return;
        var expanded = group.Header.IsExpanded;
        if (expanded) _closedFeatureGroups.Remove(key); else _closedFeatureGroups.Add(key);
        var enabled = AnimateNavigation && view.Scroll.IsVisible;
        var before = view.Groups.Where(item => item.Container.IsEffectivelyVisible)
            .ToDictionary(item => item.Container, item => item.Container.TranslatePoint(default, view.Stack) ?? default);
        foreach (var container in before.Keys) _navigationMotion.Cancel(container);
        var height = group.Body.Bounds.Height;
        if (expanded)
        {
            group.Body.IsVisible = true; group.Body.IsEnabled = true; group.Body.IsHitTestVisible = true;
            group.Body.Height = double.NaN; group.Container.RowDefinitions[1].Height = GridLength.Auto;
            group.Container.ZIndex = 0;
            _navigationMotion.Animate(group.Body, 160, from: new Vector(0, -3), fromOpacity: 0, enabled: enabled);
        }
        else
        {
            if (group.Body.IsKeyboardFocusWithin) group.Header.Focus(NavigationMethod.Directional);
            group.Body.IsEnabled = false; group.Body.IsHitTestVisible = false;
            group.Body.Height = height; group.Container.RowDefinitions[1].Height = new GridLength(0);
            group.Container.ZIndex = 1;
            _navigationMotion.Animate(group.Body, 75, new Vector(0, -3), 0, enabled: enabled, completed: () =>
            {
                if (group.Header.IsExpanded) return;
                group.Body.IsVisible = false; group.Body.Height = double.NaN; group.Container.ZIndex = 0;
            });
        }
        view.Stack.UpdateLayout();
        foreach (var (container, point) in before)
        {
            var next = container.TranslatePoint(default, view.Stack) ?? point;
            var offset = new Vector(point.X - next.X, point.Y - next.Y);
            if (offset.Length > 0.5)
                _navigationMotion.Animate(container, 180, from: offset, enabled: enabled);
        }
    }

    private void RestoreFeatureOffset(CategoryView view, int revision, string? focusedFeature = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || revision != _categoryRevision || _toolPanelHidden || !view.Scroll.IsVisible) return;
            view.Scroll.Offset = _categoryOffsets.GetValueOrDefault(_category);
            if (focusedFeature is not null && _featureButtons.TryGetValue(focusedFeature, out var button)) button.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    private (int Tools, int Formats) FeatureColumns()
    {
        var width = FeatureViewHost.Bounds.Width;
        if (width <= 0) width = _expandedSidebarWidth - 54 - 6 - FeaturePanel.Padding.Left - FeaturePanel.Padding.Right;
        var available = width - 16;
        return (available >= 560 ? 3 : available >= 300 ? 2 : 1, available >= 340 ? 6 : available >= 188 ? 4 : 3);
    }

    private void RefreshFeatureMetrics()
    {
        if (_categoryHeaders.Count == 0 || _toolPanelHidden || !_categoryViews.TryGetValue(_category, out var view)
            || (view.Tools, view.Formats) == FeatureColumns() || _featureRefreshQueued) return;
        _featureRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _featureRefreshQueued = false;
            if (_closing || _toolPanelHidden) return;
            var focused = _featureButtons.FirstOrDefault(item => item.Value.IsKeyboardFocusWithin).Key;
            ShowCategory(_category);
            if (focused is not null) RestoreFeatureOffset(_categoryViews[_category], _categoryRevision, focused);
        }, DispatcherPriority.Loaded);
    }

    private void RememberCategoryOffset()
    {
        if (!_toolPanelHidden && _categoryViews.TryGetValue(_category, out var view) && view.Scroll.IsVisible)
            _categoryOffsets[_category] = view.Scroll.Offset;
    }

    private void CollapseCategory() => SetToolPanelHidden(true);

    private void SetToolPanelHidden(bool hidden)
    {
        if (_toolPanelHidden == hidden) return;
        RememberCategoryOffset();
        var enabled = AnimateNavigation;
        var before = TaskWorkspace.TranslatePoint(default, WorkspaceLayout) ?? default;
        var panelWidth = FeaturePanel.Bounds.Width;
        _navigationMotion.Cancel(TaskWorkspace);
        _toolPanelHidden = hidden;
        var revision = ++_panelRevision;
        var column = WorkspaceLayout.ColumnDefinitions[0];
        if (hidden)
        {
            _expandedSidebarWidth = Math.Max(column.MinWidth, FeatureSidebar.Bounds.Width);
            if (FeaturePanel.IsKeyboardFocusWithin) SidebarToggle.Focus(NavigationMethod.Directional);
            FeaturePanel.IsEnabled = false; FeaturePanel.IsHitTestVisible = false;
            FeaturePanel.Width = panelWidth; FeaturePanel.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarLayout.ColumnDefinitions[1].Width = new GridLength(0);
            column.MinWidth = 54 + FeatureSidebar.Margin.Left + FeatureSidebar.Margin.Right;
            column.Width = new GridLength(column.MinWidth);
            _navigationMotion.Animate(FeaturePanel, 140, new Vector(-14, 0), 0, enabled: enabled, completed: () =>
            {
                if (revision != _panelRevision || !_toolPanelHidden) return;
                FeaturePanel.IsVisible = false; FeaturePanel.Width = double.NaN;
                FeaturePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            });
        }
        else
        {
            column.MinWidth = 260; column.Width = new GridLength(Math.Max(260, _expandedSidebarWidth));
            SidebarLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            FeaturePanel.Width = double.NaN; FeaturePanel.HorizontalAlignment = HorizontalAlignment.Stretch; FeaturePanel.IsVisible = true;
            FeaturePanel.IsEnabled = true; FeaturePanel.IsHitTestVisible = true;
            _navigationMotion.Animate(FeaturePanel, 200, from: new Vector(-14, 0), fromOpacity: 0, enabled: enabled);
        }
        SidebarSplitter.IsEnabled = !hidden;
        UpdateSidebarSelection();
        WorkspaceLayout.UpdateLayout();
        var after = TaskWorkspace.TranslatePoint(default, WorkspaceLayout) ?? before;
        _navigationMotion.Animate(TaskWorkspace, 220, from: new Vector(before.X - after.X, before.Y - after.Y), enabled: enabled);
        MoveCategoryIndicator(false);
        if (!hidden && _categoryViews.TryGetValue(_category, out var view)) RestoreFeatureOffset(view, _categoryRevision);
    }

    private void UpdateSidebarSelection()
    {
        _updatingCategories = true;
        try
        {
            foreach (var (category, header) in _categoryHeaders)
            {
                header.IsSelected = category == _category;
                header.IsExpanded = category == _category && !_toolPanelHidden;
            }
            SidebarToggle.SetPanelToggle(!_toolPanelHidden);
        }
        finally { _updatingCategories = false; }
    }

    private void MoveCategoryIndicator(bool enabled)
    {
        if (!_categoryHeaders.TryGetValue(_category, out var header)) return;
        var y = header.Bounds.Height > 0 ? header.Bounds.Y : Array.IndexOf(Catalog.Categories, _category) * 55;
        CategoryActiveIndicator.Width = Math.Max(1, Categories.Bounds.Width > 0 ? Categories.Bounds.Width : 44);
        CategoryActiveIndicator.Height = header.Bounds.Height > 0 ? header.Bounds.Height : 52;
        _navigationMotion.Animate(CategoryActiveIndicator, 170, new Vector(0, y), enabled: enabled);
    }

    private void NavigateFeatureIcons(string featureId, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None || !_categoryViews.TryGetValue(_category, out var view)) return;
        var visible = view.Cells.Values.Where(cell => cell.Button.IsEffectivelyVisible).ToArray();
        var index = Array.FindIndex(visible, cell => view.Cells.GetValueOrDefault(featureId) == cell);
        if (index < 0) return;
        var cell = visible[index]; var target = cell;
        switch (args.Key)
        {
            case Key.Escape:
                _categoryHeaders[_category].Focus(NavigationMethod.Directional); args.Handled = true; return;
            case Key.Up:
            case Key.Down:
                var rows = visible.Select(item => item.Row).Distinct().Order().ToArray();
                var rowIndex = Array.IndexOf(rows, cell.Row) + (args.Key == Key.Up ? -1 : 1);
                if (rowIndex < 0) { _categoryHeaders[_category].Focus(NavigationMethod.Directional); args.Handled = true; return; }
                if (rowIndex < rows.Length)
                    target = visible.Where(item => item.Row == rows[rowIndex])
                        .MinBy(item => Math.Abs(item.Column + item.Span / 2d - cell.Column - cell.Span / 2d))!;
                break;
            case Key.Left: if (index > 0 && visible[index - 1].Row == cell.Row) target = visible[index - 1]; break;
            case Key.Right: if (index + 1 < visible.Length && visible[index + 1].Row == cell.Row) target = visible[index + 1]; break;
            case Key.Home: target = visible[0]; break;
            case Key.End: target = visible[^1]; break;
            default: return;
        }
        target.Button.Focus(NavigationMethod.Directional); args.Handled = true;
    }

    private void NavigateCategories(string category, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None) return;
        var index = Array.IndexOf(Catalog.Categories, category);
        switch (args.Key)
        {
            case Key.Up: index = Math.Max(0, index - 1); break;
            case Key.Down: index = Math.Min(Catalog.Categories.Length - 1, index + 1); break;
            case Key.Home: index = 0; break;
            case Key.End: index = Catalog.Categories.Length - 1; break;
            case Key.Left:
            case Key.Escape: CollapseCategory(); args.Handled = true; return;
            case Key.Right:
                ShowCategory(category);
                _featureButtons.Values.FirstOrDefault(button => button.IsEffectivelyVisible)?.Focus(NavigationMethod.Directional);
                args.Handled = true; return;
            default: return;
        }
        var next = Catalog.Categories[index];
        _categoryHeaders[next].Focus(NavigationMethod.Directional); ShowCategory(next); args.Handled = true;
    }
}
