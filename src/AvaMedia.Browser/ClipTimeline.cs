using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.Browser;

/// <summary>
/// Source-time timeline: ruler, filmstrip, segment boxes with in/out handles and a playhead.
/// While a handle is dragged a callout shows the nearest filmstrip frame and the timecode.
/// </summary>
internal sealed class ClipTimeline : Control
{
    public sealed record Segment(double Start, double End);
    private IReadOnlyList<Segment> _segments = [];
    private IReadOnlyList<(double Seconds, Bitmap Image)> _strip = [];
    private double _duration, _position;
    private int _selected = -1, _dragIndex = -1;
    private bool _dragStart, _seeking;
    private double _dragValue;
    private const double RulerHeight = 16, LabelHeight = 16, StripTop = RulerHeight + LabelHeight, StripHeight = 46, CalloutHeight = 78;
    public bool RemoveMode { get; set; }
    public string LabelFormat { get; set; } = "片段 {0}";
    public event Action<double>? Seek;
    public event Action<int>? SegmentSelected;
    /// <summary>(index, isStart, seconds, finished)</summary>
    public event Action<int, bool, double, bool>? HandleMoved;

    public ClipTimeline() { Height = StripTop + StripHeight + 6; ClipToBounds = false; Cursor = new Cursor(StandardCursorType.Hand); }

    public double Duration { get => _duration; set { _duration = value; InvalidateVisual(); } }
    public double Position { get => _position; set { _position = value; InvalidateVisual(); } }
    public void SetSegments(IReadOnlyList<Segment> segments, int selected) { _segments = segments; _selected = selected; InvalidateVisual(); }
    public void SetStrip(IReadOnlyList<(double, Bitmap)> strip) { _strip = strip; InvalidateVisual(); }
    public Bitmap? NearestFrame(double seconds) => _strip.Count == 0 ? null : _strip.MinBy(item => Math.Abs(item.Seconds - seconds)).Image;

    private Rect Track => new(6, StripTop, Math.Max(1, Bounds.Width - 12), StripHeight);
    private double X(double seconds) => Track.X + (_duration <= 0 ? 0 : Math.Clamp(seconds / _duration, 0, 1) * Track.Width);
    private double Time(double x) => _duration <= 0 ? 0 : Math.Clamp((x - Track.X) / Track.Width, 0, 1) * _duration;

