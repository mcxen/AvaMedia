using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Stepped folder tab joined to its panel, with a white inner highlight.</summary>
public sealed class PlatinumTabFace : Decorator
{
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<PlatinumTabFace, bool>(nameof(IsSelected));
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    static PlatinumTabFace() => AffectsRender<PlatinumTabFace>(IsSelectedProperty);
    public PlatinumTabFace() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 8 || Bounds.Height < 8) return;
        var bottom = Bounds.Height - .5;
        var right = Bounds.Width - .5;
        var top = IsSelected ? .5 : 2.5;
        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(new Point(.5, bottom), true);
            path.LineTo(new Point(.5, top + 3)); path.LineTo(new Point(3.5, top));
            path.LineTo(new Point(right - 3, top)); path.LineTo(new Point(right, top + 3));
            path.LineTo(new Point(right, bottom)); path.EndFigure(false);
        }
        context.DrawGeometry(Brush.Parse(IsSelected ? "#CCCCCC" : "#BBBBBB"), new Pen(Brushes.Black, 1), outline);
        context.DrawLine(new Pen(Brushes.White, 1), new(1.5, bottom - 1), new(1.5, top + 4));
        context.DrawLine(new Pen(Brushes.White, 1), new(4, top + 1), new(right - 3, top + 1));
        context.DrawLine(new Pen(Brush.Parse("#777777"), 1), new(right - 1, top + 4), new(right - 1, bottom - 1));
        context.DrawLine(new Pen(Brush.Parse(IsSelected ? "#CCCCCC" : "#777777"), 1), new(1, bottom), new(right - 1, bottom));
    }
}
