using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

/// <summary>通用表面与内容角色。组件外边距由宿主布局决定。</summary>
public class Card : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    public Card() => Classes.Add("ac-card");
}
public class ListRow : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    public ListRow() => Classes.Add("ac-list-row");
}
public class SectionHeader : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);
    public SectionHeader() => Classes.Add("ac-section");
}

public sealed class StatusPill : Border
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<StatusPill, string>(nameof(Text), "");
    public static readonly StyledProperty<string> StateProperty = AvaloniaProperty.Register<StatusPill, string>(nameof(State), "waiting");
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium };
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
    protected override Type StyleKeyOverride => typeof(Border);
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public StatusPill()
    {
        Classes.Add("ac-status");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; row.Children.Add(_dot); row.Children.Add(_text); Child = row;
        _text.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        BindResource(_text, TextBlock.FontSizeProperty, "AcCaptionFont");
        PropertyChanged += (_, change) => { if (change.Property == StateProperty) Refresh(); };
        Refresh();
    }
    private void Refresh()
    {
        var (fill, ink) = State switch
        {
            "running" => ("AcRunningFill", "UiAccent"), "completed" or "done" => ("AcSuccessFill", "UiSuccess"),
            "paused" => ("AcWarningFill", "AcWarningText"), "failed" => ("AcDangerFill", "UiDanger"),
            _ => ("AcWaitingFill", "UiTextSecondary")
        };
        Bind(BackgroundProperty, Resource(fill)); BindResource(_text, TextBlock.ForegroundProperty, ink);
        BindResource(_dot, Avalonia.Controls.Shapes.Shape.FillProperty, ink); _dot.IsVisible = State != "cancelled";
    }
}

public sealed class EmptyState : StackPanel
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Text), "");
    public static readonly StyledProperty<string> DetailProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Detail), "");
    public static readonly StyledProperty<bool> IsErrorProperty = AvaloniaProperty.Register<EmptyState, bool>(nameof(IsError));
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 220 };
    private readonly StatusGlyph _icon = new() { State = "empty", Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 8, 0, 0) };
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public bool IsError { get => GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }
    public EmptyState()
    {
        BindResource(_icon, WidthProperty, "AcEmptyIconSize"); BindResource(_icon, HeightProperty, "AcEmptyIconSize");
        Classes.Add("ac-empty"); HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center; Spacing = 6;
        _text.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty)); _detail.Bind(TextBlock.TextProperty, this.GetObservable(DetailProperty));
        BindResource(_text, TextBlock.FontSizeProperty, "AcBodyFont"); BindResource(_text, TextBlock.ForegroundProperty, "UiText");
        BindResource(_detail, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_detail, TextBlock.ForegroundProperty, "UiTextSecondary");
        Children.Add(_icon); Children.Add(_text); Children.Add(_detail); Children.Add(Actions);
        PropertyChanged += (_, e) => { if (e.Property == IsErrorProperty) _icon.State = IsError ? "failed" : "empty"; if (e.Property == DetailProperty) _detail.IsVisible = Detail.Length > 0; };
        _detail.IsVisible = false;
    }
}
