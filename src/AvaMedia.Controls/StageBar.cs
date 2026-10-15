using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

/// <summary>One segment of a <see cref="StageBar"/>. State: pending, active, paused, done, failed, skipped, cancelled.
/// Fraction (0..1) fills the active / paused segment; null shows the active segment as a soft indeterminate fill.</summary>
public sealed record StageBarSegment(string State, double? Fraction = null);

/// <summary>Compact segmented stage bar for list rows: equal segments, AcProgressHeight tall, AcStageBarGap apart.
/// Done uses the success colour, the active stage fills with the accent, paused with the warning colour, failed with the
/// danger colour, skipped is an outlined track and pending / cancelled stay on the track.</summary>
public sealed class StageBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<StageBarSegment>> SegmentsProperty =
        AvaloniaProperty.Register<StageBar, IReadOnlyList<StageBarSegment>>(nameof(Segments), []);
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<StageBar, double>(nameof(Gap), 2);
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(Track));
    public static readonly StyledProperty<IBrush?> DoneProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(Done));
    public static readonly StyledProperty<IBrush?> ActiveProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(Active));
    public static readonly StyledProperty<IBrush?> ActiveSoftProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(ActiveSoft));
    public static readonly StyledProperty<IBrush?> PausedProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(Paused));
    public static readonly StyledProperty<IBrush?> FailedProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(Failed));
    public static readonly StyledProperty<IBrush?> CancelledProperty = AvaloniaProperty.Register<StageBar, IBrush?>(nameof(Cancelled));

    public IReadOnlyList<StageBarSegment> Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? Done { get => GetValue(DoneProperty); set => SetValue(DoneProperty, value); }
    public IBrush? Active { get => GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    public IBrush? ActiveSoft { get => GetValue(ActiveSoftProperty); set => SetValue(ActiveSoftProperty, value); }
    public IBrush? Paused { get => GetValue(PausedProperty); set => SetValue(PausedProperty, value); }
    public IBrush? Failed { get => GetValue(FailedProperty); set => SetValue(FailedProperty, value); }
    public IBrush? Cancelled { get => GetValue(CancelledProperty); set => SetValue(CancelledProperty, value); }

    static StageBar() => AffectsRender<StageBar>(SegmentsProperty, GapProperty, TrackProperty, DoneProperty, ActiveProperty,
        ActiveSoftProperty, PausedProperty, FailedProperty, CancelledProperty);

    public StageBar()
    {
        Classes.Add("ac-stage-bar");
        BindResource(this, HeightProperty, ProgressHeight); BindResource(this, GapProperty, "AcStageBarGap");
        BindResource(this, TrackProperty, "UiTrack"); BindResource(this, DoneProperty, "UiSuccessFill");
        BindResource(this, ActiveProperty, "UiAccent"); BindResource(this, ActiveSoftProperty, "AcRunningFill");
        BindResource(this, PausedProperty, "UiWarning"); BindResource(this, FailedProperty, "UiDanger");
        BindResource(this, CancelledProperty, "AcTextTertiary");
    }

    /// <summary>Replaces the segments only when they changed, so frequent progress publishes do not re-render.</summary>
    public void Update(IReadOnlyList<StageBarSegment> segments)
    {
        var current = Segments;
        if (current.Count == segments.Count && current.SequenceEqual(segments)) return;
        Segments = segments.ToArray();
    }

    public override void Render(DrawingContext context)
    {
        var segments = Segments;
        if (segments.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var gap = Math.Max(0, Gap); var height = Bounds.Height;
        var width = (Bounds.Width - gap * (segments.Count - 1)) / segments.Count;
        if (width <= 0) return;
        var radius = height / 2;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var rect = new Rect(index * (width + gap), 0, width, height);
            switch (segment.State)
            {
                case "done": Fill(Done, rect); break;
                case "failed": Fill(Failed, rect); break;
                case "cancelled": Fill(Cancelled, rect); break;
                case "skipped":
                    if (Track is { } outline) context.DrawRectangle(null, new Pen(outline, 1), rect.Deflate(.5), radius, radius);
                    break;
                case "active" or "paused":
                    Fill(Track, rect);
                    var ink = segment.State == "paused" ? Paused : Active;
                    if (segment.Fraction is { } fraction && double.IsFinite(fraction))
                    {
                        var filled = rect.Width * Math.Clamp(fraction, 0, 1);
                        if (filled > 0) Fill(ink, rect.WithWidth(Math.Max(filled, Math.Min(rect.Width, height))));
                    }
                    else if (segment.State == "active") Fill(ActiveSoft, rect);
                    break;
                default: Fill(Track, rect); break;
            }
        }
        void Fill(IBrush? brush, Rect rect) { if (brush is not null) context.DrawRectangle(brush, null, rect, radius, radius); }
    }
}
