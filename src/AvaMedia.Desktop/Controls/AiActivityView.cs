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
    private readonly TextBlock _stage = Text();
    private readonly TextBlock _meta = Text();
    private readonly TextBlock _clock = Text();
    private readonly TextBlock _latest = Text();
    private readonly TextBlock _history = Text();
    private readonly TextBlock _caption = Text();
    private readonly ProgressBar _progress = new() { Maximum = 100, Height = 5 };
    private readonly Image _image = new() { Width = 120, Height = 76, Stretch = Stretch.Uniform, IsVisible = false };
    private readonly Image _expandedImage = new() { Height = 140, Stretch = Stretch.Uniform, IsVisible = false };
    private readonly StackPanel _frame = new() { Width = 120, Spacing = 4, IsVisible = false };
    private readonly Expander _details = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
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
        var content = new StackPanel { Spacing = 4 };
        var overview = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        var summary = new StackPanel { Spacing = 4 };
        summary.Children.Add(_stage); summary.Children.Add(_progress);
        summary.Children.Add(_meta); summary.Children.Add(_clock); summary.Children.Add(_latest);
        overview.Children.Add(summary);
        _caption.MaxHeight = 36; _caption.TextTrimming = TextTrimming.CharacterEllipsis;
        _frame.Children.Add(_image); _frame.Children.Add(_caption); Grid.SetColumn(_frame, 1); overview.Children.Add(_frame);
        content.Children.Add(overview);
        var observations = new StackPanel { Spacing = 6 };
        observations.Children.Add(_expandedImage); observations.Children.Add(_history);
        _details.Content = new ScrollViewer { Content = observations, MaxHeight = 230, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        content.Children.Add(_details); Child = content;
        _timer.Tick += (_, _) => RefreshClock();
        _details.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) RefreshPreview(); };
        AttachedToVisualTree += (_, _) => { _attached = true; Localization.Changed += LanguageChanged; Update(_activity); };
        DetachedFromVisualTree += (_, _) => { _attached = false; Localization.Changed -= LanguageChanged; _timer.Stop(); ReleasePreview(); };
    }
    private void LanguageChanged(object? sender, EventArgs args) => Update(_activity);
    private static TextBlock Text()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        Localization.SetIsUserText(text, true);
        return text;
    }
    public void Update(AiActivity? activity)
    {
        _activity = activity; IsVisible = activity is not null;
        if (activity is null) { _timer.Stop(); _details.IsExpanded = false; ReleasePreview(); return; }
        var quantified = activity.Total is > 0 && activity.Current is not null;
        var count = quantified ? activity.Unit switch
        {
            "秒" => $"{MediaTime.Format(activity.Current!.Value)} / {MediaTime.Format(activity.Total!.Value)}",
            "字节" => $"{Size(activity.Current!.Value)} / {Size(activity.Total!.Value)}",
            "%" => $"{activity.Current:0}%",
            _ => $"{activity.Current:0} / {activity.Total:0} {Localization.Text(activity.Unit)}"
        } : "";
        _stage.Text = string.Join(" · ", new[] { Localization.Text(activity.Stage), count }.Where(text => text.Length > 0));
        // Unknown work has a stage and clock, without an invented percentage or animation.
        _progress.IsVisible = quantified;
        _progress.Value = quantified ? Math.Clamp(activity.Current!.Value / activity.Total!.Value * 100, 0, 100) : 0;
        var results = activity.ResultCount > 0 ? Localization.Format($"{activity.ResultCount} {Localization.Key(activity.ResultLabel)}") : "";
        _meta.Text = string.Join(" · ", new[] { activity.Model, activity.Backend, results, Localization.Text(activity.Detail) }.Where(text => text.Length > 0));
        _latest.Text = activity.RecentResults.LastOrDefault() ?? "";
        _latest.MaxHeight = 42; _latest.TextTrimming = TextTrimming.CharacterEllipsis;
        _latest.IsVisible = _latest.Text.Length > 0;
        _history.Text = string.Join(Environment.NewLine, activity.RecentStages) + (activity.RecentResults.Length > 0
            ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, activity.RecentResults) : "");
        _details.Header = activity.RecentResults.Length > 0 ? Localization.Format($"阶段结果 · 最近 {activity.RecentResults.Length} 条") : Localization.Text("过程记录");
        _details.IsVisible = activity.RecentStages.Length > 0 || activity.RecentResults.Length > 0 || activity.Preview is not null;
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
        var elapsed = MediaTime.Format(Math.Max(0, (now - activity.StartedUtc).TotalSeconds));
        var quiet = Math.Max(0, (now - activity.UpdatedUtc).TotalSeconds);
        _clock.Text = Localization.Format($"已用时 {elapsed}") + (activity.State == AiActivityState.Running && quiet >= 10
            ? " · " + Localization.Format($"最近进展 {quiet:0} 秒前") : "");
    }
    private void RefreshPreview()
    {
        if (!_attached || _activity?.Preview is not { } png) { ReleasePreview(); return; }
        if (!ReferenceEquals(png, _preview))
        {
            ReleasePreview();
            try { using var stream = new MemoryStream(png); _bitmap = Bitmap.DecodeToWidth(stream, 360); _preview = png; _image.Source = _expandedImage.Source = _bitmap; }
            catch (Exception error) when (error is ArgumentException or InvalidDataException) { }
        }
        _frame.IsVisible = _image.IsVisible = _bitmap is not null;
        _expandedImage.IsVisible = _details.IsExpanded && _bitmap is not null;
        _caption.Text = _activity.PreviewCaption;
        _caption.IsVisible = _caption.Text.Length > 0;
    }
    private void ReleasePreview()
    {
        _image.Source = _expandedImage.Source = null; _bitmap?.Dispose(); _bitmap = null; _preview = null;
        _image.IsVisible = _expandedImage.IsVisible = _caption.IsVisible = _frame.IsVisible = false;
    }
}
