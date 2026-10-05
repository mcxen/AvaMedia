using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;
public sealed class RangeBar : Control
{
    public double Duration{get;set;}=1;
    public double Start{get;set;}
    public double End{get;set;}=1;
    public event Action<double,double>? Changed;
    private int _drag;
    public override void Render(DrawingContext c)
    {
        double w=Math.Max(1,Bounds.Width-28),l=14+w*Start/Math.Max(.001,Duration),r=14+w*End/Math.Max(.001,Duration);
        c.DrawRectangle(Brush.Parse("#F3F3F3"),new Pen(Brush.Parse("#B0B0B0"),1),new Rect(0,0,Bounds.Width,Bounds.Height));c.DrawRectangle(Brush.Parse("#218DFF"),null,new Rect(l,0,Math.Max(0,r-l),Bounds.Height));
        foreach(var x in new[]{l,r}){c.DrawRectangle(Brushes.White,new Pen(Brush.Parse("#B7B7B7"),1),new Rect(x-7,0,14,Bounds.Height));c.DrawLine(new Pen(Brush.Parse("#139DD9"),2),new(x,7),new(x,Bounds.Height-7));}
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e){var x=e.GetPosition(this).X;var left=14+(Bounds.Width-28)*Start/Duration;var right=14+(Bounds.Width-28)*End/Duration;_drag=Math.Abs(x-left)<=Math.Abs(x-right)?1:2;e.Pointer.Capture(this);Update(x);}
    protected override void OnPointerMoved(PointerEventArgs e){if(_drag!=0)Update(e.GetPosition(this).X);}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){_drag=0;e.Pointer.Capture(null);}
    private void Update(double x){var t=Math.Clamp((x-14)/Math.Max(1,Bounds.Width-28)*Duration,0,Duration);if(_drag==1)Start=Math.Min(t,End-.01);else End=Math.Max(t,Start+.01);InvalidateVisual();Changed?.Invoke(Start,End);}
}
