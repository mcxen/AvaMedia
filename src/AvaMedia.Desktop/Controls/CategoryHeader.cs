using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Path = Avalonia.Controls.Shapes.Path;

namespace AvaMedia.Desktop.Controls;

/// <summary>Rail and section buttons expose their panel's expanded state to assistive tools.</summary>
public sealed class CategoryHeader : Button
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CategoryHeader, bool>(nameof(IsExpanded));
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<CategoryHeader, bool>(nameof(IsSelected));
    private readonly TextBlock _title;
    private readonly FeatureIcon? _icon;
    private readonly Path _chevron;
    private readonly bool _isGroup, _isTree;
    private readonly TextBlock? _count;
    private bool _isPanelToggle;

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    protected override Type StyleKeyOverride => typeof(Button);

    public CategoryHeader() : this("收起", "") { }

    /// <summary>Top-level row of the sidebar tool tree: chevron, line icon, title and tool count.</summary>
    public CategoryHeader(string title, string iconKind, int count) : this(title, iconKind, isGroup: false, isTree: true)
    {
        _count!.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void SetCount(int count) { if (_count is not null) _count.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture); }

    public CategoryHeader(string title, string iconKind, bool isGroup = false) : this(title, iconKind, isGroup, false) { }

    private CategoryHeader(string title, string iconKind, bool isGroup, bool isTree)
    {
        _isGroup = isGroup; _isTree = isTree;
        Classes.Add(isTree ? "feature-tree-category" : isGroup ? "feature-group-header" : "category-rail");
        this.Bind(ThemeProperty, new DynamicResourceExtension(isTree ? "UiTreeRowTheme" : isGroup ? "UiNavigationMenuTheme" : "UiNavigationRailTheme"));
        this.Bind(CornerRadiusProperty, new DynamicResourceExtension("UiNavigationButtonRadius"));
        Background = Brushes.Transparent; BorderBrush = Brushes.Transparent; BorderThickness = new(1);
        Padding = isGroup ? new Thickness(4, 4) : new Thickness(1, 4);
        Margin = default; MinHeight = isGroup ? 26 : 52; MinWidth = 0;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Center;
        _title = new TextBlock { Text = title, FontSize = isGroup ? 12 : 11,
            TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = isGroup ? TextAlignment.Left : TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        _chevron = new Path { Width = isGroup ? 11 : 18, Height = isGroup ? 11 : 18,
            StrokeThickness = 1.5, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsVisible = isGroup };
        _chevron.Bind(Path.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        if (isTree)
        {
            Padding = new Thickness(4, 0, 8, 0); MinHeight = 0; BorderThickness = default;
            _chevron.Classes.Add("tree-chevron");
            _title.FontSize = 13; _title.FontWeight = FontWeight.SemiBold; _title.TextAlignment = TextAlignment.Left;
            _chevron.Width = 10; _chevron.Height = 10; _chevron.IsVisible = true;
            _chevron.Bind(Path.StrokeProperty, new DynamicResourceExtension("UiTextSecondary"));
            var icon = new LucideIcon { Kind = iconKind, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiTreeIcon"));
            _count = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            _count.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
            var content = new Grid { ColumnDefinitions = new("14,Auto,*,Auto"), ColumnSpacing = 6 };
            content.Children.Add(_chevron);
            Grid.SetColumn(icon, 1); content.Children.Add(icon);
            Grid.SetColumn(_title, 2); content.Children.Add(_title);
            Grid.SetColumn(_count, 3); content.Children.Add(_count); Content = content;
        }
        else if (isGroup)
        {
            var content = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 6 };
            content.Children.Add(_title);
            var rule = new Border { Height = 1, Margin = new(2, 0), VerticalAlignment = VerticalAlignment.Center };
            rule.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiDivider"));
            Grid.SetColumn(rule, 1); content.Children.Add(rule);
            Grid.SetColumn(_chevron, 2); content.Children.Add(_chevron); Content = content;
        }
        else
        {
            _icon = new FeatureIcon { Kind = iconKind, Label = "", Width = 20, Height = 20,
                HorizontalAlignment = HorizontalAlignment.Center };
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(_icon); content.Children.Add(_chevron); content.Children.Add(_title); Content = content;
        }
        AutomationProperties.SetLabeledBy(this, _title);
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsExpandedProperty || change.Property == IsSelectedProperty) UpdateState();
        };
        UpdateState();
    }

    public void SetTitle(string title) { if (!_isPanelToggle) _title.Text = title; }

    public void SetPanelToggle(bool expanded)
    {
        _isPanelToggle = true;
        if (_icon is not null) _icon.IsVisible = false;
        _chevron.IsVisible = true;
        IsExpanded = expanded;
        UpdateState();
    }

    private void UpdateState()
    {
        Classes.Set("expanded", IsExpanded); Classes.Set("selected", IsSelected);
        this.Bind(ForegroundProperty, new DynamicResourceExtension(_isTree ? "UiText" : _isGroup ? "UiTextSecondary"
            : IsSelected && !_isPanelToggle ? "UiNavigationSelectionText" : "UiNavigationText"));
        if (_isPanelToggle)
        {
            _title.Text = IsExpanded ? "收起" : "展开";
            _chevron.Data = Geometry.Parse(IsExpanded ? "M 5,2 L 5,16 M 13,4 L 8,9 L 13,14" : "M 5,2 L 5,16 M 9,4 L 14,9 L 9,14");
        }
        else if (_isTree)
            _chevron.Data = Geometry.Parse(IsExpanded ? "M 1,3 L 5,7 L 9,3" : "M 3,1 L 7,5 L 3,9");
        else if (_isGroup)
            _chevron.Data = Geometry.Parse(IsExpanded ? "M 1,3 L 6,8 L 11,3" : "M 3,1 L 8,6 L 3,11");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CategoryHeaderPeer(this);

    private sealed class CategoryHeaderPeer : ButtonAutomationPeer, IExpandCollapseProvider
    {
        private readonly CategoryHeader _header;
        public CategoryHeaderPeer(CategoryHeader header) : base(header)
        {
            _header = header;
            header.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsExpandedProperty)
                    RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                        State((bool)change.OldValue!), State((bool)change.NewValue!));
            };
        }
        public ExpandCollapseState ExpandCollapseState => State(_header.IsExpanded);
        public bool ShowsMenu => false;
        public void Expand() { EnsureEnabled(); _header.IsExpanded = true; }
        public void Collapse() { EnsureEnabled(); _header.IsExpanded = false; }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Expander;
        private static ExpandCollapseState State(bool expanded) => expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    }
}
