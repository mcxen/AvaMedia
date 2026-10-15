using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop.PersonClipAdjust;

/// <summary>
/// 调整片段 timeline (design A): overview minimap, ruler, filmstrip, the keep lane with draggable bounds and the person-confidence
/// lane with threshold, unscanned spans and uncertain samples. Brushes come from theme resources; all edits go through events.
/// </summary>
internal sealed class AdjustTimeline : Control
{
    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> SoftProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Soft");
    private static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Track");
    private static readonly StyledProperty<IBrush?> UnscannedProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Unscanned");
    private static readonly StyledProperty<IBrush?> HairlineProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Hairline");
    private static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Ink");
    private static readonly StyledProperty<IBrush?> SecondaryProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Secondary");
    private static readonly StyledProperty<IBrush?> TertiaryProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Tertiary");
    private static readonly StyledProperty<IBrush?> DangerProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Danger");
    private static readonly StyledProperty<IBrush?> WarningProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Warning");
    private static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Fill");
    private static readonly StyledProperty<IBrush?> KnobProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Knob");
    private static readonly StyledProperty<IBrush?> AccentInkProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("AccentInk");
    private static readonly StyledProperty<IBrush?> CardProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Card");
    private static readonly StyledProperty<IBrush?> ExclusionFillProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("ExclusionFill");
    private static readonly StyledProperty<IBrush?> OverlayProperty = AvaloniaProperty.Register<AdjustTimeline, IBrush?>("Overlay");

    public const double LabelWidth = 58;
    private const double MiniTop = 0, MiniHeight = 12, RulerTop = 20, RulerHeight = 16, StripTop = 38, StripHeight = 34,
        KeepTop = 77, KeepHeight = 30, ScoreTop = 112, ScoreHeight = 44, TotalHeight = 158, HandleWidth = 9;

    private IReadOnlyList<PersonClipEditSegment> _segments = [];
    private IReadOnlyList<PersonClipSpan> _spans = [];
    private IReadOnlyList<PersonClipSample> _samples = [];
    private IReadOnlyList<PersonClipRange> _exclusions = [];
    private readonly Dictionary<long, IImage> _frames = [];
    private double _duration, _threshold = .35, _position, _viewStart, _zoom = 1;
    private int? _selected;
    private (double Start, double End)? _range;
    private int _hoverExclusion = -1;
    // Drag state
    private enum DragKind { None, Seek, Minimap, Range, Start, End, ExclusionStart, ExclusionEnd }
    private DragKind _drag;
    private int _dragId;
    private double _dragAnchor, _dragStart, _dragEnd;
    private PersonClipSnap? _dragSnap;

    public IImage? DragFrame { get; set; }
    private int _selectedExclusion = -1;
    public event Action? DragCancelled;
    public event Action<int, double, double>? ExclusionBoundsCommitted;
    public event Action<double>? Seek;
    public event Action<int>? SegmentClicked;
    /// <summary>Live bounds while dragging (id, start, end, snap label or null); commit is raised once on release.</summary>
    public event Action<int, double, double, PersonClipSnap?>? BoundsPreview;
    public event Action<int, double, double>? BoundsCommitted;
    public event Action<(double Start, double End)?>? RangeChanged;
    public event Action<int, Point>? ExclusionMenuRequested;
    public event Action<int?, double, Point>? SegmentMenuRequested;
    public event Action? ViewChanged;
    /// <summary>Snap provider: (seconds, toleranceSeconds, ignoreId) → snap target.</summary>
    public Func<double, double, int?, PersonClipSnap?>? Snap { get; set; }
    public bool SnapEnabled { get; set; } = true;
    public double MinimumSeconds { get; set; } = .1;

    static AdjustTimeline() => AffectsRender<AdjustTimeline>(AccentProperty, SoftProperty, TrackProperty, UnscannedProperty, HairlineProperty,
        InkProperty, SecondaryProperty, TertiaryProperty, DangerProperty, WarningProperty, FillProperty, KnobProperty, AccentInkProperty, OverlayProperty, CardProperty, ExclusionFillProperty);

