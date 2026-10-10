using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Compact queue progress with an expandable, shared processing flow.</summary>
public sealed class AiActivityView : Border
{
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<AiActivityView, bool>(nameof(Compact));
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public bool Collapsible { get; set; }
    public bool DetailsExpanded { get => _expanded; set { _expanded = value; RefreshExpansion(); } }
    public double DetailHeight { get => _detailScroll.MaxHeight; set => _detailScroll.MaxHeight = value; }
    private bool _expanded = true;
    private readonly Button _collapse = new() { Classes = { "tool" }, Padding = new(6, 2), IsVisible = false };
    private readonly TextBlock _title = Text();
    private readonly TextBlock _meta = Text();
    private readonly TextBlock _clock = Text();
    private readonly TextBlock _compactStage = Line();
    private readonly TextBlock _compactClock = Line();
    private readonly ProgressBar _compactProgress = new() { Maximum = 100, Height = 5 };
    private readonly Grid _compactTrack = new() { Height = 5 };
    private readonly AiActivityFlowView _flow = new();
    private readonly StackPanel _content = new() { Spacing = 10 };
    private readonly StackPanel _compactContent = new() { Spacing = 4 };
    private readonly ScrollViewer _detailScroll = new()
    {
        MaxHeight = 280, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };
    private readonly Flyout _flyout = new();
    private readonly Button _detailsButton = new() { Classes = { "tool" }, MinHeight = 20, Padding = new(5, 1) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AiActivity? _activity;
    private bool _attached;

    public AiActivityView()
    {
        IsVisible = false;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        Padding = new(10);
        _title.FontWeight = FontWeight.SemiBold;
        _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        var heading = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        heading.Children.Add(_title); Grid.SetColumn(_clock, 1); heading.Children.Add(_clock); Grid.SetColumn(_collapse, 2); heading.Children.Add(_collapse);
        _collapse.Click += (_, _) => { _expanded = !_expanded; RefreshExpansion(); };
        _content.Children.Add(heading); _content.Children.Add(_meta);
        _detailScroll.Content = _flow; _content.Children.Add(_detailScroll); Child = _content;
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        _compactClock.VerticalAlignment = VerticalAlignment.Center;
        _compactStage.Classes.Add("queue-state");
        footer.Children.Add(_compactClock); Grid.SetColumn(_detailsButton, 1); footer.Children.Add(_detailsButton);
        _detailsButton.Bind(Button.FontSizeProperty, new DynamicResourceExtension("UiFontCaption"));
        _detailsButton.Flyout = _flyout;
        _compactContent.Children.Add(_compactStage); _compactTrack.Children.Add(_compactProgress);
        _compactContent.Children.Add(_compactTrack); _compactContent.Children.Add(footer);
        _flyout.Opened += (_, _) => _flow.RevealCurrent();
        _timer.Tick += (_, _) => RefreshClock();
        AttachedToVisualTree += (_, _) => { _attached = true; Localization.Changed += LanguageChanged; Update(_activity); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Localization.Changed -= LanguageChanged; _timer.Stop(); _flyout.Hide(); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != CompactProperty) return;
        _flyout.Hide(); Child = null; _flyout.Content = null;
        _detailScroll.Width = Compact ? 520 : double.NaN;
        _detailScroll.MaxHeight = Compact ? 520 : 280;
        if (Compact)
        {
            Background = Brushes.Transparent; Padding = new(0);
            _flyout.Content = _content; Child = _compactContent;
        }
        else
        {
            Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
            Padding = new(10); Child = _content;
        }
        Update(_activity);
    }
    private void LanguageChanged(object? sender, EventArgs args) => Update(_activity);
    internal static TextBlock Text()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        Localization.SetIsUserText(text, true);
        return text;
    }
    private static TextBlock Line()
    {
        var text = Text(); text.TextWrapping = TextWrapping.NoWrap; text.MaxLines = 1;
        text.TextTrimming = TextTrimming.CharacterEllipsis; text.Classes.Add("queue-secondary");
        return text;
    }
    internal static bool Quantified(AiActivity activity) => activity.Total is > 0 && activity.Current is { } current
        && double.IsFinite(current) && double.IsFinite(activity.Total.Value);
    internal static string Count(AiActivity activity) => !Quantified(activity) ? "" : activity.Unit switch
    {
        "秒" => $"{AiActivity.FormatElapsed(activity.Current!.Value)} / {AiActivity.FormatElapsed(activity.Total!.Value)}",
        "字节" => $"{Size(activity.Current!.Value)} / {Size(activity.Total!.Value)}",
        "%" => $"{activity.Current:0}%",
        _ => $"{activity.Current:0} / {activity.Total:0} {Localization.Text(activity.Unit)}"
    };
    private static string Size(double bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824:0.00} GiB" : $"{bytes / 1048576:0.0} MiB";
    public void Update(AiActivity? activity)
    {
        _activity = activity; IsVisible = activity is not null;
        if (activity is null) { _timer.Stop(); _flyout.Hide(); _flow.Clear(); return; }
        _title.Text = Localization.Text("任务进度");
        _meta.Text = string.Join(" · ", new[] { Localization.Text(activity.Model), activity.Backend }.Where(text => text.Length > 0));
        _compactStage.Text = string.Join(" · ", new[] { Localization.Text(activity.Stage), Count(activity) }.Where(text => text.Length > 0));
        ToolTip.SetTip(_compactStage, _compactStage.Text);
        _compactStage.IsVisible = activity.State is AiActivityState.Running or AiActivityState.Paused;
        _compactTrack.IsVisible = activity.State == AiActivityState.Running;
        foreach (var state in Enum.GetValues<AiActivityState>()) _compactStage.Classes.Set(state.ToString().ToLowerInvariant(), state == activity.State);
        _compactProgress.IsVisible = Quantified(activity);
        _compactProgress.Value = Quantified(activity) ? Math.Clamp(activity.Current!.Value / activity.Total!.Value * 100, 0, 100) : 0;
        _detailsButton.Content = Localization.Text("详情"); ToolTip.SetTip(_detailsButton, Localization.Text("进度详情"));
        _flow.Update(activity); RefreshClock(); RefreshExpansion();
        if (_attached && activity.State == AiActivityState.Running) _timer.Start(); else _timer.Stop();
    }
    private void RefreshExpansion()
    {
        _collapse.IsVisible = Collapsible && !Compact;
        _collapse.Content = Localization.Text(_expanded ? "收起" : "展开");
        _detailScroll.IsVisible = !Collapsible || _expanded;
        _meta.IsVisible = !Collapsible || !_expanded;
    }
    public void Finish(AiActivityState state, string stage)
    {
        if (_activity is { } activity) Update(activity with { State = state, Stage = stage, UpdatedUtc = DateTime.UtcNow });
    }
    private void RefreshClock()
    {
        if (_activity is not { } activity) return;
        var now = activity.State == AiActivityState.Running ? DateTime.UtcNow : activity.UpdatedUtc;
        var elapsed = AiActivity.FormatElapsed((now - activity.StartedUtc).TotalSeconds);
        var quiet = Math.Max(0, (now - activity.UpdatedUtc).TotalSeconds);
        _clock.Text = Localization.Format($"已用时 {elapsed}") + (activity.State == AiActivityState.Running && quiet >= 10
            ? " · " + Localization.Format($"最近进展 {quiet:0} 秒前") : "");
        _compactClock.Text = _clock.Text; ToolTip.SetTip(_compactClock, _clock.Text);
        _flow.RefreshClock(now);
    }
}
