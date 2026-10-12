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
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    protected override Type StyleKeyOverride => typeof(Border);
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public StatusPill()
    {
        Classes.Add("ac-status"); Child = _text;
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
            "paused" => ("AcWarningFill", "UiWarning"), "failed" => ("AcDangerFill", "UiDanger"),
            _ => ("AcWaitingFill", "UiTextSecondary")
        };
        Bind(BackgroundProperty, Resource(fill)); BindResource(_text, TextBlock.ForegroundProperty, ink);
    }
}

public sealed class EmptyState : StackPanel
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<EmptyState, string>(nameof(Text), "");
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public EmptyState()
    {
        HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
        Bind(SpacingProperty, Resource("AcRowGap"));
        // 圆形等待符号是结构性状态，不使用装饰插图。
        Children.Add(new StatusGlyph { State = "pending", HorizontalAlignment = HorizontalAlignment.Center });
        _text.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        BindResource(_text, TextBlock.FontSizeProperty, "AcBodyFont"); BindResource(_text, TextBlock.ForegroundProperty, "UiTextSecondary");
        Children.Add(_text);
    }
}
