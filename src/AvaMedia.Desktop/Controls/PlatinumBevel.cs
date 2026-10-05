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
    static PlatinumBevel()
    {
        AffectsRender<PlatinumBevel>(BackgroundProperty, IsPressedProperty, IsInsetProperty, IsFocusedFaceProperty);
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
    private static readonly IBrush Light = Brush.Parse("#FFFFFF"), MidLight = Brush.Parse("#DDDDDD"), MidDark = Brush.Parse("#AAAAAA"), Dark = Brush.Parse("#777777");
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Background, null, new Rect(Bounds.Size));
        if (Bounds.Width < 6 || Bounds.Height < 6) return;
        var inset = IsPressed || IsInset;
        Edge(context, 0, inset ? Dark : Light, inset ? Light : Dark);
        Edge(context, 1, inset ? MidDark : MidLight, inset ? MidLight : MidDark);
        if (IsFocusedFace) context.DrawRectangle(null, new Pen(Brush.Parse("#3D4E80"), 1), new Rect(2.5, 2.5, Bounds.Width - 5, Bounds.Height - 5));
    }
    private void Edge(DrawingContext context, int inset, IBrush topLeft, IBrush bottomRight)
    {
        var x = inset + .5; var right = Bounds.Width - inset - .5; var bottom = Bounds.Height - inset - .5;
        var light = new Pen(topLeft, 1); var dark = new Pen(bottomRight, 1);
        context.DrawLine(light, new(x, bottom), new(x, x)); context.DrawLine(light, new(x, x), new(right, x));
        context.DrawLine(dark, new(right, x), new(right, bottom)); context.DrawLine(dark, new(right, bottom), new(x, bottom));
    }
}
