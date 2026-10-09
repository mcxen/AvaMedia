using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

public sealed record SegmentTimelineItem(int Number, double Start, double End, double Speed);

/// <summary>A compact view of the export order. Seeking maps back to each segment's source time.</summary>
public sealed class SegmentTimeline : Control
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<SegmentTimeline, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = AvaloniaProperty.Register<SegmentTimeline, IBrush?>(nameof(BorderBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<SegmentTimeline, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<SegmentTimeline, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> SelectedTextBrushProperty = AvaloniaProperty.Register<SegmentTimeline, IBrush?>(nameof(SelectedTextBrush));
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? SelectedTextBrush { get => GetValue(SelectedTextBrushProperty); set => SetValue(SelectedTextBrushProperty, value); }
    private IReadOnlyList<SegmentTimelineItem> _items = [];
    private int _active = -1;
    private double _position;
    public event Action<int, double>? NavigateRequested;
    static SegmentTimeline() => AffectsRender<SegmentTimeline>(TrackBrushProperty, BorderBrushProperty, SelectionBrushProperty, TextBrushProperty, SelectedTextBrushProperty);

    public void SetItems(IReadOnlyList<SegmentTimelineItem> items, int active)
    {
        _items = items; _active = active;
        MinWidth = items.Count * 72;
        InvalidateMeasure(); InvalidateVisual();
    }
    public void SetPosition(double seconds) { _position = seconds; InvalidateVisual(); }

    private IEnumerable<(int Index, Rect Rect)> Layout()
    {
        if (_items.Count == 0) yield break;
        var free = Math.Max(0, Bounds.Width - _items.Count * 72);
        var durations = _items.Select(i => Math.Max(.001, (i.End - i.Start) / Math.Max(.25, i.Speed))).ToArray();
        var total = durations.Sum(); var x = 0d;
        for (var i = 0; i < _items.Count; i++)
        {
            var width = 72 + free * durations[i] / total;
            yield return (i, new Rect(x + 1, 1, width - 2, Math.Max(0, Bounds.Height - 2)));
            x += width;
        }
    }
    public override void Render(DrawingContext context)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        foreach (var (index, rect) in Layout())
        {
            var item = _items[index]; var selected = index == _active;
            var foreground = (selected ? SelectedTextBrush : TextBrush) ?? Brushes.Black;
            context.DrawRectangle(selected ? SelectionBrush : TrackBrush, new Pen(BorderBrush, 1), rect, 3, 3);
            using (context.PushClip(rect))
            {
                var title = new FormattedText(Localization.Format($"片段 {item.Number}"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, foreground);
                var duration = new FormattedText(Core.MediaTime.Format((item.End - item.Start) / Math.Max(.25, item.Speed)), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, foreground);
                context.DrawText(title, new Point(rect.X + 7, rect.Y + 4));
                context.DrawText(duration, new Point(rect.X + 7, rect.Y + 23));
                if (selected)
                {
                    var fraction = Math.Clamp((_position - item.Start) / Math.Max(.001, item.End - item.Start), 0, 1);
                    var x = rect.X + 2 + fraction * Math.Max(0, rect.Width - 4);
                    context.DrawLine(new Pen(foreground, 2), new Point(x, rect.Y + 1), new Point(x, rect.Bottom - 1));
                }
            }
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        foreach (var (index, rect) in Layout())
        {
            if (!rect.Contains(point)) continue;
            var item = _items[index];
            var fraction = Math.Clamp((point.X - rect.X) / Math.Max(1, rect.Width), 0, 1);
            NavigateRequested?.Invoke(index, item.Start + fraction * (item.End - item.Start));
            e.Handled = true; return;
        }
    }
}