    public AdjustTimeline()
    {
        Height = TotalHeight; ClipToBounds = true; Focusable = true;
        void Res(StyledProperty<IBrush?> property, string key) => Bind(property, new DynamicResourceExtension(key));
        Res(AccentProperty, "UiAccent"); Res(SoftProperty, "AcSelection"); Res(TrackProperty, "UiTrack"); Res(UnscannedProperty, "AcUnscannedFill");
        Res(HairlineProperty, "AcHairline"); Res(InkProperty, "UiText"); Res(SecondaryProperty, "UiTextSecondary"); Res(TertiaryProperty, "AcTextTertiary");
        Res(DangerProperty, "UiDanger"); Res(WarningProperty, "UiWarning"); Res(FillProperty, "UiFill"); Res(KnobProperty, "UiMediaText");
        Res(AccentInkProperty, "UiMediaText"); Res(OverlayProperty, "UiMediaOverlay"); Res(CardProperty, "AcCardFace"); Res(ExclusionFillProperty, "AcDangerSoft");
        Avalonia.Automation.AutomationProperties.SetName(this, Localization.Text("片段时间线"));
    }

    public double Duration => _duration;
    public double Zoom { get => _zoom; set { _zoom = Math.Clamp(value, 1, 64); _viewStart = _position - VisibleDuration / 2; ClampView(); InvalidateVisual(); ViewChanged?.Invoke(); } }
    public double VisibleDuration => _duration / _zoom;
    public double ViewStart => _viewStart;
    public double Position { get => _position; set { _position = value; Follow(); InvalidateVisual(); } }
    public int? SelectedId { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public (double Start, double End)? Range { get => _range; set { _range = value; InvalidateVisual(); } }
    public double DragPosition => _drag == DragKind.Start ? _dragStart : _dragEnd;
    public bool Dragging => _drag is DragKind.Start or DragKind.End;

    public void Update(IReadOnlyList<PersonClipEditSegment> segments, IReadOnlyList<PersonClipSpan> spans, IReadOnlyList<PersonClipSample> samples,
        IReadOnlyList<PersonClipRange> exclusions, double duration, double threshold)
    {
        _segments = segments; _spans = spans; _samples = samples; _exclusions = exclusions;
        _duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0; _threshold = threshold;
        if (_drag is DragKind.Start or DragKind.End && _segments.All(s => s.Id != _dragId)) _drag = DragKind.None;
        ClampView(); InvalidateVisual();
    }

    public void SetFrame(double seconds, IImage image) { _frames[Key(seconds)] = image; InvalidateVisual(); }
    public void ClearFrames() { _frames.Clear(); InvalidateVisual(); }
    public static long Key(double seconds) => (long)Math.Round(seconds * 10);
    /// <summary>Times of the filmstrip tiles in the current view.</summary>
    public IReadOnlyList<double> TileTimes()
    {
        var width = LaneWidth; if (width <= 0 || _duration <= 0) return [];
        var tile = StripHeight * 16 / 9; var count = Math.Max(1, (int)Math.Ceiling(width / tile));
        return Enumerable.Range(0, count).Select(i => Math.Min(_duration, _viewStart + (i + .5) * tile / width * VisibleDuration)).ToArray();
    }

    public void ShowAll() { _zoom = 1; _viewStart = 0; InvalidateVisual(); ViewChanged?.Invoke(); }
    public void Reveal(double start, double end)
    {
        if (start >= _viewStart && end <= _viewStart + VisibleDuration) return;
        _viewStart = (start + end) / 2 - VisibleDuration / 2; ClampView(); InvalidateVisual(); ViewChanged?.Invoke();
    }

    private double LaneWidth => Math.Max(0, Bounds.Width - LabelWidth);
    private double X(double seconds) => LabelWidth + (seconds - _viewStart) / Math.Max(1e-9, VisibleDuration) * LaneWidth;
    private double Seconds(double x) => Math.Clamp(_viewStart + (x - LabelWidth) / Math.Max(1, LaneWidth) * VisibleDuration, 0, _duration);
    private double MiniX(double seconds) => LabelWidth + seconds / Math.Max(1e-9, _duration) * LaneWidth;
    private void ClampView() { _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, _duration - VisibleDuration)); }
    private void Follow()
    {
        if (_zoom <= 1 || _drag != DragKind.None) return;
        if (_position < _viewStart || _position > _viewStart + VisibleDuration)
        { _viewStart = _position - VisibleDuration * .1; ClampView(); ViewChanged?.Invoke(); }
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= LabelWidth) return;
        var lane = new Rect(LabelWidth, 0, LaneWidth, TotalHeight);
        DrawLabels(context);
        DrawMinimap(context);
        using (context.PushClip(new Rect(LabelWidth, RulerTop, LaneWidth, TotalHeight - RulerTop)))
        {
            DrawRuler(context); DrawStrip(context); DrawKeep(context); DrawScores(context);
            if (_range is { } range)
            {
                var left = X(range.Start); var right = X(range.End);
                using (context.PushOpacity(.35)) context.FillRectangle(GetValue(AccentProperty)!, new Rect(left, StripTop, Math.Max(1, right - left), TotalHeight - StripTop));
                var pen = new Pen(GetValue(AccentProperty), 1);
                context.DrawLine(pen, new(left, StripTop), new(left, TotalHeight)); context.DrawLine(pen, new(right, StripTop), new(right, TotalHeight));
            }
            DrawPlayhead(context);
        }
        DrawCallout(context, lane);
    }

    private void DrawLabels(DrawingContext context)
    {
        void Label(string text, double top, double height)
        {
            var layout = Text(Localization.Text(text), 11, GetValue(SecondaryProperty));
            context.DrawText(layout, new Point(0, top + (height - layout.Height) / 2));
        }
        Label("画面", StripTop, StripHeight); Label("保留", KeepTop, KeepHeight); Label("人物置信度", ScoreTop, ScoreHeight);
    }

    private void DrawMinimap(DrawingContext context)
    {
        if (_duration <= 0) return;
        var bar = new Rect(LabelWidth, MiniTop, LaneWidth, MiniHeight);
        context.DrawRectangle(GetValue(TrackProperty), null, bar, 4, 4);
        using (context.PushClip(new RoundedRect(bar, 4)))
        {
            foreach (var span in _spans.Where(s => s.Kind == PersonClipSpanKind.Unscanned))
                context.FillRectangle(GetValue(UnscannedProperty)!, new Rect(MiniX(span.Start), bar.Y, Math.Max(1, MiniX(span.End) - MiniX(span.Start)), bar.Height));
            foreach (var segment in _segments)
            {
                var box = new Rect(MiniX(segment.Start), bar.Y, Math.Max(1, MiniX(segment.End) - MiniX(segment.Start)), bar.Height);
                using (context.PushOpacity(.75)) context.DrawRectangle(segment.Keep ? GetValue(AccentProperty) : null, segment.Keep ? null : new Pen(GetValue(AccentProperty), 1), box);
            }
        }
        if (_zoom > 1)
        {
            var view = new Rect(MiniX(_viewStart), MiniTop, Math.Max(6, MiniX(_viewStart + VisibleDuration) - MiniX(_viewStart)), MiniHeight);
            context.DrawRectangle(null, new Pen(GetValue(InkProperty), 1.5), view.Deflate(.75), 3, 3);
        }
        var head = MiniX(_position);
        context.DrawLine(new Pen(GetValue(DangerProperty), 1.5), new(head, MiniTop), new(head, MiniTop + MiniHeight));
    }

    private void DrawRuler(DrawingContext context)
    {
        if (_duration <= 0) return;
        var steps = new[] { .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        var perPixel = VisibleDuration / Math.Max(1, LaneWidth);
        var major = steps.FirstOrDefault(step => step / perPixel >= 70, 3600);
        var minor = major / 5;
        var tick = new Pen(GetValue(HairlineProperty), 1);
        for (var t = Math.Floor(_viewStart / minor) * minor; t <= _viewStart + VisibleDuration + minor; t += minor)
        {
            var x = Math.Round(X(t)) + .5;
            var isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
            context.DrawLine(tick, new(x, RulerTop + RulerHeight - (isMajor ? 6 : 3)), new(x, RulerTop + RulerHeight));
            if (!isMajor) continue;
            var label = Text(major < 1 ? Clock(t, 1) : Clock(t, 0), 10, GetValue(TertiaryProperty));
            context.DrawText(label, new Point(x - label.Width / 2, RulerTop));
        }
    }

    private void DrawStrip(DrawingContext context)
    {
        var rect = new Rect(LabelWidth, StripTop, LaneWidth, StripHeight);
        context.DrawRectangle(GetValue(TrackProperty), null, rect, 4, 4);
        if (_duration <= 0) return;
        var tile = StripHeight * 16 / 9;
        using (context.PushClip(new RoundedRect(rect, 4)))
        {
            var times = TileTimes();
            for (var i = 0; i < times.Count; i++)
            {
                var image = NearestFrame(times[i]);
                if (image is null) continue;
                var target = new Rect(LabelWidth + i * tile, StripTop, tile, StripHeight);
                var size = image.Size; var scale = Math.Max(target.Width / size.Width, target.Height / size.Height);
                var source = new Rect((size.Width - target.Width / scale) / 2, (size.Height - target.Height / scale) / 2, target.Width / scale, target.Height / scale);
                context.DrawImage(image, source, target);
            }
            // Removed time is dimmed so the strip reads like the output.
            using (context.PushOpacity(.55))
                foreach (var span in _spans.Where(span => span.Kind != PersonClipSpanKind.Kept))
                    context.FillRectangle(GetValue(FillProperty)!, new Rect(X(span.Start), StripTop, Math.Max(0, X(span.End) - X(span.Start)), StripHeight));
        }
    }

    private IImage? NearestFrame(double seconds)
    {
        if (_frames.Count == 0) return null;
        if (_frames.TryGetValue(Key(seconds), out var exact)) return exact;
        var key = Key(seconds); long best = 0; var distance = long.MaxValue;
        foreach (var candidate in _frames.Keys) { var d = Math.Abs(candidate - key); if (d < distance) { distance = d; best = candidate; } }
        return _frames[best];
    }

    private (double Start, double End) Shown(PersonClipEditSegment segment) =>
        _drag is DragKind.Start or DragKind.End && segment.Id == _dragId ? (_dragStart, _dragEnd) : (segment.Start, segment.End);

    private void DrawKeep(DrawingContext context)
    {
        var rect = new Rect(LabelWidth, KeepTop, LaneWidth, KeepHeight);
        context.DrawRectangle(GetValue(FillProperty), null, rect, 6, 6);
        var accent = GetValue(AccentProperty); var hairline = new Pen(GetValue(SecondaryProperty), 1);
        var order = _segments.Select((segment, index) => (segment, index)).ToDictionary(item => item.segment.Id, item => item.index + 1);
        foreach (var segment in _segments.OrderBy(s => s.Id == _selected))
        {
            var (start, end) = Shown(segment);
            var left = X(start); var right = X(end);
            if (right < LabelWidth - 20 || left > Bounds.Width + 20) continue;
            var box = new Rect(left, KeepTop + 2, Math.Max(2, right - left), KeepHeight - 4);
            var selected = segment.Id == _selected;
            if (segment.Keep)
            {
                context.DrawRectangle(selected ? accent : GetValue(SoftProperty), null, box, 5, 5);
                context.DrawRectangle(null, new Pen(accent, selected ? 0 : 1.2), box.Deflate(.6), 4, 4);
            }
            else context.DrawRectangle(null, hairline, box.Deflate(.5), 4, 4);
            var ink = selected && segment.Keep ? GetValue(KnobProperty) : segment.Keep ? GetValue(InkProperty) : GetValue(TertiaryProperty);
            var number = order[segment.Id].ToString(CultureInfo.InvariantCulture);
            var caption = box.Width > 92 && selected ? number + " · " + Localization.Format($"{MsUi.Number(Math.Round(end - start, 1))} 秒") : number;
            var label = Text(caption, 11, ink, FontWeight.SemiBold);
            if (label.Width + 10 < box.Width)
            {
                var origin = new Point(box.X + 8, box.Y + (box.Height - label.Height) / 2);
                context.DrawText(label, origin);
                if (!segment.Keep) context.DrawLine(new Pen(GetValue(TertiaryProperty), 1), new(origin.X - 1, origin.Y + label.Height / 2), new(origin.X + label.Width + 1, origin.Y + label.Height / 2));
            }
            if (segment.Origin == PersonClipSegmentOrigin.Manual)
                context.FillRectangle(GetValue(WarningProperty)!, new Rect(box.X + 1, box.Bottom - 3, Math.Max(0, box.Width - 2), 3));
            if (segment.Adjusted)
                context.DrawEllipse(GetValue(WarningProperty), null, new Point(box.Right - 6, box.Y + 5), 2.5, 2.5);
            if (selected)
                foreach (var x in new[] { box.X, box.Right })
                {
                    var handle = new Rect(x - HandleWidth / 2, KeepTop - 3, HandleWidth, KeepHeight + 6);
                    var dragging = segment.Id == _dragId && (_drag == DragKind.Start && x == box.X || _drag == DragKind.End && x == box.Right);
                    context.DrawRectangle(GetValue(dragging ? AccentProperty : KnobProperty), new Pen(GetValue(HairlineProperty), .5), handle, 4, 4);
                    context.DrawLine(new Pen(GetValue(dragging ? KnobProperty : AccentProperty), 1.5), new(x, handle.Center.Y - 6), new(x, handle.Center.Y + 6));
                }
        }
    }

    private void DrawScores(DrawingContext context)
    {
        var rect = new Rect(LabelWidth, ScoreTop, LaneWidth, ScoreHeight);
        context.DrawRectangle(GetValue(FillProperty), null, rect, 6, 6);
        if (_duration <= 0) return;
        using (context.PushClip(new RoundedRect(rect, 6)))
        {
            double Y(double score) => ScoreTop + ScoreHeight - 2 - Math.Clamp(score, 0, 1) * (ScoreHeight - 6);
            if (_samples.Count > 1)
            {
                var visible = _samples.Where(s => s.Seconds >= _viewStart - VisibleDuration * .05 && s.Seconds <= _viewStart + VisibleDuration * 1.05).ToArray();
                var step = Math.Max(1, visible.Length / Math.Max(1, (int)LaneWidth));
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var started = false; double lastX = 0;
                    for (var i = 0; i < visible.Length; i += step)
                    {
                        var sample = visible[i]; var x = X(sample.Seconds);
                        var y = sample.Analysed ? Y(sample.Score) : ScoreTop + ScoreHeight;
                        if (!started) { g.BeginFigure(new Point(x, ScoreTop + ScoreHeight), true); started = true; }
                        g.LineTo(new Point(x, y)); lastX = x;
                    }
                    if (started) { g.LineTo(new Point(lastX, ScoreTop + ScoreHeight)); g.EndFigure(true); }
                }
                var thresholdY = Y(_threshold);
                using (context.PushClip(new Rect(LabelWidth, ScoreTop, LaneWidth, thresholdY - ScoreTop)))
                using (context.PushOpacity(.85)) context.DrawGeometry(GetValue(AccentProperty), null, geometry);
                using (context.PushClip(new Rect(LabelWidth, thresholdY, LaneWidth, ScoreTop + ScoreHeight - thresholdY)))
                using (context.PushOpacity(.55)) context.DrawGeometry(GetValue(TertiaryProperty), null, geometry);
                foreach (var sample in visible.Where(s => (s.Flags & PersonClipSampleFlags.Uncertain) != 0))
                    context.DrawEllipse(GetValue(WarningProperty), null, new Point(X(sample.Seconds), Y(sample.Score)), 2.5, 2.5);
            }
            else
            {
                // Results saved before per-sample scores: show the detected spans as a flat band instead of a curve.
                foreach (var span in _spans.Where(span => span.Kind is PersonClipSpanKind.Kept or PersonClipSpanKind.RemovedByUser))
                    using (context.PushOpacity(.45))
                        context.FillRectangle(GetValue(AccentProperty)!, new Rect(X(span.Start), Y(_threshold + .3), Math.Max(1, X(span.End) - X(span.Start)), ScoreTop + ScoreHeight - Y(_threshold + .3)));
            }
            foreach (var span in _spans.Where(span => span.Kind == PersonClipSpanKind.Unscanned))
            {
                var box = new Rect(X(span.Start), ScoreTop, Math.Max(1, X(span.End) - X(span.Start)), ScoreHeight);
                context.FillRectangle(GetValue(UnscannedProperty)!, box);
                var name = span.Reason switch { PersonClipUnscannedReason.Dark => "黑灯画面", PersonClipUnscannedReason.Blank => "无画面", PersonClipUnscannedReason.Excluded => "排除区间", _ => "未检测" };
                var caption = Text(Localization.Text(name), 10, GetValue(SecondaryProperty));
                if (box.Width > caption.Width + 8) context.DrawText(caption, new(box.Center.X - caption.Width / 2, box.Center.Y - caption.Height / 2));
            }
            for (var i = 0; i < _exclusions.Count; i++)
            {
                var exclusion = _exclusions[i];
                var (a, b) = i == _dragId && _drag is DragKind.ExclusionStart or DragKind.ExclusionEnd ? (_dragStart, _dragEnd) : (exclusion.Start, exclusion.End);
                var box = new Rect(X(a), ScoreTop, Math.Max(1, X(b) - X(a)), ScoreHeight);
                context.FillRectangle(GetValue(ExclusionFillProperty)!, box);
                if (i == _selectedExclusion || i == _hoverExclusion) context.DrawRectangle(null, new Pen(GetValue(DangerProperty), 1), box.Deflate(.5), 4, 4);
                var caption = Text(Localization.Text("排除区间"), 10, GetValue(DangerProperty));
                if (caption.Width + 8 < box.Width) context.DrawText(caption, new(box.Center.X - caption.Width / 2, box.Center.Y - caption.Height / 2));
                if (i == _selectedExclusion)
                    foreach (var edge in new[] { box.Left, box.Right }) context.DrawLine(new Pen(GetValue(DangerProperty), 3), new(edge, box.Top + 8), new(edge, box.Bottom - 8));
            }
            if (_samples.Count == 0)
            {
                var note = Text(Localization.Text("此结果未保存逐帧分数 · 重新检测后显示曲线"), 11, GetValue(SecondaryProperty));
                context.DrawText(note, new(LabelWidth + 8, ScoreTop + 3));
            }
            var threshold = Y(_threshold);
            context.DrawLine(new Pen(GetValue(WarningProperty), 1), new(LabelWidth, threshold), new(Bounds.Width, threshold));
            var label = Text(Localization.Format($"阈值 {_threshold.ToString("0.00", CultureInfo.InvariantCulture)}"), 10, GetValue(WarningProperty));
            context.DrawText(label, new Point(Bounds.Width - label.Width - 6, threshold - label.Height - 1));
        }
    }

    private void DrawPlayhead(DrawingContext context)
    {
        if (_duration <= 0) return;
        var x = X(_position);
        context.DrawLine(new Pen(GetValue(DangerProperty), 1.5), new(x, RulerTop + 6), new(x, TotalHeight));
        var head = new StreamGeometry();
        using (var g = head.Open()) { g.BeginFigure(new(x - 5, RulerTop + 4), true); g.LineTo(new(x + 5, RulerTop + 4)); g.LineTo(new(x, RulerTop + 10)); g.EndFigure(true); }
        context.DrawGeometry(GetValue(DangerProperty), null, head);
    }

    private void DrawCallout(DrawingContext context, Rect lane)
    {
        if (_drag is not (DragKind.Start or DragKind.End)) return;
        var seconds = _drag == DragKind.Start ? _dragStart : _dragEnd;
        var heading = Text(Localization.Text(_drag == DragKind.Start ? "起点" : "终点") + " " + Clock(seconds, 3), 11, GetValue(InkProperty), FontWeight.SemiBold);
        var label = Text(_dragSnap is { } snap ? Localization.Text("吸附") + " · " + Localization.Text(snap.Label) : "", 10, GetValue(WarningProperty));
        var width = Math.Max(heading.Width, label.Width) + 82;
        var x = Math.Clamp(X(seconds) - width / 2, lane.X, Math.Max(lane.X, Bounds.Width - width));
        var bubble = new Rect(x, RulerTop, width, 44);
        context.DrawRectangle(GetValue(CardProperty), new Pen(GetValue(HairlineProperty), .5), bubble, 8, 8);
        var frame = DragFrame ?? NearestFrame(seconds);
        if (frame is not null) context.DrawImage(frame, new Rect(frame.Size), new Rect(x + 6, bubble.Y + 7, 54, 30));
        context.DrawText(heading, new(x + 68, bubble.Y + 6));
        context.DrawText(label, new(x + 68, bubble.Y + 23));
        if (_dragSnap is { } target)
        {
            var guide = X(target.Seconds);
            context.DrawLine(new Pen(GetValue(WarningProperty), 1.5), new(guide, bubble.Bottom), new(guide, TotalHeight));
        }
    }

    private static FormattedText Text(string text, double size, IBrush? brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, brush!);

    public static string Clock(double seconds, int decimals)
    {
        seconds = Math.Max(0, seconds);
        var scale = Math.Pow(10, decimals); var rounded = Math.Round(seconds * scale) / scale;
        var minutes = (int)(rounded / 60); var rest = rounded - minutes * 60;
        var format = decimals == 0 ? "00" : "00." + new string('0', decimals);
        return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + rest.ToString(format, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- input
    private int? HitSegment(Point point)
    {
        if (point.Y < KeepTop || point.Y > KeepTop + KeepHeight) return null;
        var seconds = Seconds(point.X);
        return _segments.FirstOrDefault(s => seconds >= s.Start && seconds <= s.End)?.Id;
    }
    private (int Id, bool Start)? HitHandle(Point point)
    {
        if (point.Y < KeepTop - 4 || point.Y > KeepTop + KeepHeight + 4) return null;
        (int, bool)? best = null; var distance = 7.0;
        foreach (var segment in _segments)
        {
            var priority = segment.Id == _selected ? 3 : 0;
            foreach (var (x, start) in new[] { (X(segment.Start), true), (X(segment.End), false) })
            {
                var d = Math.Abs(point.X - x) - priority;
                if (d < distance) { distance = d; best = (segment.Id, start); }
            }
        }
        return best;
    }
    private int HitExclusion(Point point)
    {
        if (point.Y < ScoreTop || point.Y > ScoreTop + ScoreHeight) return -1;
        var seconds = Seconds(point.X);
        for (var i = 0; i < _exclusions.Count; i++) if (seconds >= _exclusions[i].Start && seconds <= _exclusions[i].End) return i;
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_duration <= 0) return;
        var point = e.GetPosition(this);
        if (point.X < LabelWidth - 4) return;
        var properties = e.GetCurrentPoint(this).Properties;
        Focus();
        if (properties.IsRightButtonPressed)
        {
            var exclusion = HitExclusion(point);
            if (exclusion >= 0) ExclusionMenuRequested?.Invoke(exclusion, point);
            else SegmentMenuRequested?.Invoke(HitSegment(point), Seconds(point.X), point);
            e.Handled = true; return;
        }
        if (!properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this); e.Handled = true;
        if (point.Y <= MiniTop + MiniHeight + 4)
        {
            _drag = DragKind.Minimap; CenterMinimap(point.X); return;
        }
        if (HitHandle(point) is { } handle)
        {
            var segment = _segments.First(s => s.Id == handle.Id);
            _drag = handle.Start ? DragKind.Start : DragKind.End; _dragId = handle.Id;
            _dragStart = segment.Start; _dragEnd = segment.End; _dragSnap = null;
            if (_selected != handle.Id) SegmentClicked?.Invoke(handle.Id);
            InvalidateVisual(); return;
        }
        if (HitSegment(point) is { } id)
        {
            _drag = DragKind.None; SegmentClicked?.Invoke(id); Seek?.Invoke(Seconds(point.X)); return;
        }
        if (_selectedExclusion >= 0 && _selectedExclusion < _exclusions.Count && point.Y >= ScoreTop)
        {
            var exclusion = _exclusions[_selectedExclusion];
            if (Math.Abs(point.X - X(exclusion.Start)) <= 8 || Math.Abs(point.X - X(exclusion.End)) <= 8)
            {
                _dragId = _selectedExclusion; _dragStart = exclusion.Start; _dragEnd = exclusion.End;
                _drag = Math.Abs(point.X - X(exclusion.Start)) < Math.Abs(point.X - X(exclusion.End)) ? DragKind.ExclusionStart : DragKind.ExclusionEnd;
                return;
            }
        }
        if (HitExclusion(point) is var clickedExclusion && clickedExclusion >= 0)
        { _selectedExclusion = clickedExclusion; _range = null; RangeChanged?.Invoke(null); InvalidateVisual(); return; }
        _selectedExclusion = -1;
        if (point.Y >= ScoreTop)
        {
            _drag = DragKind.Range; _dragAnchor = Seconds(point.X); _range = null; RangeChanged?.Invoke(null);
            Seek?.Invoke(_dragAnchor); InvalidateVisual(); return;
        }
        _drag = DragKind.Seek; Seek?.Invoke(Seconds(point.X));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        switch (_drag)
        {
            case DragKind.Seek: Seek?.Invoke(Seconds(point.X)); return;
            case DragKind.Minimap: CenterMinimap(point.X); return;
            case DragKind.Range:
            {
                var at = Seconds(point.X);
                _range = Math.Abs(at - _dragAnchor) * LaneWidth / Math.Max(1e-9, VisibleDuration) < 3 ? null : (Math.Min(at, _dragAnchor), Math.Max(at, _dragAnchor));
                InvalidateVisual(); return;
            }
            case DragKind.ExclusionStart or DragKind.ExclusionEnd:
            {
                var at = Seconds(point.X);
                if (_drag == DragKind.ExclusionStart) _dragStart = Math.Clamp(at, 0, Math.Max(0, _dragEnd - MinimumSeconds));
                else _dragEnd = Math.Clamp(at, Math.Min(_duration, _dragStart + MinimumSeconds), _duration);
                InvalidateVisual(); return;
            }
            case DragKind.Start or DragKind.End:
            {
                var at = Seconds(point.X);
                var tolerance = 8 * VisibleDuration / Math.Max(1, LaneWidth);
                _dragSnap = SnapEnabled && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? Snap?.Invoke(at, tolerance, _dragId) : null;
                if (_dragSnap is { } snap) at = snap.Seconds;
                var segment = _segments.First(s => s.Id == _dragId);
                var index = _segments.OrderBy(s => s.Start).ToList().FindIndex(s => s.Id == _dragId);
                var ordered = _segments.OrderBy(s => s.Start).ToList();
                var lower = index > 0 ? ordered[index - 1].End : 0;
                var upper = index < ordered.Count - 1 ? ordered[index + 1].Start : _duration;
                if (_drag == DragKind.Start) _dragStart = Math.Clamp(at, lower, segment.End - MinimumSeconds);
                else _dragEnd = Math.Clamp(at, segment.Start + MinimumSeconds, upper);
                BoundsPreview?.Invoke(_dragId, _dragStart, _dragEnd, _dragSnap);
                InvalidateVisual(); return;
            }
        }
        var hover = HitExclusion(point);
        if (hover != _hoverExclusion) { _hoverExclusion = hover; InvalidateVisual(); }
        Cursor = HitHandle(point) is not null ? new Cursor(StandardCursorType.SizeWestEast) : point.Y >= ScoreTop ? new Cursor(StandardCursorType.Ibeam) : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag; _drag = DragKind.None; e.Pointer.Capture(null);
        if (drag is DragKind.ExclusionStart or DragKind.ExclusionEnd) { ExclusionBoundsCommitted?.Invoke(_dragId, _dragStart, _dragEnd); InvalidateVisual(); }
        if (drag is DragKind.Start or DragKind.End)
        {
            var segment = _segments.FirstOrDefault(s => s.Id == _dragId);
            _dragSnap = null; InvalidateVisual();
            if (segment is not null && (Math.Abs(segment.Start - _dragStart) > 1e-6 || Math.Abs(segment.End - _dragEnd) > 1e-6))
                BoundsCommitted?.Invoke(_dragId, _dragStart, _dragEnd);
            else DragCancelled?.Invoke();
        }
        if (drag == DragKind.Range) RangeChanged?.Invoke(_range);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is DragKind.Start or DragKind.End) { _drag = DragKind.None; _dragSnap = null; InvalidateVisual(); DragCancelled?.Invoke(); }
        else _drag = DragKind.None;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverExclusion != -1) { _hoverExclusion = -1; InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_duration <= 0) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            var anchor = Seconds(e.GetPosition(this).X); var ratio = (anchor - _viewStart) / Math.Max(1e-9, VisibleDuration);
            _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.25 : .8), 1, 64);
            _viewStart = anchor - ratio * VisibleDuration;
        }
        else _viewStart += -(Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y) * VisibleDuration * .1;
        ClampView(); InvalidateVisual(); ViewChanged?.Invoke(); e.Handled = true;
    }

    private void CenterMinimap(double x)
    {
        var seconds = Math.Clamp((x - LabelWidth) / Math.Max(1, LaneWidth), 0, 1) * _duration;
        if (_zoom <= 1) { Seek?.Invoke(seconds); return; }
        _viewStart = seconds - VisibleDuration / 2; ClampView(); InvalidateVisual(); ViewChanged?.Invoke();
    }
}
