using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;
public sealed class CropOverlay : Control
{
    public static readonly StyledProperty<IBrush?> ShadeBrushProperty = AvaloniaProperty.Register<CropOverlay, IBrush?>(nameof(ShadeBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<CropOverlay, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> BorderBrushProperty = AvaloniaProperty.Register<CropOverlay, IBrush?>(nameof(BorderBrush));
    public static readonly StyledProperty<IBrush?> HandleBrushProperty = AvaloniaProperty.Register<CropOverlay, IBrush?>(nameof(HandleBrush));
    public IBrush? ShadeBrush { get => GetValue(ShadeBrushProperty); set => SetValue(ShadeBrushProperty,value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty,value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty,value); }
    public IBrush? HandleBrush { get => GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty,value); }
    static CropOverlay() => AffectsRender<CropOverlay>(ShadeBrushProperty,SelectionBrushProperty,BorderBrushProperty,HandleBrushProperty);
    public int SourceWidth{get;set;}=1920;
    public int SourceHeight{get;set;}=1080;
    public Rect Selection{get;set;}
    public bool Enabled{get;set;}
    public int PixelStep{get;set;}=2;
    public double AspectRatio{get;set;}
    public event Action<Rect>? Changed;
    private Point? _origin;
    private Rect _before;
    private int _gesture;
    public CropOverlay(){Focusable=true;}
    private Rect ImageRect
    {get{double scale=Math.Min(Bounds.Width/Math.Max(1,SourceWidth),Bounds.Height/Math.Max(1,SourceHeight));return new((Bounds.Width-SourceWidth*scale)/2,(Bounds.Height-SourceHeight*scale)/2,SourceWidth*scale,SourceHeight*scale);}}
    public override void Render(DrawingContext c)
    {
        if(!Enabled)return;var image=ImageRect;var rect=ScreenRect(Selection);
        if(Selection.Width>0 && Selection.Height>0)
        {
            var shade=ShadeBrush;
            c.DrawRectangle(shade,null,new Rect(image.X,image.Y,image.Width,Math.Max(0,rect.Y-image.Y)));
            c.DrawRectangle(shade,null,new Rect(image.X,rect.Bottom,image.Width,Math.Max(0,image.Bottom-rect.Bottom)));
            c.DrawRectangle(shade,null,new Rect(image.X,rect.Y,Math.Max(0,rect.X-image.X),rect.Height));
            c.DrawRectangle(shade,null,new Rect(rect.Right,rect.Y,Math.Max(0,image.Right-rect.Right),rect.Height));
        }
        c.DrawRectangle(SelectionBrush,new Pen(BorderBrush,2),rect);
        foreach(var p in Corners(rect))c.DrawRectangle(HandleBrush,new Pen(BorderBrush,1),new Rect(p.X-4,p.Y-4,8,8));
    }
    private Rect ScreenRect(Rect source)
    {var image=ImageRect;var scale=image.Width/Math.Max(1,SourceWidth);return new(image.X+source.X*scale,image.Y+source.Y*scale,source.Width*scale,source.Height*scale);}
    private static Point[] Corners(Rect rect)=>[rect.TopLeft,rect.TopRight,rect.BottomLeft,rect.BottomRight];
    private int Hit(Point p)
    {
        var rect=ScreenRect(Selection);var corners=Corners(rect);
        for(var i=0;i<corners.Length;i++)if(Math.Abs(p.X-corners[i].X)<=8 && Math.Abs(p.Y-corners[i].Y)<=8)return i+2;
        if(Selection.Width>=SourceWidth-PixelStep && Selection.Height>=SourceHeight-PixelStep)return 0;
        return rect.Contains(p)?1:0;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if(!Enabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !ImageRect.Contains(e.GetPosition(this)) || ImageRect.Width<=0)return;
        Focus();_before=Selection;_gesture=Hit(e.GetPosition(this));_origin=SourcePoint(e.GetPosition(this));e.Pointer.Capture(this);e.Handled=true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if(_origin is {} start){Update(start,SourcePoint(e.GetPosition(this)));e.Handled=true;return;}
        if(!Enabled)return;
        Cursor=new Cursor(Hit(e.GetPosition(this)) switch{1=>StandardCursorType.SizeAll,2 or 5=>StandardCursorType.TopLeftCorner,3 or 4=>StandardCursorType.TopRightCorner,_=>StandardCursorType.Cross});
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if(_origin is {} start)Update(start,SourcePoint(e.GetPosition(this)));
        _origin=null;e.Pointer.Capture(null);base.OnPointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){_origin=null;base.OnPointerCaptureLost(e);}
    private void Update(Point start,Point end)
    {
        var step=Math.Max(1,PixelStep);var maxWidth=Snap(SourceWidth);var maxHeight=Snap(SourceHeight);
        if(_gesture==1)
        {
            Selection=new Rect(Math.Clamp(Snap(_before.X+end.X-start.X),0,Math.Max(0,maxWidth-_before.Width)),Math.Clamp(Snap(_before.Y+end.Y-start.Y),0,Math.Max(0,maxHeight-_before.Height)),_before.Width,_before.Height);
        }
        else
        {
            var anchor=_gesture switch{2=>_before.BottomRight,3=>_before.BottomLeft,4=>_before.TopRight,5=>_before.TopLeft,_=>start};
            var dx=end.X-anchor.X;var dy=end.Y-anchor.Y;
            if(AspectRatio>0)
            {
                var width=Math.Min(Math.Abs(dx),Math.Abs(dy)*AspectRatio);var height=width/AspectRatio;
                dx=Math.CopySign(width,dx);dy=Math.CopySign(height,dy);
            }
            var other=new Point(Math.Clamp(anchor.X+dx,0,maxWidth),Math.Clamp(anchor.Y+dy,0,maxHeight));
            var rect=new Rect(anchor,other);var left=Snap(rect.X);var top=Snap(rect.Y);var right=Snap(rect.Right);var bottom=Snap(rect.Bottom);
            if(right-left<step || bottom-top<step)return;
            Selection=new(left,top,right-left,bottom-top);
        }
        InvalidateVisual();Changed?.Invoke(Selection);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(!Enabled)return;
        if(e.Key==Key.Escape && _origin is not null){Selection=_before;_origin=null;InvalidateVisual();Changed?.Invoke(Selection);e.Handled=true;return;}
        var step=Math.Max(1,PixelStep)*(e.KeyModifiers.HasFlag(KeyModifiers.Shift)?10:1);
        var dx=e.Key==Key.Left?-step:e.Key==Key.Right?step:0;var dy=e.Key==Key.Up?-step:e.Key==Key.Down?step:0;
        if(dx==0 && dy==0)return;
        Selection=new(Math.Clamp(Selection.X+dx,0,Math.Max(0,Snap(SourceWidth)-Selection.Width)),Math.Clamp(Selection.Y+dy,0,Math.Max(0,Snap(SourceHeight)-Selection.Height)),Selection.Width,Selection.Height);
        InvalidateVisual();Changed?.Invoke(Selection);e.Handled=true;
    }
    private int Snap(double value)=>(int)Math.Floor(value/Math.Max(1,PixelStep)+.0000001)*Math.Max(1,PixelStep);
    private Point SourcePoint(Point p)
    {var image=ImageRect;var scale=image.Width/Math.Max(1,SourceWidth);return new(Snap(Math.Clamp((p.X-image.X)/scale,0,SourceWidth)),Snap(Math.Clamp((p.Y-image.Y)/scale,0,SourceHeight)));}
}
