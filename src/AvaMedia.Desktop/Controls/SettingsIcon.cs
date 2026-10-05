using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Original vectors for the reference settings layout.</summary>
public sealed class SettingsIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<SettingsIcon, string>(nameof(Kind), "gear");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    static SettingsIcon() => AffectsRender<SettingsIcon>(KindProperty);
    public override void Render(DrawingContext c)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 48;
        using var transform = c.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 48 * scale) / 2, (Bounds.Height - 48 * scale) / 2));
        var blue = Brush.Parse("#169ED8"); var cyan = Brush.Parse("#4AC2E3"); var orange = Brush.Parse("#FFB54B");
        var pen = new Pen(blue, 2); var gray = Brush.Parse("#B8BCC0");
        void Text(string text, double x, double y, double size, IBrush brush) => c.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), size, brush), new(x, y));
        if (Kind is "gear" or "internal" or "smallgear")
        {
            var color = Kind == "internal" ? cyan : Kind == "smallgear" ? gray : blue;
            for (var i = 0; i < 10; i++)
            {
                using var tooth = c.PushTransform(Matrix.CreateTranslation(-24, -24) * Matrix.CreateRotation(i * Math.PI / 5) * Matrix.CreateTranslation(24, 24));
                c.DrawRectangle(color, null, new Rect(20, 2, 8, 13), 2, 2);
            }
            c.DrawEllipse(color, null, new(24, 24), 17, 17); c.DrawEllipse(Brushes.White, null, new(24, 24), 9, 9);
            if (Kind == "gear") c.DrawEllipse(orange, null, new(24, 24), 6, 6);
        }
        else if (Kind is "badge" or "share")
        {
            if (Kind == "share") c.DrawRectangle(blue, null, new Rect(2, 2, 44, 44), 5, 5);
            c.DrawGeometry(Kind == "badge" ? orange : Brushes.White, Kind == "badge" ? new Pen(Brush.Parse("#FF9223"), 1.5) : null,
                Geometry.Parse("M 24,3 L 42,13 L 40,34 L 24,44 L 8,34 L 6,13 Z"));
            Text("V", 14, 9, 27, Kind == "badge" ? Brushes.White : blue);
        }
        else if (Kind == "gpu")
        {
            c.DrawRectangle(null, pen, new Rect(1, 9, 45, 32));
            foreach (var x in new[] { 13d, 34d })
            {
                c.DrawEllipse(null, pen, new(x, 24), 10, 10); c.DrawEllipse(null, pen, new(x, 24), 3, 3);
                for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4; c.DrawLine(pen, new(x + Math.Cos(a) * 4, 24 + Math.Sin(a) * 4), new(x + Math.Cos(a + .4) * 9, 24 + Math.Sin(a + .4) * 9)); }
            }
            c.DrawRectangle(null, pen, new Rect(4, 41, 30, 5));
        }
        else if (Kind == "cpu")
        {
            for (var i = 0; i < 6; i++) { var p = 10 + i * 5; c.DrawLine(pen, new(p, 1), new(p, 8)); c.DrawLine(pen, new(p, 40), new(p, 47)); c.DrawLine(pen, new(1, p), new(8, p)); c.DrawLine(pen, new(40, p), new(47, p)); }
            c.DrawRectangle(Brush.Parse("#C7E9FB"), pen, new Rect(8, 8, 32, 32)); c.DrawRectangle(Brush.Parse("#78BEE8"), pen, new Rect(15, 15, 18, 18));
        }
        else if (Kind is "jpg" or "webp")
        {
            c.DrawGeometry(Brush.Parse("#E4E5E8"), null, Geometry.Parse("M 6,0 L 29,0 L 42,13 L 42,46 L 6,46 Z"));
            c.DrawGeometry(Brush.Parse("#BFC4CC"), null, Geometry.Parse("M 29,0 L 42,13 L 29,13 Z"));
            if (Kind == "webp") c.DrawGeometry(gray, null, Geometry.Parse("M 10,26 L 20,14 L 27,22 L 31,17 L 40,29 Z"));
            c.DrawRectangle(Brush.Parse(Kind == "jpg" ? "#FFA800" : "#F2202A"), null, new Rect(1, 28, 46, 17), 2, 2);
            Text(Kind == "jpg" ? "JPG" : "WEBP", Kind == "jpg" ? 8 : 3, 28, 13, Brushes.White);
        }
        else if (Kind == "cancel") c.DrawGeometry(Brush.Parse("#23B69F"), null, Geometry.Parse("M 20,4 L 2,20 L 20,36 L 20,25 Q 40,23 45,42 Q 48,9 20,13 Z"));
        else if (Kind == "default")
        {
            c.DrawRectangle(null, pen, new Rect(8, 15, 32, 24)); c.DrawRectangle(Brushes.White, pen, new Rect(13, 3, 22, 20)); c.DrawRectangle(Brushes.White, pen, new Rect(13, 28, 22, 16));
        }
        else
        {
            c.DrawEllipse(null, pen, new(24, 24), 17, 17);
            if (Kind == "check") c.DrawGeometry(null, pen, Geometry.Parse("M 14,23 L 22,31 L 35,16"));
            else { c.DrawRectangle(Brush.Parse("#FAFAFA"), null, new Rect(30, 4, 18, 12)); c.DrawGeometry(blue, null, Geometry.Parse("M 38,2 L 38,17 L 25,13 Z")); }
        }
    }
}
