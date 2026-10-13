using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

/// <summary>
/// Sidebar tool tree: one category open at a time (accordion), groups as small headers,
/// tools as icon rows and audio / image targets as a compact chip group.
/// </summary>
public partial class MainWindow
{
    private sealed record TreeCategory(CategoryHeader Header, StackPanel Body);
    private readonly Dictionary<string, TreeCategory> _treeCategories = [];
    private readonly Dictionary<string, Button> _featureButtons = [];
    private readonly Dictionary<Button, LucideIcon> _featureIcons = [];
    private bool _treeBeta, _treeBuilt, _treeExclusiveUnlocked;
    private string? _selectedFeatureId;

    private void BuildFeatureTree()
    {
        _treeBeta = _settings.EnableBetaFeatures;
        _treeExclusiveUnlocked = _settings.ExclusiveToolUnlocked;
        var focused = _featureButtons.FirstOrDefault(item => item.Value.IsKeyboardFocusWithin).Key;
        ResetOutputDropSession();
        FeatureTree.Children.Clear(); _treeCategories.Clear(); _featureButtons.Clear(); _featureIcons.Clear();
        foreach (var category in FeatureNavigation.Categories)
        {
            var sections = FeatureNavigation.Sections(category, _treeBeta, _treeExclusiveUnlocked).ToArray();
            var header = new CategoryHeader(Localization.Text(FeatureNavigation.CategoryTitle(category)),
                FeatureNavigation.CategoryIcon(category), sections.Sum(section => section.Features.Length))
            { Name = "Category_" + Array.IndexOf(FeatureNavigation.Categories, category) };
            var body = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var section in sections) AddTreeSection(body, section);
            header.Click += (_, _) => ToggleCategory(category);
            header.PropertyChanged += (_, change) =>
            {
                // Automation (ExpandCollapse) writes IsExpanded directly.
                if (_updatingTree || change.Property != CategoryHeader.IsExpandedProperty) return;
                if (header.IsExpanded) ShowCategory(category); else if (_category == category) CollapseCategories();
            };
            header.KeyDown += (_, args) => NavigateCategories(category, args);
            EnableCategoryDropNavigation(header, category);
            _treeCategories.Add(category, new(header, body));
            FeatureTree.Children.Add(header); FeatureTree.Children.Add(body);
        }
        _treeBuilt = true;
        ApplyCategoryState();
        if (focused is not null && _featureButtons.TryGetValue(focused, out var restore)) restore.Focus(NavigationMethod.Directional);
    }

    private void AddTreeSection(StackPanel body, FeatureSection section)
    {
        var title = new TextBlock { Text = Localization.Text(section.Title), FontSize = 11, FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(20, 4, 0, 0), Classes = { "feature-tree-group" } };
        title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        body.Children.Add(title);
        if (section.Formats)
        {
            var chips = new WrapPanel { Margin = new Thickness(30, 2, 4, 6), ItemSpacing = 4, LineSpacing = 4 };
            foreach (var feature in section.Features) chips.Children.Add(FormatChip(feature));
            body.Children.Add(chips);
            return;
        }
        foreach (var feature in section.Features) body.Children.Add(ToolRow(feature));
    }

    private Button ToolRow(Feature feature)
    {
        var label = FeatureNavigation.Label(feature);
        var content = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        var icon = new LucideIcon { Kind = FeatureNavigation.Icon(feature), Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
        icon.Classes.Add("non-platinum");
        content.Children.Add(icon);
        // Mac OS 9 shows its colored Platinum artwork instead of the line icon.
        content.Children.Add(new FeatureIcon { Kind = feature.Icon, Label = "", Width = 18, Height = 16, VerticalAlignment = VerticalAlignment.Center, Classes = { "platinum-only" } });
        var text = new TextBlock { Text = Localization.Text(label), FontSize = 13, MaxLines = 1, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        name.Children.Add(text);
        Grid.SetColumn(name, 1); content.Children.Add(name);
        if (Catalog.IsBeta(feature))
        {
            var badge = new Border { Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                Classes = { "beta-badge" }, Child = new TextBlock { Text = "BETA", [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("UiFontTiny"), FontWeight = FontWeight.Bold } };
            name.Children.Add(badge);
        }
        var button = TreeButton(feature, content, "UiTreeRowTheme");
        button.Classes.Add("feature-tree-row"); button.Padding = new Thickness(30, 0, 6, 0);
        _featureIcons.Add(button, icon);
        AutomationProperties.SetName(button, label);
        SetFeatureSelection(button, feature.Id);
        return button;
    }

    private Button FormatChip(Feature feature)
    {
        var content = new Grid();
        content.Children.Add(new TextBlock { Text = feature.Format.ToUpperInvariant(), HorizontalAlignment = HorizontalAlignment.Center });
        var button = TreeButton(feature, content, "UiFormatChipTheme");
        button.Classes.Add("feature-format");
        button.Padding = new Thickness(7, 2); button.FontSize = 11; button.FontWeight = FontWeight.Medium;
        button.CornerRadius = new CornerRadius(6); button.BorderThickness = new Thickness(1);
        button.Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        button.Bind(BorderBrushProperty, new DynamicResourceExtension("UiDivider"));
        AutomationProperties.SetName(button, "→ " + feature.Format.ToUpperInvariant());
        SetFeatureSelection(button, feature.Id);
        return button;
    }

    private Button TreeButton(Feature feature, Grid content, string theme)
    {
        var button = new Button { Name = "Feature_" + feature.Id.Replace('-', '_'), Content = content,
            Classes = { "feature-menu" }, MinHeight = 0, MinWidth = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center };
        if (theme == "UiTreeRowTheme")
        {
            button.Background = Brushes.Transparent; button.BorderThickness = default; button.FontSize = 13;
            button.Bind(CornerRadiusProperty, new DynamicResourceExtension("UiNavigationButtonRadius"));
        }
        button.Bind(ThemeProperty, new DynamicResourceExtension(theme));
        if (theme == "UiTreeRowTheme") button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Click += async (_, _) =>
        {
            await Configure(feature);
        };
        button.KeyDown += (_, args) => NavigateFeatureRows(feature.Id, args);
        if (feature.Id == FeatureNavigation.ExclusiveRenamer.Id)
        {
            DragDrop.SetAllowDrop(button, true);
            button.AddHandler(DragDrop.DragOverEvent, (_, args) => { args.DragEffects = DragDropEffects.None; args.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
            button.AddHandler(DragDrop.DropEvent, (_, args) => { args.DragEffects = DragDropEffects.None; args.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        else EnableFeatureDrop(button, content, feature);
        _featureButtons.Add(feature.Id, button);
        return button;
    }

    // The tool last opened (tree click, routing, drag-drop) keeps the blue selected row.
    private void MarkFeatureSelected(string id)
    {
        _selectedFeatureId = id;
        foreach (var active in _featureButtons) SetFeatureSelection(active.Value, active.Key);
    }

    private void SetFeatureSelection(Button button, string id)
    {
        var selected = _selectedFeatureId == id;
        button.Classes.Set("selected", selected);
        button.Bind(ForegroundProperty, new DynamicResourceExtension(selected ? "UiTreeSelectionText" : "UiText"));
        if (_featureIcons.TryGetValue(button, out var icon))
            icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension(selected ? "UiTreeSelectionText" : "UiTreeIcon"));
    }

    private bool _updatingTree;

    private void ShowCategory(string category)
    {
        category = FeatureNavigation.TreeCategory(category);
        if (!FeatureNavigation.Categories.Contains(category)) return;
        _category = category;
        if (!_treeBuilt) InitializeCategories();
        if (!_treeBuilt || _treeBeta != _settings.EnableBetaFeatures || _treeExclusiveUnlocked != _settings.ExclusiveToolUnlocked) BuildFeatureTree();
        else ApplyCategoryState();
    }

    private void ToggleCategory(string category)
    {
        if (_category == category) CollapseCategories(); else ShowCategory(category);
    }

    private void CollapseCategories() { _category = ""; ApplyCategoryState(); }

    private void ApplyCategoryState()
    {
        _updatingTree = true;
        try
        {
            foreach (var (category, item) in _treeCategories)
            {
                var expanded = category == _category;
                if (!expanded && item.Body.IsKeyboardFocusWithin) item.Header.Focus(NavigationMethod.Directional);
                item.Header.IsExpanded = expanded;
                item.Body.IsVisible = expanded;
            }
        }
        finally { _updatingTree = false; }
    }

    private void InitializeCategories()
    {
        Localization.Changed += FeatureLanguageChanged;
        Closed += (_, _) => Localization.Changed -= FeatureLanguageChanged;
    }

    private void FeatureLanguageChanged(object? sender, EventArgs args)
    {
        if (!_closing && _treeBuilt) BuildFeatureTree();
    }

    private void NavigateFeatureRows(string featureId, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None || !_treeCategories.TryGetValue(_category, out var tree)) return;
        var visible = _featureButtons.Where(item => item.Value.IsEffectivelyVisible).Select(item => item.Value).ToArray();
        var index = Array.IndexOf(visible, _featureButtons[featureId]);
        if (index < 0) return;
        Button? target = args.Key switch
        {
            Key.Up or Key.Left when index == 0 => null,
            Key.Up or Key.Left => visible[index - 1],
            Key.Down or Key.Right => visible[Math.Min(visible.Length - 1, index + 1)],
            Key.Home => visible[0], Key.End => visible[^1], _ => visible[index]
        };
        if (args.Key is not (Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.Escape)) return;
        (target ?? (Control)tree.Header).Focus(NavigationMethod.Directional);
        if (args.Key == Key.Escape) tree.Header.Focus(NavigationMethod.Directional);
        args.Handled = true;
    }

    private void NavigateCategories(string category, KeyEventArgs args)
    {
        if (args.KeyModifiers != KeyModifiers.None) return;
        var categories = FeatureNavigation.Categories;
        var index = Array.IndexOf(categories, category);
        switch (args.Key)
        {
            case Key.Up: index = Math.Max(0, index - 1); break;
            case Key.Down when _category == category:
                _featureButtons.Values.FirstOrDefault(button => button.IsEffectivelyVisible)?.Focus(NavigationMethod.Directional);
                args.Handled = true; return;
            case Key.Down: index = Math.Min(categories.Length - 1, index + 1); break;
            case Key.Home: index = 0; break;
            case Key.End: index = categories.Length - 1; break;
            case Key.Left: if (_category == category) CollapseCategories(); args.Handled = true; return;
            case Key.Right: ShowCategory(category); args.Handled = true; return;
            default: return;
        }
        _treeCategories[categories[index]].Header.Focus(NavigationMethod.Directional); args.Handled = true;
    }
}