    public override void Render(DrawingContext context)
    {
        var track = Track; var secondary = new SolidColorBrush(Color.Parse("#86868B"));
        // Ruler labels: five evenly spaced ticks.
        for (var index = 0; index <= 4; index++)
        {
            var seconds = _duration * index / 4; var text = Format(Short(seconds), 10.5, secondary);
            var x = Math.Clamp(X(seconds) - (index == 0 ? 6 : index == 4 ? text.Width - 6 : text.Width / 2), 0, Math.Max(0, Bounds.Width - text.Width));
            context.DrawText(text, new Point(x, 0));
        }
        using (context.PushClip(new RoundedRect(track, 6)))
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#D9DCE3")), track);
            if (_strip.Count > 0)
            {
                var cell = track.Width / _strip.Count;
                for (var index = 0; index < _strip.Count; index++)
                {
                    var image = _strip[index].Image; var target = new Rect(track.X + cell * index, track.Y, cell + .5, track.Height);
                    var scale = Math.Max(target.Width / image.Size.Width, target.Height / image.Size.Height);
                    var source = new Size(target.Width / scale, target.Height / scale);
                    context.DrawImage(image, new Rect((image.Size.Width - source.Width) / 2, (image.Size.Height - source.Height) / 2, source.Width, source.Height), target);
                    context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1), target.TopLeft, target.BottomLeft);
                }
            }
            // Dim what will not be exported.
            var dim = new SolidColorBrush(Color.FromArgb(RemoveMode ? (byte)0 : (byte)70, 255, 255, 255));
            if (!RemoveMode) context.FillRectangle(dim, track);
        }
        var blue = new SolidColorBrush(Color.Parse("#007AFF"));
        for (var index = 0; index < _segments.Count; index++)
        {
            var segment = _segments[index]; var selected = index == _selected;
            var start = index == _dragIndex && _dragStart ? _dragValue : segment.Start;
            var end = index == _dragIndex && !_dragStart ? _dragValue : segment.End;
            var box = new Rect(X(start), track.Y - 1, Math.Max(2, X(end) - X(start)), track.Height + 2);
            var fill = RemoveMode ? new SolidColorBrush(Color.FromArgb(90, 255, 59, 48)) : new SolidColorBrush(Color.FromArgb(selected ? (byte)40 : (byte)22, 0, 122, 255));
            context.DrawRectangle(fill, new Pen(RemoveMode ? new SolidColorBrush(Color.Parse("#FF3B30")) : blue, selected ? 2.2 : 1.4), box, 5, 5);
            var label = Format(L.F(LabelFormat, index + 1), 10.5, RemoveMode ? new SolidColorBrush(Color.Parse("#FF3B30")) : blue, true);
            context.DrawText(label, new Point(Math.Min(box.X + 4, Bounds.Width - label.Width), RulerHeight + 1));
            if (selected)
                foreach (var x in new[] { box.X, box.Right })
                    context.DrawRectangle(blue, null, new Rect(x - 3.5, track.Y + track.Height / 2 - 10, 7, 20), 3, 3);
        }
        // Playhead
        var head = X(_position); var red = new SolidColorBrush(Color.Parse("#FF3B30"));
        context.DrawLine(new Pen(red, 1.5), new Point(head, RulerHeight + 4), new Point(head, track.Bottom + 4));
        context.DrawEllipse(red, null, new Point(head, RulerHeight + 6), 4.5, 4.5);
        if (_dragIndex >= 0) DrawCallout(context, X(_dragValue), _dragValue);
    }

    private void DrawCallout(DrawingContext context, double x, double seconds)
    {
        var frame = NearestFrame(seconds); var width = 118d; var imageHeight = 62d;
        var box = new Rect(Math.Clamp(x - width / 2, 0, Math.Max(0, Bounds.Width - width)), -CalloutHeight - 8, width, CalloutHeight);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#1C1C1E")), null, box, 8, 8);
        var imageRect = new Rect(box.X + 4, box.Y + 4, width - 8, imageHeight);
        if (frame is not null)
            using (context.PushClip(new RoundedRect(imageRect, 5)))
            {
                var scale = Math.Max(imageRect.Width / frame.Size.Width, imageRect.Height / frame.Size.Height);
                var source = new Size(imageRect.Width / scale, imageRect.Height / scale);
                context.DrawImage(frame, new Rect((frame.Size.Width - source.Width) / 2, (frame.Size.Height - source.Height) / 2, source.Width, source.Height), imageRect);
            }
        var text = Format((_dragStart ? L.T("起点") : L.T("终点")) + " " + Long(seconds), 10.5, Brushes.White, true);
        context.DrawText(text, new Point(box.X + (width - text.Width) / 2, imageRect.Bottom + 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_duration <= 0) return;
        var point = e.GetPosition(this);
        if (_selected >= 0 && _selected < _segments.Count)
        {
            var segment = _segments[_selected];
            foreach (var (isStart, value) in new[] { (true, segment.Start), (false, segment.End) })
                if (Math.Abs(point.X - X(value)) <= 8 && point.Y >= RulerHeight)
                { _dragIndex = _selected; _dragStart = isStart; _dragValue = value; e.Pointer.Capture(this); e.Handled = true; InvalidateVisual(); return; }
        }
        var time = Time(point.X);
        if (point.Y >= StripTop)
        {
            var hit = Enumerable.Range(0, _segments.Count).Where(index => _segments[index].Start <= time && time <= _segments[index].End).Cast<int?>().FirstOrDefault();
            if (hit is { } index && index != _selected) SegmentSelected?.Invoke(index);
        }
        _seeking = true; e.Pointer.Capture(this); Seek?.Invoke(time); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var time = Time(e.GetPosition(this).X);
        if (_dragIndex >= 0)
        {
            var segment = _segments[_dragIndex]; var minimum = Math.Max(.05, _duration / 2000);
            _dragValue = _dragStart ? Math.Clamp(time, 0, segment.End - minimum) : Math.Clamp(time, segment.Start + minimum, _duration);
            HandleMoved?.Invoke(_dragIndex, _dragStart, _dragValue, false); InvalidateVisual();
        }
        else if (_seeking) Seek?.Invoke(time);
    }
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
