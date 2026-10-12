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
    public static readonly StyledProperty<IBrush?> CalloutBrushProperty = AvaloniaProperty.Register<FilmstripTimeline, IBrush?>(nameof(CalloutBrush), new SolidColorBrush(Color.Parse("#1C1C1E")));
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public IBrush? RemoveBrush { get => GetValue(RemoveBrushProperty); set => SetValue(RemoveBrushProperty, value); }
    public IBrush? CalloutBrush { get => GetValue(CalloutBrushProperty); set => SetValue(CalloutBrushProperty, value); }
    static FilmstripTimeline() => AffectsRender<FilmstripTimeline>(AccentBrushProperty, TrackBrushProperty, TextBrushProperty, PlayheadBrushProperty, RemoveBrushProperty, CalloutBrushProperty);

    private IReadOnlyList<Segment> _segments = [];
    private IReadOnlyList<(double Seconds, IImage Image)> _strip = [];
    private double _duration, _position, _dragValue;
    private int _selected = -1, _dragIndex = -1, _hoverIndex = -1;
    private bool _dragStart, _hoverStart, _seeking;
    private const double RulerHeight = 16, LabelHeight = 16, StripTop = RulerHeight + LabelHeight, StripHeight = 48;
    public const double CalloutHeight = 86;

    public bool RemoveMode { get; set; }
    /// <summary>Localizes the callout edge names (起点 / 终点).</summary>
    public Func<string, string> Translate { get; set; } = text => text;
    /// <summary>Seek to a time; the int is the segment under the pointer or -1.</summary>
    public event Action<int, double>? NavigateRequested;
    /// <summary>(index, isStart, seconds, finished)</summary>
    public event Action<int, bool, double, bool>? HandleMoved;

    public FilmstripTimeline() { Height = StripTop + StripHeight + 6; ClipToBounds = false; Cursor = new Cursor(StandardCursorType.Hand); }
    public double Duration { get => _duration; set { _duration = value; InvalidateVisual(); } }
    public double Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public int Selected => _selected;
    public bool Dragging => _dragIndex >= 0;
    public void SetSegments(IReadOnlyList<Segment> segments, int selected) { _segments = segments; _selected = selected; InvalidateVisual(); }
    public void SetStrip(IReadOnlyList<(double Seconds, IImage Image)> strip) { _strip = strip; InvalidateVisual(); }
    public IImage? NearestFrame(double seconds) => _strip.Count == 0 ? null : _strip.MinBy(item => Math.Abs(item.Seconds - seconds)).Image;
    /// <summary>Horizontal offset of a time, for keeping the playhead visible when zoomed.</summary>
    public double OffsetOf(double seconds) => X(seconds);

    private Rect Track => new(6, StripTop, Math.Max(1, Bounds.Width - 12), StripHeight);
    private double X(double seconds) => Track.X + (_duration <= 0 ? 0 : Math.Clamp(seconds / _duration, 0, 1) * Track.Width);
    private double Time(double x) => _duration <= 0 ? 0 : Math.Clamp((x - Track.X) / Track.Width, 0, 1) * _duration;

    public override void Render(DrawingContext context)
    {
        var track = Track; var text = TextBrush ?? Brushes.Gray; var accent = AccentBrush ?? Brushes.DodgerBlue;
        // Ruler: one tick per ~90 px so zooming reveals finer labels.
        var ticks = Math.Clamp((int)(track.Width / 90), 2, 60);
        for (var index = 0; index <= ticks; index++)
        {
            var seconds = _duration * index / ticks; var label = Format(Short(seconds), 10.5, text);
            var x = Math.Clamp(X(seconds) - (index == 0 ? 6 : index == ticks ? label.Width - 6 : label.Width / 2), 0, Math.Max(0, Bounds.Width - label.Width));
            context.DrawText(label, new Point(x, 0));
        }
        using (context.PushClip(new RoundedRect(track, 6)))
        {
            context.FillRectangle(TrackBrush ?? Brushes.LightGray, track);
            if (_strip.Count > 0)
            {
                // Repeat frames so cells stay roughly 16:9 at any zoom.
                var cells = Math.Max(_strip.Count, (int)Math.Ceiling(track.Width / (track.Height * 16 / 9)));
                var cell = track.Width / cells;
                for (var index = 0; index < cells; index++)
                {
                    var image = NearestFrame(_duration * (index + .5) / cells)!;
                    var target = new Rect(track.X + cell * index, track.Y, cell + .5, track.Height);
                    var scale = Math.Max(target.Width / image.Size.Width, target.Height / image.Size.Height);
                    var source = new Size(target.Width / scale, target.Height / scale);
                    context.DrawImage(image, new Rect((image.Size.Width - source.Width) / 2, (image.Size.Height - source.Height) / 2, source.Width, source.Height), target);
                    context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1), target.TopLeft, target.BottomLeft);
                }
            }
            if (!RemoveMode) context.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), track);
        }
        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index]; var selected = index == _selected;
            var start = index == _dragIndex && _dragStart ? _dragValue : segment.Start;
            var end = index == _dragIndex && !_dragStart ? _dragValue : segment.End;
            var stroke = RemoveMode ? RemoveBrush ?? Brushes.OrangeRed : accent;
            var box = new Rect(X(start), track.Y - 1, Math.Max(2, X(end) - X(start)), track.Height + 2);
            var color = (stroke as ISolidColorBrush)?.Color ?? Colors.DodgerBlue;
            context.DrawRectangle(new SolidColorBrush(color, selected ? .2 : .1), new Pen(stroke, selected ? 2.4 : 1.4), box, 5, 5);
            var label = Format(segment.Label, 10.5, stroke, true);
            context.DrawText(label, new Point(Math.Clamp(box.X + 4, 0, Math.Max(0, Bounds.Width - label.Width)), RulerHeight + 1));
            if (selected)
                foreach (var x in new[] { box.X, box.Right })
                    context.DrawRectangle(stroke, new Pen(Brushes.White, 1), new Rect(x - 4, track.Y + track.Height / 2 - 12, 8, 24), 3, 3);
        }
        var head = X(_position); var playhead = PlayheadBrush ?? Brushes.Red;
        context.DrawLine(new Pen(playhead, 1.6), new Point(head, RulerHeight + 4), new Point(head, track.Bottom + 4));
        context.DrawEllipse(playhead, null, new Point(head, RulerHeight + 6), 4.5, 4.5);
        if (_dragIndex >= 0) DrawCallout(context, X(_dragValue), _dragValue, _dragStart);
        else if (_hoverIndex >= 0 && _hoverIndex < _segments.Count)
        {
            var value = _hoverStart ? _segments[_hoverIndex].Start : _segments[_hoverIndex].End;
            DrawCallout(context, X(value), value, _hoverStart);
        }
    }

    private void DrawCallout(DrawingContext context, double x, double seconds, bool start)
    {
        var frame = NearestFrame(seconds); const double width = 132, imageHeight = 70;
        var box = new Rect(Math.Clamp(x - width / 2, 0, Math.Max(0, Bounds.Width - width)), -CalloutHeight - 6, width, CalloutHeight);
        context.DrawRectangle(CalloutBrush, null, box, 8, 8);
        var imageRect = new Rect(box.X + 4, box.Y + 4, width - 8, imageHeight);
        if (frame is not null)
            using (context.PushClip(new RoundedRect(imageRect, 5)))
            {
                var scale = Math.Max(imageRect.Width / frame.Size.Width, imageRect.Height / frame.Size.Height);
                var source = new Size(imageRect.Width / scale, imageRect.Height / scale);
                context.DrawImage(frame, new Rect((frame.Size.Width - source.Width) / 2, (frame.Size.Height - source.Height) / 2, source.Width, source.Height), imageRect);
            }
        var label = Format(Translate(start ? "起点" : "终点") + " " + Long(seconds), 10.5, Brushes.White, true);
        context.DrawText(label, new Point(box.X + (width - label.Width) / 2, imageRect.Bottom + 1));
    }

    private (int Index, bool Start)? HandleAt(Point point)
    {
        if (_selected < 0 || _selected >= _segments.Count || point.Y < RulerHeight) return null;
        var segment = _segments[_selected];
        if (Math.Abs(point.X - X(segment.Start)) <= 8) return (_selected, true);
        if (Math.Abs(point.X - X(segment.End)) <= 8) return (_selected, false);
        return null;
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
        _seeking = true; e.Pointer.Capture(this); NavigateRequested?.Invoke(hit, time); e.Handled = true;
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
        else if (_seeking) NavigateRequested?.Invoke(-1, time);
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
        _seeking = false; e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { _dragIndex = -1; _seeking = false; InvalidateVisual(); base.OnPointerCaptureLost(e); }

    private static FormattedText Format(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);
    public static string Short(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}"; }
    public static string Long(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return (time.TotalHours >= 1 ? $"{(int)time.TotalHours}:" : "") + $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"; }
}
