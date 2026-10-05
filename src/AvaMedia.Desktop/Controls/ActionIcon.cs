using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Original small vector icons with stable rendering across platforms.</summary>
public sealed class ActionIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ActionIcon, string>(nameof(Kind), "info");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ActionIcon, IBrush?>(nameof(Foreground), Brush.Parse("#159DD5"));
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public ActionIcon() { Width = 20; Height = 20; }
    static ActionIcon() => AffectsRender<ActionIcon>(KindProperty, ForegroundProperty);
    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2));
        var blue = Brush.Parse("#159DD5"); var orange = Brush.Parse("#ECA026");
        var pen = new Pen(Kind is "up" or "down" ? orange : blue, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var actionPen = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (Kind is "play" or "pause" or "stop")
        {
            if (Kind == "play") context.DrawGeometry(Foreground, null, Geometry.Parse("M 7,4 L 20,12 L 7,20 Z"));
            else if (Kind == "stop") context.DrawRectangle(Foreground, null, new Rect(5,5,14,14));
            else { context.DrawRectangle(Foreground, null, new Rect(6,4,4,16)); context.DrawRectangle(Foreground, null, new Rect(14,4,4,16)); }
        }
        else if (Kind is "backward" or "forward")
        {
            var shape = Kind == "backward" ? "M 12,5 L 5,12 L 12,19 M 20,5 L 13,12 L 20,19" : "M 4,5 L 11,12 L 4,19 M 12,5 L 19,12 L 12,19";
            context.DrawGeometry(null, actionPen, Geometry.Parse(shape));
        }
        else if (Kind is "speaker" or "muted")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 3,9 L 7,9 L 12,5 L 12,19 L 7,15 L 3,15 Z"));
            context.DrawGeometry(null, actionPen, Geometry.Parse(Kind == "muted" ? "M 16,8 L 22,16 M 22,8 L 16,16" : "M 16,8 Q 21,12 16,16 M 19,4 Q 27,12 19,20"));
        }
        else if (Kind is "plus" or "minus")
        {
            context.DrawLine(actionPen, new(5,12), new(19,12));
            if (Kind == "plus") context.DrawLine(actionPen, new(12,5), new(12,19));
        }
        else if (Kind == "folder")
        {
            context.DrawGeometry(Brush.Parse("#FFD061"), new Pen(Brush.Parse("#C29024"), 1), Geometry.Parse("M 2,6 L 9,6 L 11,9 L 22,9 L 21,21 L 2,21 Z"));
            context.DrawGeometry(Brush.Parse("#FFE6A6"), null, Geometry.Parse("M 4,5 L 9,5 L 11,8 L 20,8 L 20,10 L 4,10 Z"));
        }
        else if (Kind is "up" or "down")
        {
            var top = Kind == "up" ? 3 : 21; var bottom = Kind == "up" ? 21 : 3; var wing = Kind == "up" ? 10 : 14;
            context.DrawLine(pen, new(12, top), new(12, bottom));
            context.DrawLine(pen, new(5, wing), new(12, top)); context.DrawLine(pen, new(19, wing), new(12, top));
        }
        else if (Kind == "remove")
        {
            context.DrawEllipse(Brush.Parse("#E56854"), null, new(12, 12), 9, 9);
            context.DrawLine(new Pen(Brushes.White, 2.5, lineCap: PenLineCap.Round), new(7, 12), new(17, 12));
        }
        else
        {
            context.DrawRectangle(blue, null, new Rect(3, 3, 18, 18), 2, 2);
            context.DrawEllipse(Brushes.White, null, new(12, 7), 1.3, 1.3);
            context.DrawLine(new Pen(Brushes.White, 2.5, lineCap: PenLineCap.Round), new(12, 11), new(12, 17));
        }
    }
}
