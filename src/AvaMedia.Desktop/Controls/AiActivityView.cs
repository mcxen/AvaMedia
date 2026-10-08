using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>One shared, compact disclosure for subtitle and visual inference progress.</summary>
public sealed class AiActivityView : Border
{
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<AiActivityView, bool>(nameof(Compact));
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    private readonly TextBlock _stage = Text();
    private readonly TextBlock _meta = Text();
    private readonly TextBlock _clock = Text();
    private readonly TextBlock _latest = Text();
    private readonly TextBlock _history = Text();
    private readonly TextBlock _caption = Text();
    private readonly TextBlock _expandedCaption = Text();
    private readonly TextBlock _compactStage = Line();
    private readonly TextBlock _compactClock = Line();
    private readonly ProgressBar _progress = new() { Maximum = 100, Height = 5 };
    private readonly ProgressBar _compactProgress = new() { Maximum = 100, Height = 5 };
    private readonly Grid _compactTrack = new() { Height = 5 };
    private readonly Image _image = new() { Width = 120, Height = 76, Stretch = Stretch.Uniform, IsVisible = false };
    private readonly Image _expandedImage = new() { Height = 140, Stretch = Stretch.Uniform, IsVisible = false };
    private readonly StackPanel _frame = new() { Width = 120, Spacing = 4, IsVisible = false };
    private readonly Expander _details = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _content = new() { Spacing = 4 };
    private readonly StackPanel _compactContent = new() { Spacing = 4 };
    private readonly ScrollViewer _detailScroll = new()
    {
        Width = 440, MaxHeight = 360,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };
    private readonly Flyout _flyout = new();
    private readonly Button _detailsButton = new() { Classes = { "tool" }, MinHeight = 20, Padding = new(5, 1) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AiActivity? _activity;
    private byte[]? _preview;
    private Bitmap? _bitmap;
    private bool _attached;

    public AiActivityView()
    {
        IsVisible = false;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        Bind(PaddingProperty, new DynamicResourceExtension("UiStatusPadding"));
        var overview = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        var summary = new StackPanel { Spacing = 4 };
        summary.Children.Add(_stage); summary.Children.Add(_progress);
        summary.Children.Add(_meta); summary.Children.Add(_clock); summary.Children.Add(_latest);
        overview.Children.Add(summary);
        _caption.MaxHeight = 36; _caption.TextTrimming = TextTrimming.CharacterEllipsis;
        _frame.Children.Add(_image); _frame.Children.Add(_caption); Grid.SetColumn(_frame, 1); overview.Children.Add(_frame);
        _content.Children.Add(overview);
        var observations = new StackPanel { Spacing = 6 };
        observations.Children.Add(_expandedImage); observations.Children.Add(_expandedCaption); observations.Children.Add(_history);
        _details.Content = new ScrollViewer { Content = observations, MaxHeight = 230, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _content.Children.Add(_details); Child = _content;
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        _compactClock.VerticalAlignment = VerticalAlignment.Center;
        _compactStage.Classes.Add("queue-state");
        footer.Children.Add(_compactClock); Grid.SetColumn(_detailsButton, 1); footer.Children.Add(_detailsButton);
        _detailsButton.Bind(Button.FontSizeProperty, new DynamicResourceExtension("UiFontCaption"));
        _detailsButton.Flyout = _flyout;
        _compactContent.Children.Add(_compactStage);
        // Reserve the track even during unquantified stages so updates do not move the row.
        _compactTrack.Children.Add(_compactProgress);
        _compactContent.Children.Add(_compactTrack); _compactContent.Children.Add(footer);
        _flyout.Opened += (_, _) => { _details.IsExpanded = true; RefreshPreview(); };
        _flyout.Closed += (_, _) => { _details.IsExpanded = false; if (Compact) ReleasePreview(); };
        _timer.Tick += (_, _) => RefreshClock();
        _details.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) RefreshPreview(); };
        AttachedToVisualTree += (_, _) => { _attached = true; Localization.Changed += LanguageChanged; Update(_activity); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Localization.Changed -= LanguageChanged; _timer.Stop(); _flyout.Hide(); ReleasePreview(); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != CompactProperty) return;
        _flyout.Hide(); Child = null; _detailScroll.Content = null; _flyout.Content = null;
        if (Compact)
        {
            Background = Brushes.Transparent; Padding = new(0);
            _detailScroll.Content = _content; _flyout.Content = _detailScroll; Child = _compactContent;
        }
        else
        {
            Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
            Bind(PaddingProperty, new DynamicResourceExtension("UiStatusPadding"));
            Child = _content;
        }
        Update(_activity);
    }
    private void LanguageChanged(object? sender, EventArgs args) => Update(_activity);
    private static TextBlock Text()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        Localization.SetIsUserText(text, true);
        return text;
    }
    private static TextBlock Line()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.NoWrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "caption", "queue-secondary" } };
        Localization.SetIsUserText(text, true);
        return text;
    }
    public void Update(AiActivity? activity)
    {
        _activity = activity; IsVisible = activity is not null;
        if (activity is null) { _timer.Stop(); _flyout.Hide(); _details.IsExpanded = false; ReleasePreview(); return; }
        var quantified = activity.Total is > 0 && activity.Current is not null;
        var count = quantified ? activity.Unit switch
        {
            "秒" => $"{AiActivity.FormatElapsed(activity.Current!.Value)} / {AiActivity.FormatElapsed(activity.Total!.Value)}",
            "字节" => $"{Size(activity.Current!.Value)} / {Size(activity.Total!.Value)}",
            "%" => $"{activity.Current:0}%",
            _ => $"{activity.Current:0} / {activity.Total:0} {Localization.Text(activity.Unit)}"
        } : "";
        _stage.Text = string.Join(" · ", new[] { Localization.Text(activity.Stage), count }.Where(text => text.Length > 0));
        _compactStage.Text = _stage.Text;
        ToolTip.SetTip(_compactStage, _stage.Text);
        _compactStage.IsVisible = _compactTrack.IsVisible = activity.State == AiActivityState.Running;
        foreach (var state in Enum.GetValues<AiActivityState>()) _compactStage.Classes.Set(state.ToString().ToLowerInvariant(), state == activity.State);
        // Unknown work has a stage and clock, without an invented percentage or animation.
        _progress.IsVisible = quantified;
        _progress.Value = quantified ? Math.Clamp(activity.Current!.Value / activity.Total!.Value * 100, 0, 100) : 0;
        _compactProgress.IsVisible = quantified; _compactProgress.Value = _progress.Value;
        var results = activity.ResultCount > 0 ? Localization.Format($"{activity.ResultCount} {Localization.Key(activity.ResultLabel)}") : "";
        _meta.Text = string.Join(" · ", new[] { activity.Model, activity.Backend, results, Localization.Text(activity.Detail) }.Where(text => text.Length > 0));
        _latest.Text = activity.RecentResults.LastOrDefault() ?? "";
        _latest.MaxHeight = 42; _latest.TextTrimming = TextTrimming.CharacterEllipsis;
        _latest.IsVisible = _latest.Text.Length > 0;
        _history.Text = string.Join(Environment.NewLine, activity.RecentStages) + (activity.RecentResults.Length > 0
            ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, activity.RecentResults) : "");
        _details.Header = activity.RecentResults.Length > 0 ? Localization.Format($"阶段结果 · 最近 {activity.RecentResults.Length} 条") : Localization.Text("过程记录");
        _details.IsVisible = activity.RecentStages.Length > 0 || activity.RecentResults.Length > 0 || activity.Preview is not null;
        _detailsButton.Content = Localization.Text("详情");
        ToolTip.SetTip(_detailsButton, Localization.Text("进度详情"));
        RefreshPreview(); RefreshClock();
        if (_attached && activity.State == AiActivityState.Running) _timer.Start(); else _timer.Stop();
    }
    private static string Size(double bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824:0.00} GiB" : $"{bytes / 1048576:0.0} MiB";
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
    }
    private void RefreshPreview()
    {
        if (!_attached || (Compact && !_flyout.IsOpen) || _activity?.Preview is not { } png) { ReleasePreview(); return; }
        if (!ReferenceEquals(png, _preview))
        {
            ReleasePreview();
            try { using var stream = new MemoryStream(png); _bitmap = Bitmap.DecodeToWidth(stream, 360); _preview = png; _image.Source = _expandedImage.Source = _bitmap; }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { }
        }
        _frame.IsVisible = _image.IsVisible = !Compact && _bitmap is not null;
        _expandedImage.IsVisible = _details.IsExpanded && _bitmap is not null;
        _caption.Text = _expandedCaption.Text = _activity.PreviewCaption;
        _caption.IsVisible = !Compact && _caption.Text.Length > 0;
        _expandedCaption.IsVisible = Compact && _expandedImage.IsVisible && _expandedCaption.Text.Length > 0;
    }
    private void ReleasePreview()
    {
        _image.Source = _expandedImage.Source = null; _bitmap?.Dispose(); _bitmap = null; _preview = null;
        _image.IsVisible = _expandedImage.IsVisible = _caption.IsVisible = _expandedCaption.IsVisible = _frame.IsVisible = false;
    }
}
