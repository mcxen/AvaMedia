using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Classic window widgets and choice marks, drawn on a fourteen-pixel grid.</summary>
public sealed class PlatinumGlyph : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<PlatinumGlyph, string>(nameof(Kind), "close");
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<PlatinumGlyph, bool>(nameof(IsActive), true);
    public static readonly StyledProperty<bool> IsPressedProperty = AvaloniaProperty.Register<PlatinumGlyph, bool>(nameof(IsPressed));
    public static readonly StyledProperty<bool?> IsCheckedProperty = AvaloniaProperty.Register<PlatinumGlyph, bool?>(nameof(IsChecked), false);
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsPressed { get => GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
    public bool? IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    static PlatinumGlyph() => AffectsRender<PlatinumGlyph>(KindProperty, IsActiveProperty, IsPressedProperty, IsCheckedProperty, IsEnabledProperty);
    public PlatinumGlyph() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 14;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
        var dark = IsActive && IsEffectivelyEnabled ? Brushes.Black : Brush.Parse("#888888");
        var pen = new Pen(dark, 1);
        if (Kind == "resize")
        {
            for (var i = 4; i <= 12; i += 3)
            {
                context.DrawLine(new Pen(Brushes.White, 1), new(i - 1, 13), new(13, i - 1));
                context.DrawLine(new Pen(Brush.Parse("#777777"), 1), new(i, 13), new(13, i));
            }
            return;
        }
        if (Kind == "radio")
        {
            context.DrawEllipse(IsPressed ? Brush.Parse("#BBBBBB") : Brushes.White, pen, new(7, 7), 5.5, 5.5);
            context.DrawGeometry(null, new Pen(Brush.Parse("#999999"), 1), Geometry.Parse("M 3,8 L 3,5 L 5,3 L 8,3"));
            if (IsChecked == true) context.DrawEllipse(dark, null, new(7, 7), 2.5, 2.5);
            return;
        }
        var face = IsPressed ? "#AAAAAA" : Kind == "check" ? "#FFFFFF" : "#CCCCCC";
        context.DrawRectangle(Brush.Parse(face), pen, new Rect(.5, .5, 13, 13));
        context.DrawGeometry(null, new Pen(IsPressed ? Brush.Parse("#888888") : Brushes.White, 1), Geometry.Parse("M 1.5,12 L 1.5,1.5 L 12,1.5"));
        context.DrawGeometry(null, new Pen(IsPressed ? Brushes.White : Brush.Parse("#777777"), 1), Geometry.Parse("M 12.5,2 L 12.5,12.5 L 2,12.5"));
        switch (Kind)
        {
            case "zoom":
                context.DrawRectangle(null, pen, new Rect(3.5, 3.5, 7, 7));
                context.DrawRectangle(null, pen, new Rect(3.5, 3.5, 4, 4));
                break;
            case "collapse":
                context.DrawRectangle(null, pen, new Rect(3.5, 4.5, 7, 5));
                context.DrawLine(pen, new(4, 6.5), new(10, 6.5));
                break;
            case "check" when IsChecked == true:
                context.DrawGeometry(null, new Pen(dark, 1.5), Geometry.Parse("M 3,7 L 6,10 L 11,3"));
                break;
            case "check" when IsChecked is null:
                context.DrawRectangle(dark, null, new Rect(4, 6, 6, 2));
                break;
        }
    }
}
