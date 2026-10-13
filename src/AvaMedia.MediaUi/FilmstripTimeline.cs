using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.MediaUi;

/// <summary>
/// Shared source-time filmstrip: ruler, thumbnail strip, segment boxes with in/out handles and a playhead.
/// Hovering or dragging a handle of the selected segment shows a callout with the nearest strip frame and
/// the "起点 / 终点" timecode. Zoom scales the drawn width; host it in a horizontal ScrollViewer.
/// Brushes come from styled properties so every skin can supply its own resources.
/// </summary>
public sealed class FilmstripTimeline : Control
{
    public sealed record Segment(double Start, double End, string Label);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(AccentBrush), Brushes.DodgerBlue);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(TrackBrush), Brushes.LightGray);
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(TextBrush), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(PlayheadBrush), Brushes.Red);
    public static readonly StyledProperty<IBrush?> RemoveBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(RemoveBrush), Brushes.OrangeRed);
    public static readonly StyledProperty<IBrush?> CalloutBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(CalloutBrush), Brushes.White);
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public IBrush? RemoveBrush { get => GetValue(RemoveBrushProperty); set => SetValue(RemoveBrushProperty, value); }
    public static readonly StyledProperty<IBrush?> CalloutTextBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(CalloutTextBrush), Brushes.Black);
    public IBrush? CalloutTextBrush { get => GetValue(CalloutTextBrushProperty); set => SetValue(CalloutTextBrushProperty, value); }
    public IBrush? CalloutBrush { get => GetValue(CalloutBrushProperty); set => SetValue(CalloutBrushProperty, value); }
    static FilmstripTimeline() => AffectsRender<FilmstripTimeline>(AccentBrushProperty, TrackBrushProperty, TextBrushProperty, PlayheadBrushProperty, RemoveBrushProperty, CalloutBrushProperty);

    private IReadOnlyList<Segment> _segments = [];
    private IReadOnlyList<(double Seconds, IImage Image)> _strip = [];
    private double _duration, _position, _dragValue, _zoom = 1, _viewStart;
    private int _selected = -1, _dragIndex = -1, _hoverIndex = -1;
    private bool _dragStart, _hoverStart, _seeking, _completeSeek;
    private double _seekValue;
    private const double RulerHeight = 14, StripTop = RulerHeight, StripHeight = 52;

    public bool RemoveMode { get; set; }
    /// <summary>Localizes the callout edge names (起点 / 终点).</summary>
    public Func<string, string> Translate { get; set; } = text => text;
    /// <summary>Seek to a time; the int is the segment under the pointer or -1.</summary>
    public event Action<int, double>? NavigateRequested;
    public event Action<double>? NavigateCompleted;
    /// <summary>(index, isStart, seconds, finished)</summary>
    public event Action<int, bool, double, bool>? HandleMoved;

    public FilmstripTimeline() { Height = StripTop + StripHeight + 4; ClipToBounds = false; Cursor = new Cursor(StandardCursorType.Hand); }
    public double Duration { get => _duration; set { _duration = value; ClampView(); InvalidateVisual(); } }
    public double Position { get => _position; set { _position = value; FollowPlayhead(); InvalidateVisual(); } }
    /// <summary>1 = whole source visible; larger values show duration / zoom seconds around the playhead.</summary>
    public double Zoom { get => _zoom; set { _zoom = Math.Clamp(value, 1, 64); _viewStart = _position - VisibleDuration / 2; ClampView(); InvalidateVisual(); } }
    public int Selected => _selected;
    /// <summary>Show the handle callout of the selected segment without a pointer (design review captures).</summary>
    public void ShowHandleCallout(bool start) { _hoverIndex = _selected; _hoverStart = start; InvalidateVisual(); }
    public bool Dragging => _dragIndex >= 0;
    public void SetSegments(IReadOnlyList<Segment> segments, int selected) { _segments = segments; _selected = selected; InvalidateVisual(); }
    public void SetStrip(IReadOnlyList<(double Seconds, IImage Image)> strip) { _strip = strip; InvalidateVisual(); }
    public IImage? NearestFrame(double seconds) => _strip.Count == 0 ? null : _strip.MinBy(item => Math.Abs(item.Seconds - seconds)).Image;
    public double OffsetOf(double seconds) => X(seconds);

    private double VisibleDuration => _duration / _zoom;
    private void ClampView() => _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, _duration - VisibleDuration));
    private void FollowPlayhead()
    {
        if (_zoom <= 1 || _dragIndex >= 0) return;
        if (_position < _viewStart || _position > _viewStart + VisibleDuration) { _viewStart = _position - VisibleDuration * .1; ClampView(); }
    }
    private Rect Track => new(4, StripTop, Math.Max(1, Bounds.Width - 8), StripHeight);
    private double X(double seconds) => Track.X + (_duration <= 0 ? 0 : (seconds - _viewStart) / VisibleDuration * Track.Width);
    private double Time(double x) => _duration <= 0 ? 0 : Math.Clamp(_viewStart + (x - Track.X) / Track.Width * VisibleDuration, 0, _duration);

    private static double TickStep(double visible, double width)
    {
        foreach (var step in new double[] { .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 })
            if (step / Math.Max(visible, 1e-6) * width >= 70) return step;
        return 7200;
    }

    public override void Render(DrawingContext context)
    {
        var track = Track; var text = TextBrush ?? Brushes.Gray; var accent = AccentBrush ?? Brushes.DodgerBlue;
        if (_duration <= 0) { context.DrawRectangle(TrackBrush, null, track, 8, 8); return; }
        var step = TickStep(VisibleDuration, track.Width);
        for (var seconds = Math.Ceiling(_viewStart / step) * step; seconds <= _viewStart + VisibleDuration + 1e-6; seconds += step)
        {
            var label = Format(Tick(seconds, step), 10, text);
            context.DrawText(label, new Point(Math.Min(X(seconds), Bounds.Width - label.Width), 0));
        }
        using (context.PushClip(new RoundedRect(track, 8)))
        {
            context.FillRectangle(TrackBrush ?? Brushes.LightGray, track);
            if (_strip.Count > 0)
            {
                var cell = track.Height * 1.25;
                var cells = (int)Math.Ceiling(track.Width / cell);
                for (var index = 0; index < cells; index++)
                {
                    var target = new Rect(track.X + cell * index, track.Y, cell, track.Height);
                    var image = NearestFrame(Time(target.Center.X))!;
                    var scale = Math.Max(target.Width / image.Size.Width, target.Height / image.Size.Height);
                    var source = new Size(target.Width / scale, target.Height / scale);
                    context.DrawImage(image, new Rect((image.Size.Width - source.Width) / 2, (image.Size.Height - source.Height) / 2, source.Width, source.Height), target);
                    context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)), 1), target.TopRight, target.BottomRight);
                }
            }
            // Unselected source is dimmed so the segment boxes read as "kept".
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(RemoveMode ? (byte)40 : (byte)110, 255, 255, 255)), track);
        }
        Rect? selectedBox = null;
        using (context.PushClip(new Rect(0, track.Y - 4, Bounds.Width, track.Height + 8)))
            for (var index = 0; index < _segments.Count; index++)
            {
                var segment = _segments[index]; var selected = index == _selected;
                var start = index == _dragIndex && _dragStart ? _dragValue : segment.Start;
                var end = index == _dragIndex && !_dragStart ? _dragValue : segment.End;
                var box = new Rect(X(start), track.Y - 2, Math.Max(3, X(end) - X(start)), track.Height + 4);
                var stroke = RemoveMode ? RemoveBrush ?? Brushes.OrangeRed : selected ? accent : Brushes.White;
                var color = ((stroke as ISolidColorBrush)?.Color ?? Colors.White);
                context.DrawRectangle(new SolidColorBrush(color, selected ? .15 : .13), new Pen(stroke, selected ? 3 : 2), box.Deflate(selected ? 1.5 : 1), 7, 7);
                var label = Format(segment.Label, 10.5, Brushes.White, true);
                using (context.PushClip(box)) { context.DrawText(Format(segment.Label, 10.5, new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), true), new Point(box.X + 6, box.Y + 5)); context.DrawText(label, new Point(box.X + 6, box.Y + 4)); }
                if (selected) selectedBox = box;
            }
        if (selectedBox is { } chosen)
        {
            var handle = RemoveMode ? RemoveBrush ?? Brushes.OrangeRed : accent;
            foreach (var x in new[] { chosen.X, chosen.Right })
                if (x >= -6 && x <= Bounds.Width + 6) context.DrawRectangle(handle, null, new Rect(x - 5.5, track.Y + track.Height / 2 - 17, 11, 34), 4, 4);
        }
        var head = X(_position);
        if (head >= track.X - 1 && head <= track.Right + 1)
        {
            var playhead = PlayheadBrush ?? Brushes.Red;
            context.FillRectangle(playhead, new Rect(head - 1, track.Y - 20, 2, track.Height + 26));
            var cap = new StreamGeometry();
            using (var g = cap.Open()) { g.BeginFigure(new Point(head - 6, track.Y - 20), true); g.LineTo(new Point(head + 6, track.Y - 20)); g.LineTo(new Point(head, track.Y - 10)); g.EndFigure(true); }
            context.DrawGeometry(playhead, null, cap);
        }
        if (_dragIndex >= 0) DrawCallout(context, X(_dragValue), _dragValue, _dragStart);
        else if (_hoverIndex >= 0 && _hoverIndex < _segments.Count)
        {
            var value = _hoverStart ? _segments[_hoverIndex].Start : _segments[_hoverIndex].End;
            DrawCallout(context, X(value), value, _hoverStart);
        }
    }

    /// <summary>Callout above the handle: white card with the nearest strip frame and "起点 01:11.000".</summary>
    private void DrawCallout(DrawingContext context, double x, double seconds, bool start)
    {
        var frame = NearestFrame(seconds); const double width = 110, imageHeight = 58, height = 82;
        var box = new Rect(Math.Clamp(x - width / 2, 0, Math.Max(0, Bounds.Width - width)), Track.Y - 10 - height, width, height);
        var pointer = new StreamGeometry();
        using (var g = pointer.Open()) { g.BeginFigure(new Point(x - 6, box.Bottom - 1), true); g.LineTo(new Point(x + 6, box.Bottom - 1)); g.LineTo(new Point(x, box.Bottom + 5)); g.EndFigure(true); }
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, box.Translate(new Vector(0, 3)).Inflate(1), 9, 9);
        context.DrawRectangle(CalloutBrush, new Pen(new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), .5), box, 8, 8);
        context.DrawGeometry(CalloutBrush, null, pointer);
        var imageRect = new Rect(box.X + 3, box.Y + 3, width - 6, imageHeight);
        if (frame is not null)
            using (context.PushClip(new RoundedRect(imageRect, 6)))
            {
                var scale = Math.Max(imageRect.Width / frame.Size.Width, imageRect.Height / frame.Size.Height);
                var source = new Size(imageRect.Width / scale, imageRect.Height / scale);
                context.DrawImage(frame, new Rect((frame.Size.Width - source.Width) / 2, (frame.Size.Height - source.Height) / 2, source.Width, source.Height), imageRect);
            }
        else context.DrawRectangle(TrackBrush, null, imageRect, 6, 6);
        var label = Format(Translate(start ? "起点" : "终点") + " " + Short2(seconds), 10.5, CalloutTextBrush ?? Brushes.Black, true);
        context.DrawText(label, new Point(box.X + (width - label.Width) / 2, imageRect.Bottom + 3));
    }

    private (int Index, bool Start)? HandleAt(Point point)
    {
        if (_selected < 0 || _selected >= _segments.Count || point.Y < StripTop - 8) return null;
        var segment = _segments[_selected];
        if (Math.Abs(point.X - X(segment.Start)) <= 8) return (_selected, true);
        if (Math.Abs(point.X - X(segment.End)) <= 8) return (_selected, false);
        return null;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_zoom <= 1) return;
        _viewStart -= (e.Delta.Y + e.Delta.X) * VisibleDuration * .1; ClampView(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        if (HandleAt(point) is { } handle)
        {
            _dragIndex = handle.Index; _dragStart = handle.Start; _hoverIndex = -1;
            _dragValue = handle.Start ? _segments[handle.Index].Start : _segments[handle.Index].End;
            e.Pointer.Capture(this); e.Handled = true; InvalidateVisual(); return;
        }
        var time = Time(point.X);
        var hit = point.Y >= StripTop ? Enumerable.Range(0, _segments.Count).Where(index => _segments[index].Start <= time && time <= _segments[index].End).DefaultIfEmpty(-1).First() : -1;
        _seeking = true; _completeSeek = hit < 0; _seekValue = time; e.Pointer.Capture(this); NavigateRequested?.Invoke(hit, time); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this); var time = Time(point.X);
        if (_dragIndex >= 0)
        {
            var segment = _segments[_dragIndex]; var minimum = Math.Max(.04, _duration / 4000);
            _dragValue = _dragStart ? Math.Clamp(time, 0, segment.End - minimum) : Math.Clamp(time, segment.Start + minimum, _duration);
            HandleMoved?.Invoke(_dragIndex, _dragStart, _dragValue, false); InvalidateVisual();
        }
        else if (_seeking) { _completeSeek = true; _seekValue = time; NavigateRequested?.Invoke(-1, time); }
        else
        {
            var handle = HandleAt(point); var index = handle?.Index ?? -1; var start = handle?.Start ?? false;
            Cursor = new Cursor(handle is null ? StandardCursorType.Hand : StandardCursorType.SizeWestEast);
            if (index != _hoverIndex || start != _hoverStart) { _hoverIndex = index; _hoverStart = start; InvalidateVisual(); }
        }
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (_hoverIndex >= 0) { _hoverIndex = -1; InvalidateVisual(); } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex >= 0) { var index = _dragIndex; _dragIndex = -1; HandleMoved?.Invoke(index, _dragStart, _dragValue, true); InvalidateVisual(); }
        CompleteSeek(); e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { _dragIndex = -1; CompleteSeek(); InvalidateVisual(); base.OnPointerCaptureLost(e); }
    private void CompleteSeek()
    {
        var complete = _seeking && _completeSeek; _seeking = _completeSeek = false;
        if (complete) NavigateCompleted?.Invoke(_seekValue);
    }

    private static FormattedText Format(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);
    private static string Tick(double seconds, double step) => step < 1 ? Compact(seconds).TrimEnd('0').TrimEnd('.') : Short(seconds);
    /// <summary>mm:ss.fff as in the demo callout.</summary>
    public static string Short2(double seconds) => Long(seconds);
    public static string Short(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}"; }
    /// <summary>m:ss.fff (h:mm:ss.fff past an hour), as in the segment list.</summary>
    public static string Compact(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}" : $"{time.Minutes}:{time.Seconds:00}.{time.Milliseconds:000}"; }
    public static string Long(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return (time.TotalHours >= 1 ? $"{(int)time.TotalHours}:" : "") + $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"; }
}
