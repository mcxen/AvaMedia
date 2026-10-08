using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Path = Avalonia.Controls.Shapes.Path;

namespace AvaMedia.Desktop.Controls;

/// <summary>A category button that also exposes its expanded state to assistive tools.</summary>
public sealed class CategoryHeader : Button
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CategoryHeader, bool>(nameof(IsExpanded));
    private static readonly Geometry ClosedChevron = Geometry.Parse("M 3,1 L 8,6 L 3,11");
    private static readonly Geometry OpenChevron = Geometry.Parse("M 1,3 L 6,8 L 11,3");

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    protected override Type StyleKeyOverride => typeof(Button);

    public CategoryHeader(string category, string glyph)
    {
        Classes.Add("category");
        var content = new Grid { ColumnDefinitions = new("24,*,20") };
        content.Children.Add(new TextBlock
        {
            Text = glyph, Classes = { "muted-icon" }, VerticalAlignment = VerticalAlignment.Center
        });
        var title = new TextBlock
        {
            Text = category, Classes = { "category-title" }, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        var chevron = new Path
        {
            Data = ClosedChevron, Classes = { "category-chevron" }, Width = 12, Height = 12, StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        chevron.Bind(Path.StrokeProperty, new DynamicResourceExtension("UiTextSecondary"));
        Grid.SetColumn(chevron, 2);
        content.Children.Add(chevron);
        Content = content;
        ActualThemeVariantChanged += (_, _) => UpdateChevron();
        ToolTip.SetTip(this, category);
        AutomationProperties.SetLabeledBy(this, title);
        PropertyChanged += (_, change) =>
        {
            if (change.Property != IsExpandedProperty) return;
            Classes.Set("expanded", IsExpanded);
            UpdateChevron();
        };
        void UpdateChevron()
        {
            var classic = ActualThemeVariant == Skin.MacOS9;
            chevron.Data = classic ? Geometry.Parse(IsExpanded ? "M 1,3 L 11,3 L 6,9 Z" : "M 3,1 L 9,6 L 3,11 Z") : IsExpanded ? OpenChevron : ClosedChevron;
            chevron.StrokeThickness = classic ? 0 : 1.5;
            if (classic) chevron.Bind(Path.FillProperty, new DynamicResourceExtension("UiText"));
            else chevron.ClearValue(Path.FillProperty);
            RenderOptions.SetEdgeMode(chevron, classic ? EdgeMode.Aliased : EdgeMode.Unspecified);
        }
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
        private static ExpandCollapseState State(bool expanded) =>
            expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    }
}
