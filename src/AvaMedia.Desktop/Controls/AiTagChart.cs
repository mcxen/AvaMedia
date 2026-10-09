using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed record TagChartBar(string Key, string Label, double Score, double Peak, double Average, double? Current);
public sealed record TagChartSeries(string Key, IReadOnlyList<MediaTagPoint> Points, string Model, bool Semantic, int Variant);
public sealed record TagChartThreshold(string Model, double Value, bool Semantic);

/// <summary>Actual sampled observations, with a draggable threshold and sample seeking.</summary>
public sealed class AiTagChart : Control
{
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> SuccessBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(SuccessBrush));
    public static readonly StyledProperty<IBrush?> WarningBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(WarningBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<AiTagChart, IBrush?>(nameof(SurfaceBrush));
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? SuccessBrush { get => GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }
    public IBrush? WarningBrush { get => GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    /// <summary>Overrides the empty-state text once data exists but nothing matches; null shows the pre-analysis hint.</summary>
    public string? EmptyText { get => _emptyText; set { _emptyText = value; InvalidateVisual(); } }
    public bool Timeline { get; init; }
    private IReadOnlyList<TagChartBar> _bars = [];
    private IReadOnlyList<TagChartSeries> _series = [];
    private IReadOnlyList<TagChartThreshold> _thresholds = [];
    private TagChartThreshold? _draggedThreshold;
    private string? _activeThresholdModel, _hoveredThresholdModel, _emptyText;
    private bool _thresholdHovered;
    private const double ThresholdHitSlop = 10;
    private double _threshold = .4, _duration = 1, _cursor, _minimum;
    private string? _selected, _hovered;
    private bool _draggingThreshold, _seeking, _canSeek;
    public bool IsSeeking => _seeking;
    public event Action<string>? TagSelected;
    public event Action<double>? SampleSelected;
    public event Action<double?>? SampleHovered;
    public event Action<TagChartBar?>? BarHovered;
    public event Action<double>? ThresholdEdited;
    public event Action<string, double>? ModelThresholdEdited;
    static AiTagChart() => AffectsRender<AiTagChart>(AccentBrushProperty, TextBrushProperty, GridBrushProperty, SuccessBrushProperty, WarningBrushProperty, SurfaceBrushProperty);
    public AiTagChart()
    {
        Focusable = true;
        Bind(AccentBrushProperty, new DynamicResourceExtension("UiAccent"));
        Bind(TextBrushProperty, new DynamicResourceExtension("UiText"));
        Bind(GridBrushProperty, new DynamicResourceExtension("UiDivider"));
        Bind(SuccessBrushProperty, new DynamicResourceExtension("UiSuccess"));
        Bind(WarningBrushProperty, new DynamicResourceExtension("UiWarning"));
        Bind(SurfaceBrushProperty, new DynamicResourceExtension("UiSurfaceRaised"));
        AutomationProperties.SetName(this, "AI 标签图表");
    }
    public void Update(IReadOnlyList<TagChartBar> bars, IReadOnlyList<TagChartSeries> series, double threshold, double duration, double cursor, string? selected, bool semantic, IReadOnlyList<TagChartThreshold>? thresholds = null)
    {
        _bars = bars; _series = series; _threshold = threshold;
        _duration = double.IsFinite(duration) && duration > 0 ? duration : 1; _cursor = cursor; _selected = selected;
        _canSeek = Timeline && double.IsFinite(duration) && duration > 0;
        _thresholds = thresholds ?? []; _minimum = semantic ? -1 : 0; _hovered = null; InvalidateVisual();
    }
    public void UpdateCursor(double seconds)
    {
        _cursor = Math.Clamp(seconds, 0, _duration); InvalidateVisual();
    }
    private Rect Plot => Timeline ? new(38, 28, Math.Max(1, Bounds.Width - 92), Math.Max(1, Bounds.Height - 62))
        : new(24, 24, Math.Max(1, Bounds.Width - 24), Math.Max(1, Bounds.Height - 28));
    private double X(double seconds) => Plot.X + seconds / _duration * Plot.Width;
    private double ScoreX(double score) => Plot.X + (score - _minimum) / (1 - _minimum) * Plot.Width;
    private double Y(double score, bool semantic = false)
    {
        var minimum = Timeline ? (semantic ? -1 : 0) : _minimum;
        return Plot.Bottom - (score - minimum) / (1 - minimum) * Plot.Height;
    }
    private IBrush ModelBrush(string model) => model == ModelCatalog.EmbeddingId ? SuccessBrush ?? Brushes.SeaGreen : AccentBrush ?? Brushes.DodgerBlue;
    private FormattedText FormatText(string text, double size = 11, IBrush? brush = null, FontWeight? weight = null)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), weight: weight ?? FontWeight.Normal), size, brush ?? TextBrush ?? Brushes.Black);
    private void Text(DrawingContext context, string text, Point point, double size = 11, IBrush? brush = null)
    {
        context.DrawText(FormatText(text, size, brush), point);
    }
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Bounds.Width < 200 || Bounds.Height < 80) return;
        var plot = Plot; var grid = new Pen(GridBrush, 1);
        if (Timeline) RenderTimeline(context, plot, grid);
        else RenderBars(context, plot);
    }
    private void RenderBars(DrawingContext context, Rect plot)
    {
        var color = ModelBrush(_minimum < 0 ? ModelCatalog.EmbeddingId : ModelCatalog.JoyTagId);
        using (context.PushOpacity(.55))
        {
            Text(context, _minimum < 0 ? "−1" : "0", new(plot.X, 0), 10);
            var end = FormatText("1", 10); context.DrawText(end, new(plot.Right - end.Width, 0));
            if (_minimum < 0) Text(context, "0", new(ScoreX(0) - 3, 0), 10);
        }
        var row = plot.Height / Math.Max(1, _bars.Count);
        for (var index = 0; index < _bars.Count; index++)
        {
            var item = _bars[index]; var y = plot.Y + index * row;
            var selected = item.Key == _selected; var hovered = item.Key == _hovered;
            if (selected || hovered)
                using (context.PushOpacity(selected ? .09 : .05))
                    context.DrawRectangle(color, null, new Rect(0, y - 4, Bounds.Width, row - 4), 4, 4);
            using (context.PushOpacity(.5)) Text(context, (index + 1).ToString("00"), new(0, y + 2), 10);
            var value = FormatText(item.Score.ToString("0.000"), 12, color, FontWeight.SemiBold);
            var label = FormatText(item.Label, 12, weight: selected ? FontWeight.SemiBold : FontWeight.Normal);
            label.MaxTextWidth = Math.Max(1, plot.Width - value.Width - 16);
            label.MaxLineCount = 1; label.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(label, new(plot.X, y));
            context.DrawText(value, new(plot.Right - value.Width, y));
            var track = new Rect(plot.X, y + 22, plot.Width, 5);
            using (context.PushOpacity(.35)) context.DrawRectangle(GridBrush, null, track, 2.5, 2.5);
            var scoreX = ScoreX(Math.Clamp(item.Score, _minimum, 1)); var zeroX = ScoreX(0);
            var filled = new Rect(Math.Min(scoreX, zeroX), track.Y, Math.Abs(scoreX - zeroX), track.Height);
            using (context.PushOpacity(item.Score >= _threshold ? .9 : .35))
                context.DrawRectangle(color, null, filled, 2.5, 2.5);
            if (_minimum < 0) context.DrawLine(new Pen(GridBrush, 1), new(zeroX, track.Y - 1), new(zeroX, track.Bottom + 1));
            var thresholdX = ScoreX(_threshold);
            context.DrawLine(new Pen(WarningBrush, _thresholdHovered || _draggingThreshold ? 3.5 : 2.5), new(thresholdX, track.Y - 4), new(thresholdX, track.Bottom + 4));
        }
        if (_bars.Count == 0) { Text(context, _emptyText is { } empty ? Localization.Text(empty) : Localization.Text("分析完成后显示标签排名"), new(plot.X, plot.Y + 20)); return; }
        // A continuous pass line plus a labelled handle make the draggable threshold visible across all rows.
        var lineX = ScoreX(_threshold);
        using (context.PushOpacity(_thresholdHovered || _draggingThreshold ? .9 : .55))
            context.DrawLine(new Pen(WarningBrush, 1, new DashStyle([4, 3], 0)), new(lineX, plot.Y - 6), new(lineX, plot.Bottom));
        ThresholdHandle(context, _threshold.ToString("0.00", CultureInfo.InvariantCulture), new(lineX, 6), WarningBrush, plot);
    }
    private void ThresholdHandle(DrawingContext context, string value, Point anchor, IBrush? brush, Rect plot, bool rightAligned = false)
    {
        var text = FormatText(value, 10, brush, FontWeight.SemiBold);
        var width = text.Width + 10; var height = text.Height + 2;
        var x = rightAligned ? plot.Right - width : Math.Clamp(anchor.X - width / 2, plot.X, Math.Max(plot.X, plot.Right - width));
        var box = new Rect(x, anchor.Y, width, height);
        context.DrawRectangle(SurfaceBrush ?? Brushes.White, new Pen(brush, _thresholdHovered || _draggingThreshold ? 2 : 1), box, 4, 4);
        context.DrawText(text, new(box.X + 5, box.Y + 1));
    }
    private void RenderTimeline(DrawingContext context, Rect plot, Pen grid)
    {
        var semantic = _thresholds.Any(threshold => threshold.Semantic) || _series.Any(series => series.Semantic);
        Text(context, Localization.Text("标签分数"), new(plot.X, 4), 10, ModelBrush(ModelCatalog.JoyTagId));
        if (semantic) Text(context, Localization.Text("语义相似度"), new(Math.Max(plot.X, plot.Right - 90), 4), 10, ModelBrush(ModelCatalog.EmbeddingId));
        for (var i = 0; i <= 4; i++)
        {
            var score = i / 4d; var y = Y(score);
            using (context.PushOpacity(.5)) context.DrawLine(grid, new(plot.X, y), new(plot.Right, y));
            Text(context, score.ToString("0.00"), new(0, y - 7), brush: ModelBrush(ModelCatalog.JoyTagId));
            if (semantic) Text(context, (score * 2 - 1).ToString("0.00"), new(plot.Right + 5, y - 7), brush: ModelBrush(ModelCatalog.EmbeddingId));
            var x = plot.X + plot.Width * i / 4; context.DrawLine(grid, new(x, plot.Bottom), new(x, plot.Bottom + 3));
        }
        DrawTimeLabels(context, plot);
        foreach (var threshold in _thresholds)
        {
            var color = ModelBrush(threshold.Model); var y = Y(threshold.Value, threshold.Semantic);
            var active = threshold.Model == _hoveredThresholdModel || threshold.Model == _draggedThreshold?.Model;
            context.DrawLine(new Pen(color, active ? 3 : 2, new DashStyle([6, 4], 0)), new(plot.X, y), new(plot.Right, y));
        }
        using (context.PushClip(plot.Inflate(4)))
            foreach (var series in _series)
            {
                var color = ModelBrush(series.Model);
                var dash = series.Variant switch { 1 => new DashStyle([6, 3], 0), 2 => new DashStyle([1, 3], 0), _ => null };
                var line = new Pen(color, 2, dash); Point? previous = null;
                var peak = series.Points.Where(point => point.Score.HasValue).MaxBy(point => point.Score);
                foreach (var point in series.Points)
                {
                    if (point.Score is not { } score) { previous = null; continue; }
                    var position = new Point(X(point.Seconds), Y(score, series.Semantic));
                    if (previous is { } before) context.DrawLine(line, before, position);
                    context.DrawEllipse(color, null, position, 3, 3);
                    if (point == peak) context.DrawEllipse(null, new Pen(color, 2), position, 6, 6);
                    previous = position;
                }
            }
        if (_canSeek) context.DrawLine(new Pen(TextBrush, 1), new(X(_cursor), plot.Y), new(X(_cursor), plot.Bottom));
        foreach (var threshold in _thresholds)
        {
            var y = Y(threshold.Value, threshold.Semantic);
            ThresholdHandle(context, threshold.Value.ToString("0.00", CultureInfo.InvariantCulture), new(threshold.Semantic ? plot.Right : plot.X, Math.Max(plot.Y - 6, y - 18)), ModelBrush(threshold.Model), plot,
                rightAligned: threshold.Semantic);
        }
        // Before analysis the ranking chart carries the empty-state hint; the curve stays blank instead of repeating it.
        if (_series.Count == 0 && (_thresholds.Count > 0 || _bars.Count > 0)) Text(context, Localization.Text("点击下方标签或柱条添加对比曲线"), new(plot.X + 12, plot.Y + plot.Height / 2));
    }
    private void DrawTimeLabels(DrawingContext context, Rect plot)
    {
        // Axis ticks need whole seconds; sample readouts retain their exact timestamps.
        var labels = Enumerable.Range(0, 5).Select(index => (Index: index, Caption: MediaTime.Format(Math.Round(_duration * index / 4))[..^4]))
            .Select(item => (item.Index, item.Caption, Text: FormatText(item.Caption, 10))).ToArray();
        var end = labels[^1]; var endX = plot.Right - end.Text.Width; var y = plot.Bottom + 8;
        var previousEnd = plot.X + labels[0].Text.Width;
        if (previousEnd + 8 > endX) { context.DrawText(end.Text, new(endX, y)); return; }
        context.DrawText(labels[0].Text, new(plot.X, y));
        var displayed = new HashSet<string> { labels[0].Caption, end.Caption };
        foreach (var label in labels.Skip(1).SkipLast(1))
        {
            var x = plot.X + plot.Width * label.Index / 4 - label.Text.Width / 2;
            if (x < previousEnd + 8 || x + label.Text.Width > endX - 8 || !displayed.Add(label.Caption)) continue;
            context.DrawText(label.Text, new(x, y)); previousEnd = x + label.Text.Width;
        }
        context.DrawText(end.Text, new(endX, y));
    }
    private void SetThreshold(Point point)
    {
        if (Timeline && _draggedThreshold is { } threshold)
        {
            var minimum = threshold.Semantic ? -1 : 0;
            ModelThresholdEdited?.Invoke(threshold.Model, Math.Clamp(minimum + (Plot.Bottom - point.Y) / Plot.Height * (1 - minimum), .05, .95));
        }
        else if (!Timeline) ThresholdEdited?.Invoke(Math.Clamp(_minimum + (point.X - Plot.X) / Plot.Width * (1 - _minimum), .05, .95));
    }
    private TagChartBar? BarAt(Point point)
        => !Timeline && _bars.Count > 0 && new Rect(0, Plot.Y - 4, Bounds.Width, Plot.Height + 4).Contains(point)
            ? _bars[Math.Clamp((int)((point.Y - Plot.Y) / Plot.Height * _bars.Count), 0, _bars.Count - 1)] : null;
    private bool OnBarThreshold(Point point)
    {
        if (Timeline || _bars.Count == 0 || Math.Abs(point.X - ScoreX(_threshold)) >= ThresholdHitSlop) return false;
        if (point.Y >= 0 && point.Y < Plot.Y - 4) return true; // value handle above the rows
        if (BarAt(point) is null) return false;
        // Lower part of each row (track and its margin); the label line above stays a tag-selection target.
        var row = Plot.Height / _bars.Count;
        var relativeY = (point.Y - Plot.Y) % row;
        return relativeY >= 12;
    }
    private TagChartThreshold? ThresholdAt(Point point)
    {
        if (!Timeline) return null;
        var plot = Plot;
        if (point.X < plot.X || point.X > plot.Right + 4 || point.Y < plot.Y - ThresholdHitSlop || point.Y > plot.Bottom + ThresholdHitSlop) return null;
        return _thresholds.Where(threshold => Math.Abs(point.Y - Y(threshold.Value, threshold.Semantic)) < ThresholdHitSlop)
            .OrderBy(threshold => Math.Abs(point.Y - Y(threshold.Value, threshold.Semantic)))
            .ThenBy(threshold => threshold.Semantic == (point.X > plot.Center.X) ? 0 : 1).FirstOrDefault();
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this); Focus();
        if (Timeline && ThresholdAt(point) is { } hit)
        {
            _draggedThreshold = hit; _activeThresholdModel = hit.Model; _draggingThreshold = true; e.Pointer.Capture(this); SetThreshold(point);
        }
        else if (Timeline && new Rect(Plot.X, Plot.Y, Plot.Width, Bounds.Height - Plot.Y).Contains(point))
        {
            if (_canSeek) { _seeking = true; e.Pointer.Capture(this); Seek(point.X, force: true); }
        }
        else if (!Timeline && OnBarThreshold(point))
        { _draggingThreshold = true; e.Pointer.Capture(this); SetThreshold(point); }
        else if (BarAt(point) is { } bar) TagSelected?.Invoke(bar.Key);
        e.Handled = true;
    }
    private void Seek(double x, bool force = false)
    {
        var seconds = Math.Clamp((x - Plot.X) / Plot.Width * _duration, 0, Math.Max(0, _duration - .001));
        if (!force && Math.Abs(seconds - _cursor) < .0001) return;
        UpdateCursor(seconds); SampleSelected?.Invoke(seconds);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var point = e.GetPosition(this);
        if (_seeking) { Seek(point.X); e.Handled = true; return; }
        if (_draggingThreshold) { SetThreshold(point); return; }
        if (Timeline)
        {
            var hovered = ThresholdAt(point)?.Model;
            if (hovered != _hoveredThresholdModel) { _hoveredThresholdModel = hovered; InvalidateVisual(); }
            Cursor = new Cursor(hovered is not null ? StandardCursorType.SizeNorthSouth : StandardCursorType.Arrow);
        }
        if (Timeline && Plot.Contains(point))
        {
            var time = (point.X - Plot.X) / Plot.Width * _duration;
            var nearest = _series.SelectMany(series => series.Points).Where(sample => sample.Score.HasValue).MinBy(sample => Math.Abs(sample.Seconds - time));
            SampleHovered?.Invoke(nearest?.Seconds);
        }
        else if (!Timeline)
        {
            var item = BarAt(point);
            var onThreshold = OnBarThreshold(point);
            if (_hovered != item?.Key || _thresholdHovered != onThreshold) { _hovered = item?.Key; _thresholdHovered = onThreshold; InvalidateVisual(); }
            Cursor = new Cursor(onThreshold ? StandardCursorType.SizeWestEast : item is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            BarHovered?.Invoke(item);
        }
        else { SampleHovered?.Invoke(null); BarHovered?.Invoke(null); }
    }
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e); _hovered = _hoveredThresholdModel = null; _thresholdHovered = false; InvalidateVisual(); SampleHovered?.Invoke(null); BarHovered?.Invoke(null);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_seeking) { _seeking = false; Seek(e.GetPosition(this).X, force: true); e.Handled = true; }
        _seeking = _draggingThreshold = false; _draggedThreshold = null; e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e); _seeking = _draggingThreshold = false; _draggedThreshold = null;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Up or Key.Down)
        {
            var step = e.Key == Key.Up ? .01 : -.01;
            if (!Timeline) ThresholdEdited?.Invoke(Math.Clamp(_threshold + step, .05, .95));
            else if ((_thresholds.FirstOrDefault(item => item.Model == _activeThresholdModel)
                ?? _thresholds.FirstOrDefault(item => _selected?.StartsWith(item.Model + "/", StringComparison.Ordinal) == true)
                ?? _thresholds.FirstOrDefault()) is { } threshold)
                ModelThresholdEdited?.Invoke(threshold.Model, Math.Clamp(threshold.Value + step, .05, .95));
            e.Handled = true;
        }
        if (Timeline && _canSeek && e.Key is Key.Left or Key.Right)
        {
            var points = _series.SelectMany(series => series.Points).Select(point => point.Seconds).Distinct().Order().ToArray();
            var next = e.Key == Key.Left ? points.Where(time => time < _cursor).LastOrDefault(_cursor) : points.Where(time => time > _cursor).FirstOrDefault(_cursor);
            SampleSelected?.Invoke(next); e.Handled = true;
        }
    }
}
