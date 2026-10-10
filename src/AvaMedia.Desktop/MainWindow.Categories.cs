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
    private int _categoryRevision;

    private void InitializeCategories()
    {
        for (var index = 0; index < Catalog.Categories.Length; index++)
        {
            var category = Catalog.Categories[index];
            Categories.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Categories.RowDefinitions.Add(new RowDefinition(0, GridUnitType.Pixel));
            var header = new CategoryHeader(category, category switch
            {
                "视频" => "▣", "音频" => "♫", "图片" => "▧", "文档" => "▤",
                "工具集" => "⚙", _ => "◉"
            });
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
        var columns = _windowsXPFeatures ? 3 : 4;
        FeatureGrid.Children.Clear();
        _featureButtons.Clear();
        FeatureGrid.RowDefinitions.Clear();
        FeatureGrid.ColumnDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            FeatureGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        FeatureGrid.Margin = _windowsXPFeatures ? new Thickness(6) : new Thickness(12);
        FeatureScroll.Margin = _windowsXPFeatures ? new Thickness(10, 0, 10, 6) : default;
        int column = 0, row = 0;
        foreach (var feature in Catalog.All.Where(feature => feature.Category == category && (!Catalog.IsBeta(feature) || _settings.EnableBetaFeatures)))
        {
            var span = _windowsXPFeatures ? 1 : feature.Span;
            if (column + span > columns) { column = 0; row++; }
            while (FeatureGrid.RowDefinitions.Count <= row)
                FeatureGrid.RowDefinitions.Add(new RowDefinition(FeatureRowHeight, GridUnitType.Pixel));
            var content = _windowsXPFeatures
                ? new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 4 }
                : new Grid { RowDefinitions = new("*,Auto") };
            var icon = new FeatureIcon { Kind = feature.Icon, Label = feature.Id == "mp4" ? "" : feature.Format.ToUpperInvariant() };
            icon.Bind(HeightProperty, new DynamicResourceExtension("UiFeatureIconHeight"));
            if (_windowsXPFeatures)
            {
                icon.Bind(WidthProperty, new DynamicResourceExtension("UiFeatureIconHeight"));
                icon.HorizontalAlignment = HorizontalAlignment.Center;
            }
            content.Children.Add(icon);
            var text = new TextBlock
            {
                Text = feature.Label, TextWrapping = TextWrapping.Wrap,
                Classes = { "feature-label" }, Margin = new(1, 0),
                VerticalAlignment = _windowsXPFeatures ? VerticalAlignment.Top : VerticalAlignment.Bottom
            };
            Grid.SetRow(text, 1);
            content.Children.Add(text);
            var tile = new Button { Name = "Feature_" + feature.Id.Replace('-', '_'), Content = content,
                Margin = new Thickness(3), Classes = { "tile" } };
            AutomationProperties.SetName(tile, feature.Label);
            ToolTip.SetTip(tile, feature.Label);
            tile.Click += async (_, _) => await Configure(feature);
            EnableFeatureDrop(tile, content, feature);
            if (_windowsXPFeatures) tile.KeyDown += (_, args) => NavigateFeatureIcons(feature.Id, args);
            _featureButtons.Add(feature.Id, tile);
            Grid.SetColumn(tile, column);
            Grid.SetRow(tile, row);
            Grid.SetColumnSpan(tile, span);
            FeatureGrid.Children.Add(tile);
            column += span;
            if (column == columns) { column = 0; row++; }
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

    private double FeatureRowHeight => this.TryFindResource("UiFeatureRowHeight", out var value) && value is double height ? height : 91;
    private void RefreshFeatureMetrics()
    {
        if (_windowsXPFeatures != (ActualThemeVariant == Skin.WindowsXP))
        {
            if (_featureRefreshQueued) return;
            _featureRefreshQueued = true;
            // Rebuild after theme inheritance has settled, outside the skin-switch event.
            Dispatcher.UIThread.Post(() =>
            {
                _featureRefreshQueued = false;
                if (_closing || _windowsXPFeatures == (ActualThemeVariant == Skin.WindowsXP)) return;
                var focused = _featureButtons.FirstOrDefault(item => item.Value.IsKeyboardFocusWithin).Key;
                RememberCategoryOffset();
                var revision = ++_categoryRevision;
                BuildFeatureGrid(_category);
                RestoreFeatureOffset(revision, focused);
            }, DispatcherPriority.Loaded);
            return;
        }
        foreach (var row in FeatureGrid.RowDefinitions) row.Height = new GridLength(FeatureRowHeight);
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
        var columns = FeatureGrid.ColumnDefinitions.Count;
        switch (args.Key)
        {
            case Key.Up when index < columns:
            case Key.Left when index % columns == 0:
            case Key.Escape:
                _categoryHeaders[_category].Focus(NavigationMethod.Directional);
                args.Handled = true;
                return;
            case Key.Up: index -= columns; break;
            case Key.Down:
                if (index / columns < (keys.Length - 1) / columns)
                    index = Math.Min(keys.Length - 1, index + columns);
                break;
            case Key.Left: index--; break;
            case Key.Right:
                if (index % columns < columns - 1 && index + 1 < keys.Length) index++;
                break;
            case Key.Home: index = 0; break;
            case Key.End: index = keys.Length - 1; break;
            default: return;
        }
        _featureButtons[keys[index]].Focus(NavigationMethod.Directional);
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
