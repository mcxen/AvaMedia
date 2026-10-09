using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

public sealed class SplitTimeline : Control
{
    public double Start{get;set;}
    public double End{get;set;}
    public List<double> Cuts{get;}=[];
    public event Action? Changed;
    public double? SelectedTime => _selected>=0&&_selected<Cuts.Count?Cuts[_selected]:null;
    private int _selected=-1;
    private bool _dragging;
    public static readonly StyledProperty<IBrush?> AccentProperty=AvaloniaProperty.Register<SplitTimeline,IBrush?>(nameof(Accent));
    public IBrush? Accent{get=>GetValue(AccentProperty);set=>SetValue(AccentProperty,value);}
    public SplitTimeline(){Height=68;Focusable=true;Bind(AccentProperty,new DynamicResourceExtension("UiAccent"));}
    public void SetCuts(IEnumerable<double> cuts){Cuts.Clear();Cuts.AddRange(cuts);_selected=-1;InvalidateVisual();}
    public override void Render(DrawingContext context)
    {
        var marks=new[]{Start}.Concat(Cuts.Order()).Append(End).ToArray();
        for(var index=0;index<marks.Length-1;index++)
        {
            var left=X(marks[index]);var right=X(marks[index+1]);
            context.DrawRectangle(index%2==0?Accent:Brushes.Gray,null,new Rect(left,20,Math.Max(0,right-left-2),28),3,3);
        }
        for(var index=0;index<Cuts.Count;index++){var x=X(Cuts[index]);context.DrawLine(new Pen(_selected==index?Brushes.Orange:Accent,3),new(x,8),new(x,58));context.DrawEllipse(Accent,null,new Point(x,8),5,5);}
    }
    private double X(double time)=>8+Math.Clamp((time-Start)/Math.Max(.001,End-Start),0,1)*Math.Max(1,Bounds.Width-16);
    private double Time(double x)=>Math.Clamp(Math.Round((Start+(x-8)/Math.Max(1,Bounds.Width-16)*(End-Start))*1000)/1000,Start+.001,End-.001);
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);if(End-Start<=.002||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus();var point=e.GetPosition(this);
        _selected=Cuts.FindIndex(time=>Math.Abs(X(time)-point.X)<10);
        if(_selected<0){if(Cuts.Count>=Core.ClipSplit.MaximumSegments-1)return;Cuts.Add(Time(point.X));_selected=Cuts.Count-1;Changed?.Invoke();}
        _dragging=true;e.Pointer.Capture(this);e.Handled=true;InvalidateVisual();
    }
    protected override void OnPointerMoved(PointerEventArgs e){base.OnPointerMoved(e);if(!_dragging||_selected<0||_selected>=Cuts.Count)return;Cuts[_selected]=Time(e.GetPosition(this).X);Changed?.Invoke();InvalidateVisual();e.Handled=true;}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){_dragging=false;e.Pointer.Capture(null);base.OnPointerReleased(e);}
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e){_dragging=false;base.OnPointerCaptureLost(e);}
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);if(_selected<0||_selected>=Cuts.Count)return;
        if(e.Key is Key.Delete or Key.Back){Cuts.RemoveAt(_selected);_selected=-1;Changed?.Invoke();InvalidateVisual();e.Handled=true;}
        else if(e.Key is Key.Left or Key.Right){Cuts[_selected]=Math.Clamp(Cuts[_selected]+(e.Key==Key.Left?-1:1)*(e.KeyModifiers.HasFlag(KeyModifiers.Shift)?1:.1),Start+.001,End-.001);Changed?.Invoke();InvalidateVisual();e.Handled=true;}
    }
}
