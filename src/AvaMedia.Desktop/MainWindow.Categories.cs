using Avalonia;
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
    private readonly Dictionary<string, Vector> _categoryOffsets = [];
    private bool _updatingCategories;
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
        }
        Categories.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
    }

    private void ShowCategory(string category)
    {
        if (_categoryHeaders.Count == 0) InitializeCategories();
        RememberCategoryOffset();
        _category = category;
        var revision = ++_categoryRevision;
        FeatureGrid.Children.Clear();
        FeatureGrid.RowDefinitions.Clear();
        int column = 0, row = 0;
        foreach (var feature in Catalog.All.Where(feature => feature.Category == category && (feature.Id != "person-clip" || _settings.EnableBetaFeatures)))
        {
            if (column + feature.Span > 4) { column = 0; row++; }
            while (FeatureGrid.RowDefinitions.Count <= row)
                FeatureGrid.RowDefinitions.Add(new RowDefinition(FeatureRowHeight, GridUnitType.Pixel));
            var content = new Grid { RowDefinitions = new("*,Auto") };
            var icon = new FeatureIcon { Kind = feature.Icon, Label = feature.Id == "mp4" ? "" : feature.Format.ToUpperInvariant() };
            icon.Bind(HeightProperty, new DynamicResourceExtension("UiFeatureIconHeight"));
            content.Children.Add(icon);
            var text = new TextBlock
            {
                Text = feature.Label, TextWrapping = TextWrapping.Wrap,
                Margin = new(1, 0), VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(text, 1);
            content.Children.Add(text);
            var tile = new Button { Content = content, Margin = new(3), Classes = { "tile" } };
            ToolTip.SetTip(tile, feature.Label);
            tile.Click += async (_, _) => await Configure(feature);
            Grid.SetColumn(tile, column);
            Grid.SetRow(tile, row);
            Grid.SetColumnSpan(tile, feature.Span);
            FeatureGrid.Children.Add(tile);
            column += feature.Span;
            if (column == 4) { column = 0; row++; }
        }
        ArrangeCategories(category);
        FeatureScroll.Offset = default;
        // Apply remembered scroll after layout; rapid switches must not restore an old category.
        Dispatcher.UIThread.Post(() =>
        {
            if (revision != _categoryRevision || !FeatureScroll.IsVisible) return;
            FeatureScroll.Offset = _categoryOffsets.GetValueOrDefault(category);
        }, DispatcherPriority.Loaded);
        Motion.Reveal(FeatureGrid);
    }

    private double FeatureRowHeight => ActualThemeVariant == Skin.MacOS9 ? 74 : 91;
    private void RefreshFeatureMetrics()
    {
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
            _categoryOffsets[_category] = FeatureScroll.Offset;
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
