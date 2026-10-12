using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>Source-time coverage. Partial observations never paint beyond the analyzed boundary.</summary>
internal sealed class PersonTaskTimeline : Control
{
    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<PersonTaskTimeline, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<PersonTaskTimeline, IBrush?>("Track");
    private static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<PersonTaskTimeline, IBrush?>("Ink");
    private static readonly StyledProperty<IBrush?> PendingProperty = AvaloniaProperty.Register<PersonTaskTimeline, IBrush?>("Pending");
    private PersonClipResult? _result;
    private double _duration;
    private int _selected = -1, _hovered = -1;
    private bool _showCursor;
    public event Action<int>? SelectSegment;
    public int Selected { get => _selected; set { if (_selected != value) { _selected = value; InvalidateVisual(); } } }
    public bool ShowCursor { get => _showCursor; set { if (_showCursor != value) { _showCursor = value; InvalidateVisual(); } } }
    static PersonTaskTimeline() => AffectsRender<PersonTaskTimeline>(AccentProperty, TrackProperty, InkProperty, PendingProperty);
    public PersonTaskTimeline()
    {
        Classes.Add("person-timeline");
        Bind(AccentProperty, new DynamicResourceExtension("UiAccent")); Bind(TrackProperty, new DynamicResourceExtension("UiTrack"));
        Bind(InkProperty, new DynamicResourceExtension("UiText")); Bind(PendingProperty, new DynamicResourceExtension("UiFill"));
        ToolTip.SetPlacement(this, PlacementMode.Bottom); ToolTip.SetVerticalOffset(this, 8); ToolTip.SetShowDelay(this, 150);
        Avalonia.Automation.AutomationProperties.SetName(this, Localization.Text("人物出现时间轴"));
        PointerMoved += Hover; PointerExited += (_, _) => { _hovered = -1; ToolTip.SetIsOpen(this, false); InvalidateVisual(); };
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Hit(e.GetPosition(this).X) is var index && index >= 0) SelectSegment?.Invoke(index); };
    }
    public void Update(PersonClipResult? result, double duration)
    {
        _result = result; _duration = Math.Max(0, double.IsFinite(duration) ? duration : 0);
        _hovered = -1; ToolTip.SetIsOpen(this, false); InvalidateVisual();
    }
    private double Observed => _result is null ? 0 : _result.IsPartial ? Math.Clamp(_result.AnalyzedSeconds, 0, _duration) : _duration;
    private int Hit(double x)
    {
        if (_result is null || _duration <= 0 || Bounds.Width <= 0) return -1;
        var seconds = Math.Clamp(x / Bounds.Width, 0, 1) * _duration;
        if (seconds > Observed) return -1;
        for (var i = 0; i < _result.Segments.Count; i++)
            if (seconds >= _result.Segments[i].Start && seconds <= _result.Segments[i].End) return i;
        return -1;
    }
    private void Hover(object? sender, PointerEventArgs e)
    {
        var index = Hit(e.GetPosition(this).X);
        if (_hovered == index) return; _hovered = index; InvalidateVisual();
        ToolTip.SetIsOpen(this, false);
        if (index < 0 || _result is null) return;
        var segment = _result.Segments[index];
        ToolTip.SetTip(this, Localization.Format($"片段 {index + 1}  {PersonTaskView.Time(segment.Start)} — {PersonTaskView.Time(segment.End)} · {segment.End - segment.Start:0.#} 秒"));
        ToolTip.SetIsOpen(this, true);
    }
    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var height = Bounds.Height;
        context.DrawRectangle(GetValue(PendingProperty), null, new Rect(0, 0, width, height), 6, 6);
        if (_duration <= 0 || _result is null) return;
        var boundary = Observed / _duration * width;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, width, height), 6)))
        {
            context.DrawRectangle(GetValue(TrackProperty), null, new Rect(0, 0, boundary, height));
            for (var i = 0; i < _result.Segments.Count; i++)
            {
                var segment = _result.Segments[i];
                var start = Math.Clamp(segment.Start, 0, Observed) / _duration * width;
                var end = Math.Clamp(segment.End, 0, Observed) / _duration * width;
                if (end <= start) continue;
                context.DrawRectangle(GetValue(AccentProperty), null, new Rect(start, 0, Math.Max(1, end - start), height), 2, 2);
                if (i == Selected || i == _hovered)
                    context.DrawRectangle(null, new Pen(GetValue(InkProperty), 1.5), new Rect(start + .75, .75, Math.Max(1, end - start - 1.5), height - 1.5), 2, 2);
            }
        }
        if (ShowCursor) context.DrawLine(new Pen(GetValue(InkProperty), 2), new(boundary, -2), new(boundary, height + 2));
    }
}
