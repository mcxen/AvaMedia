using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

/// <summary>条形进度以外的独立等待/按钮内活动符号。</summary>
public sealed class LoadingSpinner : Control
{
    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<LoadingSpinner, double>(nameof(Angle));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<LoadingSpinner, IBrush?>(nameof(Track));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<LoadingSpinner, IBrush?>(nameof(Accent));
    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public IBrush? Track => GetValue(TrackProperty);
    public IBrush? Accent => GetValue(AccentProperty);
    static LoadingSpinner() => AffectsRender<LoadingSpinner>(AngleProperty, TrackProperty, AccentProperty);
    public LoadingSpinner()
    {
        Classes.Add("ac-spinner"); Bind(TrackProperty, Resource("AcRunningFill")); Bind(AccentProperty, Resource("UiAccent"));
    }
    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height); var r = size / 2 - 1;
        context.DrawEllipse(null, new Pen(Track, 2), new(size / 2, size / 2), r, r);
        var path = new StreamGeometry();
        Point P(double a) => new(size / 2 + r * Math.Cos(a), size / 2 + r * Math.Sin(a));
        var angle = Angle * Math.PI / 180;
        using (var g = path.Open()) { g.BeginFigure(P(angle), false); g.ArcTo(P(angle + Math.PI / 2), new(r, r), 0, false, SweepDirection.Clockwise); g.EndFigure(false); }
        context.DrawGeometry(null, new Pen(Accent, 2, lineCap: PenLineCap.Round), path);
    }
}

public sealed class ProgressRing : Grid
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));
    private readonly RingDrawing _drawing = new();
    private readonly TextBlock _text = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public ProgressRing()
    {
        Classes.Add("ac-ring"); Children.Add(_drawing); Children.Add(_text);
        BindResource(_text, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(_text, TextBlock.ForegroundProperty, "UiText");
        _text.FontFeatures = new() { FontFeature.Parse("tnum") };
        PropertyChanged += (_, e) => { if (e.Property == ValueProperty) { _drawing.Value = Value; _text.Text = $"{Value:0}%"; } };
    }
    private sealed class RingDrawing : Control
    {
        public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<RingDrawing, double>(nameof(Value));
        public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<RingDrawing, IBrush?>("Track");
        public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<RingDrawing, IBrush?>("Accent");
        public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        static RingDrawing() => AffectsRender<RingDrawing>(ValueProperty, TrackProperty, AccentProperty);
        public RingDrawing() { Bind(TrackProperty, Resource("UiTrack")); Bind(AccentProperty, Resource("UiAccent")); }
        public override void Render(DrawingContext context)
        {
            var size = Math.Min(Bounds.Width, Bounds.Height); var center = new Point(size / 2, size / 2); var r = size / 2 - 2;
            context.DrawEllipse(null, new Pen(GetValue(TrackProperty), 3.5), center, r, r);
            var value = Math.Clamp(Value, 0, 100); if (value == 0) return;
            var pen = new Pen(GetValue(AccentProperty), 3.5, lineCap: PenLineCap.Round);
            if (value == 100) { context.DrawEllipse(null, pen, center, r, r); return; }
            var a = value / 100 * Math.PI * 2 - Math.PI / 2;
            var path = new StreamGeometry();
            using (var g = path.Open()) { g.BeginFigure(new(center.X, center.Y - r), false); g.ArcTo(new(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a)), new(r, r), 0, value > 50, SweepDirection.Clockwise); g.EndFigure(false); }
            context.DrawGeometry(null, pen, path);
        }
    }
}

/// <summary>两个可拖动、可用方向键调整的范围端点；值按 Minimum/Maximum 约束。</summary>
public sealed class RangeSlider : Grid
{
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<RangeSlider, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<RangeSlider, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> LowerValueProperty = AvaloniaProperty.Register<RangeSlider, double>(nameof(LowerValue), 20);
    public static readonly StyledProperty<double> UpperValueProperty = AvaloniaProperty.Register<RangeSlider, double>(nameof(UpperValue), 70);
    private readonly Canvas _canvas = new();
    private readonly Border _fill = new();
    private readonly Thumb _lower = new() { Focusable = true }, _upper = new() { Focusable = true };
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LowerValue { get => GetValue(LowerValueProperty); set => SetValue(LowerValueProperty, value); }
    public double UpperValue { get => GetValue(UpperValueProperty); set => SetValue(UpperValueProperty, value); }
    public RangeSlider()
    {
        Classes.Add("ac-range"); Bind(MinHeightProperty, Resource("AcSliderHeight"));
        var track = new Border { VerticalAlignment = VerticalAlignment.Center };
        BindResource(track, HeightProperty, "AcSliderTrack"); BindResource(track, Border.BackgroundProperty, "UiTrack"); BindResource(track, Border.CornerRadiusProperty, "AcProgressRadius"); Children.Add(track);
        BindResource(_fill, HeightProperty, "AcSliderTrack"); BindResource(_fill, Border.BackgroundProperty, "UiAccent"); BindResource(_fill, Border.CornerRadiusProperty, "AcProgressRadius");
        _canvas.Children.Add(_fill); _canvas.Children.Add(_lower); _canvas.Children.Add(_upper); Children.Add(_canvas);
        foreach (var thumb in new[] { _lower, _upper })
        {
            thumb.Classes.Add("ac-range-thumb");
            Avalonia.Automation.AutomationProperties.SetName(thumb, thumb == _lower ? "范围起点" : "范围终点");
            thumb.DragDelta += (_, e) => SetEndpoint(thumb, Endpoint(thumb) + e.Vector.X / Math.Max(1, Bounds.Width - 18) * Math.Max(0, Maximum - Minimum));
            thumb.KeyDown += (_, e) =>
            {
                var step = Math.Max(0, Maximum - Minimum) / 100;
                if (e.Key is Key.Left or Key.Down or Key.Right or Key.Up or Key.Home or Key.End)
                {
                    SetEndpoint(thumb, e.Key == Key.Home ? Minimum : e.Key == Key.End ? Maximum : Endpoint(thumb) + (e.Key is Key.Left or Key.Down ? -step : step)); e.Handled = true;
                }
            };
        }
        PropertyChanged += (_, e) => { if (e.Property == LowerValueProperty || e.Property == UpperValueProperty || e.Property == MinimumProperty || e.Property == MaximumProperty) Position(); };
        SizeChanged += (_, _) => Position();
    }
    private double Endpoint(Thumb thumb) => thumb == _lower ? LowerValue : UpperValue;
    private void SetEndpoint(Thumb thumb, double value)
    {
        var max = Math.Max(Minimum, Maximum);
        if (thumb == _lower) SetCurrentValue(LowerValueProperty, Math.Clamp(value, Minimum, Math.Clamp(UpperValue, Minimum, max)));
        else SetCurrentValue(UpperValueProperty, Math.Clamp(value, Math.Clamp(LowerValue, Minimum, max), max));
    }
    private void Position()
    {
        var range = Math.Max(.00001, Maximum - Minimum); var width = Math.Max(0, Bounds.Width - 18);
        var left = Math.Clamp((LowerValue - Minimum) / range, 0, 1) * width;
        var right = Math.Clamp((UpperValue - Minimum) / range, 0, 1) * width;
        Canvas.SetLeft(_lower, left); Canvas.SetLeft(_upper, right); Canvas.SetTop(_lower, 2); Canvas.SetTop(_upper, 2);
        Canvas.SetLeft(_fill, left + 9); Canvas.SetTop(_fill, 9.5); _fill.Width = Math.Max(0, right - left);
    }
}
