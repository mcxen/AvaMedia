using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Resolution-independent Platinum bevel; drawn without bitmap assets.</summary>
public sealed class PlatinumBevel : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<PlatinumBevel>();
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public static readonly StyledProperty<Thickness> BorderThicknessProperty = Border.BorderThicknessProperty.AddOwner<PlatinumBevel>();
    public Thickness BorderThickness { get => GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public static readonly StyledProperty<bool> IsPressedProperty = AvaloniaProperty.Register<PlatinumBevel, bool>(nameof(IsPressed));
    public static readonly StyledProperty<bool> IsInsetProperty = AvaloniaProperty.Register<PlatinumBevel, bool>(nameof(IsInset));
    public static readonly StyledProperty<bool> IsFocusedFaceProperty = AvaloniaProperty.Register<PlatinumBevel, bool>(nameof(IsFocusedFace));
    public bool IsPressed { get => GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
    public bool IsInset { get => GetValue(IsInsetProperty); set => SetValue(IsInsetProperty, value); }
    public bool IsFocusedFace { get => GetValue(IsFocusedFaceProperty); set => SetValue(IsFocusedFaceProperty, value); }
    public static readonly StyledProperty<IBrush?> LightBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(LightBrush));
    public static readonly StyledProperty<IBrush?> MidLightBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(MidLightBrush));
    public static readonly StyledProperty<IBrush?> MidDarkBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(MidDarkBrush));
    public static readonly StyledProperty<IBrush?> DarkBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(DarkBrush));
    public static readonly StyledProperty<IBrush?> FocusBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(FocusBrush));
    public IBrush? LightBrush { get => GetValue(LightBrushProperty); set => SetValue(LightBrushProperty,value); }
    public IBrush? MidLightBrush { get => GetValue(MidLightBrushProperty); set => SetValue(MidLightBrushProperty,value); }
    public IBrush? MidDarkBrush { get => GetValue(MidDarkBrushProperty); set => SetValue(MidDarkBrushProperty,value); }
    public IBrush? DarkBrush { get => GetValue(DarkBrushProperty); set => SetValue(DarkBrushProperty,value); }
    public IBrush? FocusBrush { get => GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty,value); }
    static PlatinumBevel()
    {
        AffectsRender<PlatinumBevel>(BackgroundProperty, IsPressedProperty, IsInsetProperty, IsFocusedFaceProperty,LightBrushProperty,MidLightBrushProperty,MidDarkBrushProperty,DarkBrushProperty,FocusBrushProperty);
        AffectsMeasure<PlatinumBevel>(BorderThicknessProperty);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var inset = Padding + BorderThickness;
        Child?.Measure(availableSize.Deflate(inset));
        return (Child?.DesiredSize ?? default).Inflate(inset);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize).Deflate(Padding + BorderThickness));
        return finalSize;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        if (Bounds.Width < 6 || Bounds.Height < 6) return;
        var inset = IsPressed || IsInset;
        Edge(context, 0, inset ? DarkBrush : LightBrush, inset ? LightBrush : DarkBrush);
        Edge(context, 1, inset ? MidDarkBrush : MidLightBrush, inset ? MidLightBrush : MidDarkBrush);
        if (IsFocusedFace) context.DrawRectangle(null, new Pen(FocusBrush, 1), new Rect(2.5, 2.5, Bounds.Width - 5, Bounds.Height - 5));
    }
    private void Edge(DrawingContext context, int inset, IBrush? topLeft, IBrush? bottomRight)
    {
        var x = inset + .5; var right = Bounds.Width - inset - .5; var bottom = Bounds.Height - inset - .5;
        var light = new Pen(topLeft, 1); var dark = new Pen(bottomRight, 1);
        context.DrawLine(light, new(x, bottom), new(x, x)); context.DrawLine(light, new(x, x), new(right, x));
        context.DrawLine(dark, new(right, x), new(right, bottom)); context.DrawLine(dark, new(right, bottom), new(x, bottom));
    }
}
