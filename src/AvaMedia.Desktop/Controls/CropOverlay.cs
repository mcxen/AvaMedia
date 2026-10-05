using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;
public sealed class CropOverlay : Control
{
    public int SourceWidth{get;set;}=1920;
    public int SourceHeight{get;set;}=1080;
    public Rect Selection{get;set;}
    public bool Enabled{get;set;}
    public event Action<Rect>? Changed;
    private Point? _origin;
    private Rect ImageRect
    {get{double scale=Math.Min(Bounds.Width/Math.Max(1,SourceWidth),Bounds.Height/Math.Max(1,SourceHeight));return new((Bounds.Width-SourceWidth*scale)/2,(Bounds.Height-SourceHeight*scale)/2,SourceWidth*scale,SourceHeight*scale);}}
    public override void Render(DrawingContext c)
    {
        if(!Enabled)return;var image=ImageRect;double scale=image.Width/SourceWidth;
        var rect=new Rect(image.X+Selection.X*scale,image.Y+Selection.Y*scale,Selection.Width*scale,Selection.Height*scale);
        c.DrawRectangle(Brush.Parse("#1522A4EE"),new Pen(Brush.Parse("#37BEFF"),2),rect);
        foreach(var p in new[]{rect.TopLeft,rect.TopRight,rect.BottomLeft,rect.BottomRight})c.DrawRectangle(Brushes.White,null,new Rect(p.X-3,p.Y-3,6,6));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e){if(!Enabled)return;_origin=SourcePoint(e.GetPosition(this));e.Pointer.Capture(this);}
    protected override void OnPointerMoved(PointerEventArgs e){if(_origin is not {} start)return;var end=SourcePoint(e.GetPosition(this));Selection=new Rect(start,end);InvalidateVisual();Changed?.Invoke(Selection);}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){_origin=null;e.Pointer.Capture(null);}
    private Point SourcePoint(Point p){var image=ImageRect;var scale=image.Width/SourceWidth;return new(Math.Clamp((p.X-image.X)/scale,0,SourceWidth),Math.Clamp((p.Y-image.Y)/scale,0,SourceHeight));}
}
