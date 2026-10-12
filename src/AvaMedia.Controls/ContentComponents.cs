using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

public sealed class CountBadge : Border
{
    private readonly TextBlock _label = new() { FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    protected override Type StyleKeyOverride => typeof(Border);
    public string Text { get => _label.Text ?? ""; set => _label.Text = value; }
    public CountBadge()
    {
        Classes.Add("ac-badge"); Child = _label;
        BindResource(_label, TextBlock.FontSizeProperty, "AcSubStepFont");
        _label.FontFeatures = new() { FontFeature.Parse("tnum") };
    }
}

/// <summary>复用按钮角色；主动作和下拉动作分别保留命令、键盘和无障碍。</summary>
public sealed class SplitButton : Border
{
    public Button ActionButton { get; } = new() { Classes = { "ac" } };
    public Button MenuButton { get; } = new() { Classes = { "ac", "split-arrow" } };
    protected override Type StyleKeyOverride => typeof(Border);
    public SplitButton()
    {
        Classes.Add("ac-split");
        var row = new Grid { ColumnDefinitions = new("*,Auto") };
        MenuButton.Content = new TextBlock { Text = "⌄", FontSize = 16 }; ToolTip.SetTip(MenuButton, "更多操作");
        Avalonia.Automation.AutomationProperties.SetName(MenuButton, "更多操作");
        row.Children.Add(ActionButton); Grid.SetColumn(MenuButton, 1); row.Children.Add(MenuButton); Child = row;
    }
    public void SetPrimary(bool primary)
    {
        ActionButton.Classes.Set("primary", primary); MenuButton.Classes.Set("primary", primary);
    }
}

public sealed class Notice : Border
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<Notice, string>(nameof(Title), "");
    public static readonly StyledProperty<string> DetailProperty = AvaloniaProperty.Register<Notice, string>(nameof(Detail), "");
    public static readonly StyledProperty<string> StateProperty = AvaloniaProperty.Register<Notice, string>(nameof(State), "info");
    public static readonly StyledProperty<object?> ActionContentProperty = AvaloniaProperty.Register<Notice, object?>(nameof(ActionContent));
    public object? ActionContent { get => GetValue(ActionContentProperty); set => SetValue(ActionContentProperty, value); }
    private readonly Border _line = new() { Width = 3, Margin = new(-12, -10, 0, -10) };
    private readonly StatusGlyph _icon = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 0, 0) };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold }, _detail = new() { TextWrapping = TextWrapping.Wrap };
    protected override Type StyleKeyOverride => typeof(Border);
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public string State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public Notice()
    {
        BindResource(_icon, WidthProperty, "AcNoticeIconSize"); BindResource(_icon, HeightProperty, "AcNoticeIconSize");
        Classes.Add("ac-notice"); var row = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(_line); Grid.SetColumn(_icon, 1); row.Children.Add(_icon);
        var text = new StackPanel(); text.Children.Add(_title); text.Children.Add(_detail); Grid.SetColumn(text, 2); row.Children.Add(text);
        var action = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center }; action.Bind(ContentPresenter.ContentProperty, this.GetObservable(ActionContentProperty)); Grid.SetColumn(action, 3); row.Children.Add(action); Child = row;
        _title.Bind(TextBlock.TextProperty, this.GetObservable(TitleProperty)); _detail.Bind(TextBlock.TextProperty, this.GetObservable(DetailProperty));
        BindResource(_title, TextBlock.FontSizeProperty, "AcBodyFont"); BindResource(_detail, TextBlock.FontSizeProperty, "AcCaptionFont");
        PropertyChanged += (_, e) => { if (e.Property == StateProperty) Refresh(); }; Refresh();
    }
    private void Refresh()
    {
        Classes.Set("warning", State == "warning");
        BindResource(_line, BackgroundProperty, State switch { "completed" => "UiSuccessFill", "failed" => "UiDanger", "warning" => "UiWarning", _ => "UiAccent" });
        _icon.State = State switch { "completed" => "success", "failed" => "failed", "warning" => "warning", _ => "info" };
        BindResource(_title, TextBlock.ForegroundProperty, State == "warning" ? "AcBannerText" : "UiText");
        BindResource(_detail, TextBlock.ForegroundProperty, State == "warning" ? "AcBannerText" : "UiTextSecondary");
    }
}

public sealed class Popover : ContentControl
{
    protected override Type StyleKeyOverride => typeof(ContentControl);
    public Popover() => Classes.Add("ac-popover");
}
