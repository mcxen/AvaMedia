using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, CategoryHeader> _categoryHeaders = [];
    private readonly Dictionary<(string Category, bool WindowsXP), Vector> _categoryOffsets = [];
    private readonly Dictionary<string, Button> _featureButtons = [];
    private bool _updatingCategories;
    private bool _windowsXPFeatures, _featureRefreshQueued;
    private int _featureColumns, _formatColumns;
    private int _categoryRevision;

    private void InitializeCategories()
    {
        FeatureScroll.SizeChanged += (_, _) => RefreshFeatureMetrics();
        for (var index = 0; index < Catalog.Categories.Length; index++)
        {
            var category = Catalog.Categories[index];
            Categories.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Categories.RowDefinitions.Add(new RowDefinition(0, GridUnitType.Pixel));
            var header = new CategoryHeader(FeatureNavigation.CategoryTitle(category), FeatureNavigation.CategoryIcon(category));
            _categoryHeaders.Add(category, header);
            Grid.SetRow(header, index * 2);
            Categories.Children.Add(header);
            header.Click += (_, _) => header.IsExpanded = !header.IsExpanded;
            header.PropertyChanged += (_, change) =>
            {
                if (_updatingCategories || change.Property != CategoryHeader.IsExpandedProperty) return;
                if (header.IsExpanded) ShowCategory(category);
                else if (_category == category) CollapseCategory();
            };
            header.KeyDown += (_, args) => NavigateCategories(category, args);
            EnableCategoryDropNavigation(header, category);
        }
        Categories.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
    }

    private void ShowCategory(string category)
    {
        if (_categoryHeaders.Count == 0) InitializeCategories();
        RememberCategoryOffset();
        _category = category;
        var revision = ++_categoryRevision;
        BuildFeatureGrid(category);
        ArrangeCategories(category);
        RestoreFeatureOffset(revision);
        Motion.Reveal(FeatureGrid);
    }

    private void BuildFeatureGrid(string category)
    {
        _windowsXPFeatures = ActualThemeVariant == Skin.WindowsXP;
        FeatureGrid.Children.Clear();
        _featureButtons.Clear();
        FeatureGrid.RowDefinitions.Clear();
        FeatureGrid.ColumnDefinitions.Clear();
        // Twelve tracks support 2 / 3 / 4 / 6 equal-width tiles in each section.
        for (var index = 0; index < 12; index++)
            FeatureGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        FeatureGrid.Margin = _windowsXPFeatures ? new Thickness(6) : new Thickness(12);
        FeatureScroll.Margin = _windowsXPFeatures ? new Thickness(10, 0, 10, 6) : default;
        (_featureColumns, _formatColumns) = FeatureColumns();
        var sections = FeatureNavigation.Sections(category, _settings.EnableBetaFeatures).ToArray();
        var row = 0;
        foreach (var section in sections)
        {
            if (sections.Length > 1)
            {
                FeatureGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8,
                    Margin = new Thickness(3, row == 0 ? 0 : 8, 3, 4) };
                heading.Bind(MinHeightProperty, new DynamicResourceExtension("UiFeatureGroupHeight"));
                heading.Children.Add(new TextBlock { Text = section.Title, Classes = { "feature-group-title" },
                    VerticalAlignment = VerticalAlignment.Center });
                var divider = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center };
                divider.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiDivider"));
                Grid.SetColumn(divider, 1); heading.Children.Add(divider);
                Grid.SetRow(heading, row++); Grid.SetColumnSpan(heading, 12);
                FeatureGrid.Children.Add(heading);
            }
            var columns = section.Compact ? _formatColumns : _featureColumns;
            var span = 12 / columns;
            var column = 0;
            foreach (var feature in section.Features)
            {
                if (column == 0) FeatureGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var content = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 6 };
                var icon = new FeatureIcon { Kind = feature.Icon,
                    Label = feature.Icon is "pdf-text" or "pdf-docx" or "pdf-xlsx" or "text-pdf" ? feature.Format.ToUpperInvariant() : "",
                    HorizontalAlignment = HorizontalAlignment.Center };
                var iconResource = section.Compact ? "UiFormatIconSize" : "UiFeatureIconHeight";
                icon.Bind(HeightProperty, new DynamicResourceExtension(iconResource));
                icon.Bind(WidthProperty, new DynamicResourceExtension(iconResource));
                content.Children.Add(icon);
                var text = new TextBlock
                {
                    Text = section.Compact ? feature.Format.ToUpperInvariant() : feature.Label,
                    TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2,
                    Classes = { "feature-label" }, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(text, 1); content.Children.Add(text);
                var tile = new Button { Name = "Feature_" + feature.Id.Replace('-', '_'), Content = content,
                    Margin = new Thickness(3), Classes = { "tile", "feature-tile" } };
                tile.Bind(MinHeightProperty, new DynamicResourceExtension(section.Compact ? "UiFormatRowHeight" : "UiFeatureRowHeight"));
                AutomationProperties.SetName(tile, feature.Label);
                ToolTip.SetTip(tile, feature.Label);
                tile.Click += async (_, _) => await Configure(feature);
                EnableFeatureDrop(tile, content, feature);
                tile.KeyDown += (_, args) => NavigateFeatureIcons(feature.Id, args);
                _featureButtons.Add(feature.Id, tile);
                Grid.SetColumn(tile, column * span); Grid.SetRow(tile, row); Grid.SetColumnSpan(tile, span);
                FeatureGrid.Children.Add(tile);
                column++;
                if (column == columns) { column = 0; row++; }
            }
            if (column != 0) row++;
        }
    }

    private void RestoreFeatureOffset(int revision, string? focusedFeature = null)
    {
        FeatureScroll.Offset = default;
        // Apply remembered scroll after layout; rapid switches must not restore an old category.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || revision != _categoryRevision || !FeatureScroll.IsVisible) return;
            FeatureScroll.Offset = _categoryOffsets.GetValueOrDefault((_category, _windowsXPFeatures));
            if (focusedFeature is not null && _featureButtons.TryGetValue(focusedFeature, out var button))
                button.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    private (int Tools, int Formats) FeatureColumns()
    {
        var width = FeatureScroll.Bounds.Width;
        if (width <= 0 && this.TryFindResource("UiSidebarWidth", out var value) && value is GridLength length)
            width = length.Value - FeatureScroll.Margin.Left - FeatureScroll.Margin.Right;
        var available = width - FeatureGrid.Margin.Left - FeatureGrid.Margin.Right - 16;
        var tools = available >= 420 ? 4 : available >= 280 ? 3 : 2;
        var formats = _windowsXPFeatures ? tools : available >= 420 ? 6 : available >= 280 ? 4 : 3;
        return (tools, formats);
    }

    private void RefreshFeatureMetrics()
    {
        if (_categoryHeaders.Count == 0 || !FeatureScroll.IsVisible) return;
        if (_windowsXPFeatures != (ActualThemeVariant == Skin.WindowsXP) || (_featureColumns, _formatColumns) != FeatureColumns())
        {
            if (_featureRefreshQueued) return;
            _featureRefreshQueued = true;
            // Rebuild after theme inheritance and sidebar layout have settled.
            Dispatcher.UIThread.Post(() =>
            {
                _featureRefreshQueued = false;
                if (_closing || !FeatureScroll.IsVisible || _windowsXPFeatures == (ActualThemeVariant == Skin.WindowsXP)
                    && (_featureColumns, _formatColumns) == FeatureColumns()) return;
                var focused = _featureButtons.FirstOrDefault(item => item.Value.IsKeyboardFocusWithin).Key;
                RememberCategoryOffset();
                var revision = ++_categoryRevision;
                BuildFeatureGrid(_category);
                RestoreFeatureOffset(revision, focused);
            }, DispatcherPriority.Loaded);
            return;
        }
    }

    private void CollapseCategory()
    {
        RememberCategoryOffset();
        _categoryRevision++;
        if (FeatureScroll.IsKeyboardFocusWithin) _categoryHeaders[_category].Focus();
        ArrangeCategories(null);
    }

    private void RememberCategoryOffset()
    {
        if (FeatureScroll.IsVisible && _categoryHeaders.Count != 0)
            _categoryOffsets[(_category, _windowsXPFeatures)] = FeatureScroll.Offset;
    }

    private void NavigateFeatureIcons(string featureId, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None) return;
        var keys = _featureButtons.Keys.ToArray();
        var index = Array.IndexOf(keys, featureId);
        if (index < 0) return;
        var current = _featureButtons[featureId];
        var row = Grid.GetRow(current);
        var column = Grid.GetColumn(current);
        var target = current;
        switch (args.Key)
        {
            case Key.Escape:
                _categoryHeaders[_category].Focus(NavigationMethod.Directional);
                args.Handled = true;
                return;
            case Key.Up:
            case Key.Down:
                var rows = _featureButtons.Values.Select(Grid.GetRow).Distinct().Order().ToArray();
                var rowIndex = Array.IndexOf(rows, row) + (args.Key == Key.Up ? -1 : 1);
                if (rowIndex < 0)
                {
                    _categoryHeaders[_category].Focus(NavigationMethod.Directional);
                    args.Handled = true;
                    return;
                }
                if (rowIndex < rows.Length)
                {
                    var center = column + Grid.GetColumnSpan(current) / 2d;
                    target = _featureButtons.Values.Where(button => Grid.GetRow(button) == rows[rowIndex])
                        .MinBy(button => Math.Abs(Grid.GetColumn(button) + Grid.GetColumnSpan(button) / 2d - center))!;
                }
                break;
            case Key.Left:
                if (index > 0 && Grid.GetRow(_featureButtons[keys[index - 1]]) == row) target = _featureButtons[keys[index - 1]];
                break;
            case Key.Right:
                if (index + 1 < keys.Length && Grid.GetRow(_featureButtons[keys[index + 1]]) == row) target = _featureButtons[keys[index + 1]];
                break;
            case Key.Home: target = _featureButtons[keys[0]]; break;
            case Key.End: target = _featureButtons[keys[^1]]; break;
            default: return;
        }
        target.Focus(NavigationMethod.Directional);
        args.Handled = true;
    }

    private void ArrangeCategories(string? expanded)
    {
        _updatingCategories = true;
        try
        {
            for (var index = 0; index < Catalog.Categories.Length; index++)
            {
                var category = Catalog.Categories[index];
                var active = category == expanded;
                _categoryHeaders[category].IsExpanded = active;
                Categories.RowDefinitions[index * 2 + 1].Height = active
                    ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                if (active)
                {
                    Grid.SetRow(FeatureScroll, index * 2 + 1);
                    // Logical order must follow the visible order for Tab navigation.
                    if (Categories.Children.IndexOf(FeatureScroll) != index + 1)
                    {
                        Categories.Children.Remove(FeatureScroll);
                        Categories.Children.Insert(index + 1, FeatureScroll);
                    }
                }
            }
            Categories.RowDefinitions[^1].Height = expanded is null
                ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            FeatureScroll.IsVisible = expanded is not null;
            FeatureGrid.IsVisible = expanded is not null;
        }
        finally { _updatingCategories = false; }
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
            case Key.Left: _categoryHeaders[category].IsExpanded = false; args.Handled = true; return;
            case Key.Right: _categoryHeaders[category].IsExpanded = true; args.Handled = true; return;
            default: return; // Enter / Space and Tab keep the standard button behavior.
        }
        _categoryHeaders[Catalog.Categories[index]].Focus(NavigationMethod.Directional);
        args.Handled = true;
    }
}
