using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

// Shared local SVG renderer. Geometry is cached; color follows the containing button or theme.
public sealed class LucideIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<LucideIcon, string>(nameof(Kind), "info");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<LucideIcon, IBrush?>(nameof(Foreground), Brushes.Black);
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static LucideIcon() => AffectsRender<LucideIcon>(KindProperty, ForegroundProperty);
    public override void Render(DrawingContext context) => Draw(context, Bounds.Size, Foreground, Kind);

    internal static void Draw(DrawingContext context, Size size, IBrush? foreground, string kind)
    {
        var scale = Math.Min(size.Width, size.Height) / 24;
        if (scale <= 0 || string.IsNullOrEmpty(kind)) return;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation((size.Width - 24 * scale) / 2, (size.Height - 24 * scale) / 2));
        var pen = new Pen(foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var geometry in LucideIcons.Get(kind))
            context.DrawGeometry(kind == "star-filled" ? foreground : null, pen, geometry);
    }
}
