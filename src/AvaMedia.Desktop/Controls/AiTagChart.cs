using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed record TagChartBar(string Key, string Label, double Score, double Peak, double Average, double? Current);
public sealed record TagChartSeries(string Key, string Label, IReadOnlyList<MediaTagPoint> Points);

/// <summary>Actual sampled observations, with a draggable threshold and sample seeking.</summary>
public sealed class AiTagChart : Control
{
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> SuccessBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(SuccessBrush));
    public static readonly StyledProperty<IBrush?> WarningBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(WarningBrush));
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? SuccessBrush { get => GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }
    public IBrush? WarningBrush { get => GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public bool Timeline { get; init; }
    private IReadOnlyList<TagChartBar> _bars = [];
    private IReadOnlyList<TagChartSeries> _series = [];
    private double _threshold = .4, _duration = 1, _cursor, _minimum;
    private string? _selected;
    private bool _draggingThreshold;
    public event Action<string>? TagSelected;
    public event Action<double>? SampleSelected;
    public event Action<double>? ThresholdEdited;
    static AiTagChart() => AffectsRender<AiTagChart>(AccentBrushProperty, TextBrushProperty, GridBrushProperty, SuccessBrushProperty, WarningBrushProperty);
    public AiTagChart()
    {
        Focusable = true;
        Bind(AccentBrushProperty, new DynamicResourceExtension("UiAccent"));
        Bind(TextBrushProperty, new DynamicResourceExtension("UiText"));
        Bind(GridBrushProperty, new DynamicResourceExtension("UiDivider"));
        Bind(SuccessBrushProperty, new DynamicResourceExtension("UiSuccess"));
        Bind(WarningBrushProperty, new DynamicResourceExtension("UiWarning"));
        AutomationProperties.SetName(this, "AI 标签图表");
    }
    public void Update(IReadOnlyList<TagChartBar> bars, IReadOnlyList<TagChartSeries> series, double threshold, double duration, double cursor, string? selected, bool semantic)
    {
        _bars = bars; _series = series; _threshold = threshold; _duration = Math.Max(1, duration); _cursor = cursor; _selected = selected;
        _minimum = semantic ? -1 : 0; InvalidateVisual();
    }
    private Rect Plot => Timeline ? new(38, 24, Math.Max(1, Bounds.Width - 52), Math.Max(1, Bounds.Height - 58))
        : new(148, 24, Math.Max(1, Bounds.Width - 194), Math.Max(1, Bounds.Height - 50));
    private double X(double seconds) => Plot.X + seconds / _duration * Plot.Width;
    private double ScoreX(double score) => Plot.X + (score - _minimum) / (1 - _minimum) * Plot.Width;
    private double Y(double score) => Plot.Bottom - (score - _minimum) / (1 - _minimum) * Plot.Height;
    private void Text(DrawingContext context, string text, Point point, double size = 11, IBrush? brush = null)
    {
        var value = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, brush ?? TextBrush ?? Brushes.Black);
        context.DrawText(value, point);
    }
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Bounds.Width < 200 || Bounds.Height < 80) return;
        var plot = Plot; var grid = new Pen(GridBrush, 1);
        if (Timeline)
        {
            for (var i = 0; i <= 4; i++)
            {
                var score = _minimum + (1 - _minimum) * i / 4;
                var y = Y(score); context.DrawLine(grid, new(plot.X, y), new(plot.Right, y));
                Text(context, score.ToString("0.00"), new(0, y - 7));
                var x = plot.X + plot.Width * i / 4;
                context.DrawLine(grid, new(x, plot.Y), new(x, plot.Bottom));
                var caption = MediaTime.Format(_duration * i / 4);
                Text(context, caption, new(Math.Clamp(x - 28, plot.X, Math.Max(plot.X, plot.Right - 66)), plot.Bottom + 8), 10);
            }
            var thresholdY = Y(_threshold);
            context.DrawLine(new Pen(WarningBrush, 1, new DashStyle([5, 4], 0)), new(plot.X, thresholdY), new(plot.Right, thresholdY));
            Text(context, Localization.Text("阈值") + $" {_threshold:0.00}", new(plot.X + 4, Math.Max(plot.Y, thresholdY - 16)), 10, WarningBrush);
            IBrush?[] colors = [AccentBrush, SuccessBrush, WarningBrush];
            using (context.PushClip(plot.Inflate(4)))
                for (var index = 0; index < _series.Count; index++)
                {
                    var series = _series[index]; var color = colors[index % colors.Length] ?? Brushes.Blue;
                    var line = new Pen(color, 2); Point? previous = null;
                    var known = series.Points.Where(point => point.Score.HasValue).ToArray();
                    var peak = known.OrderByDescending(point => point.Score).FirstOrDefault();
                    foreach (var point in series.Points)
                    {
                        if (point.Score is not { } score) { previous = null; continue; }
                        var position = new Point(X(point.Seconds), Y(score));
                        if (previous is { } before) context.DrawLine(line, before, position);
                        context.DrawEllipse(color, null, position, 3, 3);
                        if (point == peak) context.DrawEllipse(null, new Pen(color, 2), position, 6, 6);
                        previous = position;
                    }
                }
            if (_series.Count > 0) context.DrawLine(new Pen(TextBrush, 1), new(X(_cursor), plot.Y), new(X(_cursor), plot.Bottom));
            if (_series.Count == 0) Text(context, Localization.Text("选择标签查看采样曲线"), new(plot.X + 12, plot.Y + plot.Height / 2));
        }
        else
        {
            for (var i = 0; i <= 4; i++)
            {
                var x = plot.X + plot.Width * i / 4; context.DrawLine(grid, new(x, plot.Y), new(x, plot.Bottom));
                Text(context, (_minimum + (1 - _minimum) * i / 4d).ToString("0.00"), new(x - 10, plot.Bottom + 8), 10);
            }
            var row = plot.Height / Math.Max(1, _bars.Count);
            for (var index = 0; index < _bars.Count; index++)
            {
                var item = _bars[index]; var y = plot.Y + index * row + 3;
                using (context.PushClip(new Rect(0, y, 140, row))) Text(context, item.Label, new(0, y + 2), 12);
                var scoreX = ScoreX(Math.Clamp(item.Score, _minimum, 1)); var zeroX = ScoreX(0);
                var rect = new Rect(Math.Min(scoreX, zeroX), y, Math.Abs(scoreX - zeroX), Math.Max(2, Math.Min(18, row - 5)));
                context.DrawRectangle(item.Score >= _threshold ? AccentBrush : GridBrush, item.Key == _selected ? new Pen(TextBrush, 1) : null, rect, 2, 2);
                Text(context, item.Score.ToString("0.000"), new(plot.Right + 5, y + 2), 10);
            }
            var thresholdX = ScoreX(_threshold);
            context.DrawLine(new Pen(WarningBrush, 1, new DashStyle([5, 4], 0)), new(thresholdX, plot.Y), new(thresholdX, plot.Bottom));
            Text(context, Localization.Text("阈值") + $" {_threshold:0.00}", new(Math.Clamp(thresholdX - 25, plot.X, Math.Max(plot.X, plot.Right - 60)), 4), 10, WarningBrush);
            if (_bars.Count == 0) Text(context, Localization.Text("等待标签分数"), new(plot.X, plot.Y + 20));
        }
    }
    private void SetThreshold(Point point) => ThresholdEdited?.Invoke(Math.Clamp(Timeline
        ? _minimum + (Plot.Bottom - point.Y) / Plot.Height * (1 - _minimum) : _minimum + (point.X - Plot.X) / Plot.Width * (1 - _minimum), .05, .95));
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this); Focus();
        if (Plot.Contains(point) && Math.Abs(Timeline ? point.Y - Y(_threshold) : point.X - ScoreX(_threshold)) < 7)
        { _draggingThreshold = true; e.Pointer.Capture(this); SetThreshold(point); }
        else if (Timeline && Plot.Contains(point)) Seek(point.X);
        else if (!Timeline && _bars.Count > 0 && point.Y >= Plot.Y && point.Y <= Plot.Bottom)
            TagSelected?.Invoke(_bars[Math.Clamp((int)((point.Y - Plot.Y) / Plot.Height * _bars.Count), 0, _bars.Count - 1)].Key);
        e.Handled = true;
    }
    private void Seek(double x)
    {
        var seconds = (x - Plot.X) / Plot.Width * _duration;
        var nearest = _series.SelectMany(series => series.Points).Where(point => point.Score.HasValue).OrderBy(point => Math.Abs(point.Seconds - seconds)).FirstOrDefault();
        if (nearest is not null) SampleSelected?.Invoke(nearest.Seconds);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var point = e.GetPosition(this);
        if (_draggingThreshold) { SetThreshold(point); return; }
        if (Timeline && Plot.Contains(point))
        {
            var time = (point.X - Plot.X) / Plot.Width * _duration;
            var nearest = _series.SelectMany(series => series.Points).Where(sample => sample.Score.HasValue).MinBy(sample => Math.Abs(sample.Seconds - time));
            ToolTip.SetTip(this, nearest is null ? null : MediaTime.Format(nearest.Seconds) + "\n" + string.Join("\n", _series.Select(series => series.Label + " · " + series.Points.FirstOrDefault(sample => sample.Seconds == nearest.Seconds)?.Score?.ToString("0.000"))));
        }
        else if (!Timeline && _bars.Count > 0 && point.Y >= Plot.Y && point.Y < Plot.Bottom)
        {
            var item = _bars[Math.Clamp((int)((point.Y - Plot.Y) / Plot.Height * _bars.Count), 0, _bars.Count - 1)];
            ToolTip.SetTip(this, $"{item.Label}\n" + Localization.Text("峰值") + $" {item.Peak:0.000} · " + Localization.Text("平均") + $" {item.Average:0.000}");
        }
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _draggingThreshold = false; e.Pointer.Capture(null); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _draggingThreshold = false; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Up or Key.Down) { ThresholdEdited?.Invoke(Math.Clamp(_threshold + (e.Key == Key.Up ? .01 : -.01), .05, .95)); e.Handled = true; }
        if (Timeline && e.Key is Key.Left or Key.Right)
        {
            var points = _series.SelectMany(series => series.Points).Select(point => point.Seconds).Distinct().Order().ToArray();
            var next = e.Key == Key.Left ? points.Where(time => time < _cursor).LastOrDefault(_cursor) : points.Where(time => time > _cursor).FirstOrDefault(_cursor);
            SampleSelected?.Invoke(next); e.Handled = true;
        }
    }
}
