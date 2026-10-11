using Avalonia;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

// Desktop-only skin drawing; range gestures and bounds are shared with the browser.
public sealed class RangeBar : MediaUi.RangeBar
{
    public override void Render(DrawingContext c)
    {
        double w=Math.Max(1,Bounds.Width-28),l=14+w*Start/Math.Max(double.Epsilon,Duration),r=14+w*End/Math.Max(double.Epsilon,Duration);
        if (ActualThemeVariant == Skin.WindowsXP)
        {
            var center = Math.Floor(Bounds.Height / 2);
            c.DrawRectangle(Brush.Parse("#E7E5DC"), new Pen(Brush.Parse("#9D9C99"), 1), new Rect(0, center - 2, Bounds.Width, 4));
            c.DrawRectangle(WindowsXPFace.Gradient((0, "#85B6ED"), (1, "#316AC5")), null, new Rect(l, center - 2, Math.Max(0, r - l), 4));
            foreach (var x in new[] { l, r })
            {
                c.DrawRectangle(WindowsXPFace.Gradient((0, "#FFFFFF"), (1, "#D9E2EE")), new Pen(BorderBrush, 1), new Rect(x - 6, 2, 12, Math.Max(1, Bounds.Height - 4)), 2, 2);
                c.DrawRectangle(Brush.Parse("#4BAD4B"), null, new Rect(x - 4, Bounds.Height - 6, 8, 2));
            }
            return;
        }
        base.Render(c);
    }
}
