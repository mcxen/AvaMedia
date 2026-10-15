using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Source-time coverage with an explicit unscanned region; partial results never paint past the analyzed boundary.</summary>
internal sealed class StageTimeline : Control
{
    private static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Accent");
    private static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Track");
    private static readonly StyledProperty<IBrush?> UnscannedProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Unscanned");
    private static readonly StyledProperty<IBrush?> HairlineProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Hairline");
    private static readonly StyledProperty<IBrush?> InkProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Ink");
    private static readonly StyledProperty<IBrush?> DangerProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Danger");
    private static readonly StyledProperty<IBrush?> MarkProperty = AvaloniaProperty.Register<StageTimeline, IBrush?>("Mark");
    private IReadOnlyList<PersonClipKeptSegment> _segments = [];
    private double _duration, _observed;
    private bool _partial, _failed;
    private int _selected = -1, _hovered = -1;
    public event Action<int>? SelectSegment;

    static StageTimeline() => AffectsRender<StageTimeline>(AccentProperty, TrackProperty, UnscannedProperty, HairlineProperty, InkProperty, DangerProperty, MarkProperty);

    public StageTimeline()
    {
        Height = 26; Cursor = new Cursor(StandardCursorType.Hand);
        Bind(AccentProperty, new DynamicResourceExtension("UiAccent")); Bind(TrackProperty, new DynamicResourceExtension("UiTrack"));
        Bind(UnscannedProperty, new DynamicResourceExtension("AcUnscannedFill")); Bind(HairlineProperty, new DynamicResourceExtension("AcHairline"));
        Bind(InkProperty, new DynamicResourceExtension("UiText")); Bind(DangerProperty, new DynamicResourceExtension("UiDanger"));
        Bind(MarkProperty, new DynamicResourceExtension("UiAccentText"));
        Avalonia.Automation.AutomationProperties.SetName(this, Localization.Text("人物出现时间轴"));
        ToolTip.SetPlacement(this, PlacementMode.Bottom); ToolTip.SetVerticalOffset(this, 8); ToolTip.SetShowDelay(this, 150);
        PointerMoved += (_, e) => Hover(Hit(e.GetPosition(this).X));
        PointerExited += (_, _) => Hover(-1);
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Hit(e.GetPosition(this).X) is var index and >= 0) SelectSegment?.Invoke(index); };
    }

    public int Selected { get => _selected; set { if (_selected != value) { _selected = value; InvalidateVisual(); } } }

    public void Update(IReadOnlyList<PersonClipKeptSegment> segments, double duration, double observed, bool partial, bool failed)
    {
        _segments = segments; _duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        _observed = partial ? Math.Clamp(double.IsFinite(observed) ? observed : 0, 0, _duration) : _duration;
        _partial = partial; _failed = failed; InvalidateVisual();
    }

    private int Hit(double x)
    {
        if (_duration <= 0 || Bounds.Width <= 0) return -1;
        var seconds = Math.Clamp(x / Bounds.Width, 0, 1) * _duration;
        if (seconds > _observed) return -1;
        for (var i = 0; i < _segments.Count; i++)
            if (seconds >= _segments[i].Start && seconds <= Math.Min(_segments[i].End, _observed)) return i;
        return -1;
    }

    private void Hover(int index)
    {
        if (_hovered == index) return;
        _hovered = index; InvalidateVisual(); ToolTip.SetIsOpen(this, false);
        if (index < 0) return;
        var segment = _segments[index];
        ToolTip.SetTip(this, Localization.Format($"片段 {segment.Number}  {PersonTaskView.Time(segment.Start)} — {PersonTaskView.Time(segment.End)} · {segment.Duration:0.#} 秒"));
        ToolTip.SetIsOpen(this, true);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var height = Bounds.Height;
        var shape = new RoundedRect(new Rect(0, 0, width, height), 6);
        context.DrawRectangle(GetValue(TrackProperty), null, shape);
        if (_duration <= 0) return;
        var boundary = _observed / _duration * width;
        using (context.PushClip(shape))
        {
            if (_partial && boundary < width)
            {
                context.DrawRectangle(GetValue(UnscannedProperty), null, new Rect(boundary, 0, width - boundary, height));
                context.DrawLine(new Pen(GetValue(HairlineProperty), .5), new(boundary + .25, 0), new(boundary + .25, height));
            }
            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                var start = Math.Clamp(segment.Start, 0, _observed) / _duration * width;
                var end = Math.Clamp(segment.End, 0, _observed) / _duration * width;
                if (end <= start) continue;
                var rect = new Rect(start, 0, Math.Max(width * .003, end - start), height);
                context.DrawRectangle(GetValue(AccentProperty), null, rect, 2, 2);
                if (i == _selected || i == _hovered)
                    context.DrawRectangle(null, new Pen(GetValue(MarkProperty), 1.5), rect.Deflate(.75), 2, 2);
            }
        }
        if (_partial && boundary < width)
            context.DrawLine(new Pen(GetValue(_failed ? DangerProperty : InkProperty), _failed ? 2.5 : 2), new(boundary, 0), new(boundary, height));
    }
}

/// <summary>Normalized detection boxes with white label capsules (UISPEC 7.9.3).</summary>
internal sealed class DetectionOverlay : Canvas
{
    private IReadOnlyList<PersonClipDetectionBox> _boxes = [];
    public DetectionOverlay()
    {
        IsHitTestVisible = false; ClipToBounds = false;
        SizeChanged += (_, _) => Rebuild();
    }
    public void Update(IReadOnlyList<PersonClipDetectionBox> boxes) { _boxes = boxes; Rebuild(); }
    private void Rebuild()
    {
        Children.Clear();
        var width = Bounds.Width; var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;
        foreach (var box in _boxes)
        {
            var frame = new Border { Width = box.Width * width, Height = box.Height * height, BorderThickness = new(2), CornerRadius = new(6) };
            frame.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiMediaText"));
            frame.Bind(Border.BoxShadowProperty, new DynamicResourceExtension("AcChipShadow"));
            SetLeft(frame, box.X * width); SetTop(frame, box.Y * height); Children.Add(frame);
            var label = new TextBlock { FontSize = 11, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
            label.Inlines = [new Avalonia.Controls.Documents.Run(Localization.Text(box.Label) + " ")];
            var score = new Avalonia.Controls.Documents.Run(box.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)) { FontWeight = FontWeight.SemiBold };
            score.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, new DynamicResourceExtension("UiAccent"));
            label.Inlines.Add(score);
            label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AcMediaTagInk"));
            var tag = new Border { Height = 20, Padding = new(8, 0), CornerRadius = new(10), Child = label };
            tag.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiMediaText"));
            tag.Bind(Border.BoxShadowProperty, new DynamicResourceExtension("AcChipShadow"));
            SetLeft(tag, box.X * width - 2); SetTop(tag, Math.Max(0, box.Y * height - 25)); Children.Add(tag);
        }
    }
}

/// <summary>Bottom corner chip on a preview: dark translucent capsule with media text.</summary>
internal sealed class PreviewChip : Border
{
    private readonly TextBlock _text = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    public PreviewChip(HorizontalAlignment alignment)
    {
        Height = 20; Padding = new(8, 0); CornerRadius = new(10); Margin = new(8);
        HorizontalAlignment = alignment; VerticalAlignment = VerticalAlignment.Bottom; IsHitTestVisible = false;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiMediaOverlay"));
        _text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiMediaText"));
        _text.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        Child = _text;
    }
    public string Text { get => _text.Text ?? ""; set { _text.Text = value; IsVisible = value.Length > 0; } }
}
