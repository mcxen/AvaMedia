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
    public static readonly StyledProperty<bool> IsDefaultFaceProperty = AvaloniaProperty.Register<PlatinumBevel, bool>(nameof(IsDefaultFace));
    public static readonly StyledProperty<double> CornerRadiusProperty = AvaloniaProperty.Register<PlatinumBevel, double>(nameof(CornerRadius));
    public static readonly StyledProperty<string> GripProperty = AvaloniaProperty.Register<PlatinumBevel, string>(nameof(Grip), "");
    public bool IsPressed { get => GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
    public bool IsInset { get => GetValue(IsInsetProperty); set => SetValue(IsInsetProperty, value); }
    public bool IsFocusedFace { get => GetValue(IsFocusedFaceProperty); set => SetValue(IsFocusedFaceProperty, value); }
    public bool IsDefaultFace { get => GetValue(IsDefaultFaceProperty); set => SetValue(IsDefaultFaceProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public string Grip { get => GetValue(GripProperty); set => SetValue(GripProperty, value); }
    public static readonly StyledProperty<IBrush?> LightBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(LightBrush));
    public static readonly StyledProperty<IBrush?> MidLightBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(MidLightBrush));
    public static readonly StyledProperty<IBrush?> MidDarkBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(MidDarkBrush));
    public static readonly StyledProperty<IBrush?> DarkBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(DarkBrush));
    public static readonly StyledProperty<IBrush?> FocusBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(FocusBrush));
    public static readonly StyledProperty<IBrush?> OutlineBrushProperty = AvaloniaProperty.Register<PlatinumBevel, IBrush?>(nameof(OutlineBrush));
    public IBrush? LightBrush { get => GetValue(LightBrushProperty); set => SetValue(LightBrushProperty,value); }
    public IBrush? MidLightBrush { get => GetValue(MidLightBrushProperty); set => SetValue(MidLightBrushProperty,value); }
    public IBrush? MidDarkBrush { get => GetValue(MidDarkBrushProperty); set => SetValue(MidDarkBrushProperty,value); }
    public IBrush? DarkBrush { get => GetValue(DarkBrushProperty); set => SetValue(DarkBrushProperty,value); }
    public IBrush? FocusBrush { get => GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty,value); }
    public IBrush? OutlineBrush { get => GetValue(OutlineBrushProperty); set => SetValue(OutlineBrushProperty,value); }
    static PlatinumBevel()
    {
        AffectsRender<PlatinumBevel>(BackgroundProperty, IsPressedProperty, IsInsetProperty, IsFocusedFaceProperty, IsDefaultFaceProperty, CornerRadiusProperty, GripProperty, LightBrushProperty,MidLightBrushProperty,MidDarkBrushProperty,DarkBrushProperty,FocusBrushProperty,OutlineBrushProperty);
        AffectsMeasure<PlatinumBevel>(BorderThicknessProperty);
    }
    public PlatinumBevel() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
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
        if (Bounds.Width < 6 || Bounds.Height < 6) return;
        var offset = IsDefaultFace ? 3 : 0;
        var face = new Rect(offset + .5, offset + .5, Bounds.Width - offset * 2 - 1, Bounds.Height - offset * 2 - 1);
        if (face.Width <= 0 || face.Height <= 0) return;
        if (IsDefaultFace) context.DrawRectangle(null, new Pen(OutlineBrush, 3), new Rect(1.5, 1.5, Bounds.Width - 3, Bounds.Height - 3), CornerRadius + 2, CornerRadius + 2);
        context.DrawRectangle(Background, new Pen(OutlineBrush, 1), face, CornerRadius, CornerRadius);
        var inset = IsPressed || IsInset;
        Edge(context, offset + 1, inset ? DarkBrush : LightBrush, inset ? LightBrush : DarkBrush);
        if (BorderThickness.Left > 2) Edge(context, offset + 2, inset ? MidDarkBrush : MidLightBrush, inset ? MidLightBrush : MidDarkBrush);
        if (IsFocusedFace && !IsDefaultFace) context.DrawRectangle(null, new Pen(FocusBrush, 1), new Rect(2.5, 2.5, Bounds.Width - 5, Bounds.Height - 5), CornerRadius, CornerRadius);
        if (Grip.Length > 0)
        {
            var vertical = Grip == "vertical";
            for (var i = -2; i <= 2; i += 2)
            {
                var center = Math.Floor((vertical ? Bounds.Width : Bounds.Height) / 2) + i + .5;
                var a = vertical ? new Point(center, 3) : new Point(3, center);
                var b = vertical ? new Point(center, Bounds.Height - 3) : new Point(Bounds.Width - 3, center);
                context.DrawLine(new Pen(DarkBrush, 1), a, b);
                context.DrawLine(new Pen(LightBrush, 1), vertical ? a + new Vector(1, 0) : a + new Vector(0, 1), vertical ? b + new Vector(1, 0) : b + new Vector(0, 1));
            }
        }
    }
    private void Edge(DrawingContext context, int inset, IBrush? topLeft, IBrush? bottomRight)
    {
        var x = inset + .5; var right = Bounds.Width - inset - .5; var bottom = Bounds.Height - inset - .5;
        var cut = Math.Max(0, CornerRadius - inset);
        var light = new Pen(topLeft, 1); var dark = new Pen(bottomRight, 1);
        context.DrawLine(light, new(x, bottom - cut), new(x, x + cut)); context.DrawLine(light, new(x + cut, x), new(right - cut, x));
        context.DrawLine(dark, new(right, x + cut), new(right, bottom - cut)); context.DrawLine(dark, new(right - cut, bottom), new(x + cut, bottom));
    }
}
