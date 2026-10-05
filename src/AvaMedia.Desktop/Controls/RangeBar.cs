using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;
public sealed class RangeBar : Control
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<RangeBar, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = AvaloniaProperty.Register<RangeBar, IBrush?>(nameof(BorderBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<RangeBar, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> HandleBrushProperty = AvaloniaProperty.Register<RangeBar, IBrush?>(nameof(HandleBrush));
    public static readonly StyledProperty<IBrush?> GripBrushProperty = AvaloniaProperty.Register<RangeBar, IBrush?>(nameof(GripBrush));
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty,value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty,value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty,value); }
    public IBrush? HandleBrush { get => GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty,value); }
    public IBrush? GripBrush { get => GetValue(GripBrushProperty); set => SetValue(GripBrushProperty,value); }
    static RangeBar() => AffectsRender<RangeBar>(TrackBrushProperty,BorderBrushProperty,SelectionBrushProperty,HandleBrushProperty,GripBrushProperty);
    public double Duration{get;set;}=1;
    public double Start{get;set;}
    public double End{get;set;}=1;
    public double Step{get;set;}
    public event Action<double,double>? Changed;
    private int _drag;
    public override void Render(DrawingContext c)
    {
        double w=Math.Max(1,Bounds.Width-28),l=14+w*Start/Math.Max(double.Epsilon,Duration),r=14+w*End/Math.Max(double.Epsilon,Duration);
        c.DrawRectangle(TrackBrush,new Pen(BorderBrush,1),new Rect(0,0,Bounds.Width,Bounds.Height));c.DrawRectangle(SelectionBrush,null,new Rect(l,0,Math.Max(0,r-l),Bounds.Height));
        foreach(var x in new[]{l,r}){c.DrawRectangle(HandleBrush,new Pen(BorderBrush,1),new Rect(x-7,0,14,Bounds.Height));c.DrawLine(new Pen(GripBrush,2),new(x,7),new(x,Bounds.Height-7));}
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e){if(Duration<=0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;var x=e.GetPosition(this).X;var left=14+(Bounds.Width-28)*Start/Duration;var right=14+(Bounds.Width-28)*End/Duration;_drag=Math.Abs(x-left)<=Math.Abs(x-right)?1:2;e.Pointer.Capture(this);Update(x);}
    protected override void OnPointerMoved(PointerEventArgs e){if(_drag!=0)Update(e.GetPosition(this).X);}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){_drag=0;e.Pointer.Capture(null);}
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){_drag=0;base.OnPointerCaptureLost(e);}
    private void Update(double x){var t=Math.Clamp((x-14)/Math.Max(1,Bounds.Width-28)*Duration,0,Duration);if(Step>0)t=Math.Clamp(Math.Round(t/Step)*Step,0,Duration);var minimum=Math.Min(.01,Duration);if(_drag==1)Start=Math.Clamp(t,0,Math.Max(0,End-minimum));else End=Math.Clamp(t,Math.Min(Duration,Start+minimum),Duration);InvalidateVisual();Changed?.Invoke(Start,End);}
}
