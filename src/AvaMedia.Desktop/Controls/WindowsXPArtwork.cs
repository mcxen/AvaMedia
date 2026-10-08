using Avalonia;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Original small, shaded shell illustrations in the XP palette; no extracted Microsoft assets.</summary>
internal static class WindowsXPArtwork
{
    private static IBrush Blue => WindowsXPFace.Gradient((0, "#83B9F5"), (.45, "#3487DF"), (1, "#1756AB"));
    private static IBrush Green => WindowsXPFace.Gradient((0, "#B0E887"), (.45, "#60B432"), (1, "#287914"));
    private static IBrush Gold => WindowsXPFace.Gradient((0, "#FFF0A6"), (.45, "#F3C650"), (1, "#C58C20"));
    private static IBrush Red => WindowsXPFace.Gradient((0, "#FFBFA6"), (.45, "#E56E43"), (1, "#B02B19"));
    private static Pen Outline => new(Brush.Parse("#465B77"), .8, lineJoin: PenLineJoin.Round);

    public static void Feature(DrawingContext c, Size bounds, string kind, string label)
    {
        var scale = Math.Min(bounds.Width, bounds.Height) / 32;
        if (scale <= 0) return;
        using var transform = c.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((bounds.Width - 32 * scale) / 2, (bounds.Height - 32 * scale) / 2));
        c.DrawEllipse(Brush.Parse("#22000000"), null, new(17, 29), 12, 1.5);
        switch (kind)
        {
            case "gear": Gear(c, 16, 16, 12); return;
            case "info":
                c.DrawEllipse(Blue, Outline, new(16, 15), 12, 12);
                c.DrawEllipse(Brushes.White, null, new(16, 9), 1.7, 1.7);
                c.DrawRectangle(Brushes.White, null, new Rect(14.5, 13, 3, 9)); return;
            case "player":
                c.DrawRectangle(Blue, Outline, new Rect(2, 4, 28, 23), 2, 2);
                c.DrawRectangle(Brush.Parse("#E3EFFD"), null, new Rect(4, 8, 24, 14));
                c.DrawRectangle(Brush.Parse("#003C74"), null, new Rect(4, 6, 16, 1));
                c.DrawGeometry(Gold, Outline, Geometry.Parse("M 12,10 L 23,15 L 12,21 Z"));
                c.DrawRectangle(Brush.Parse("#B4CCEC"), null, new Rect(5, 24, 20, 1)); return;
            case "archive": case "zip": case "unzip":
                Folder(c);
                for (var y = 8; y < 27; y += 3) c.DrawRectangle(Brush.Parse("#71674F"), null, new Rect(y % 2 == 0 ? 16 : 18, y, 3, 2));
                c.DrawRectangle(Gold, Outline, new Rect(15, 17, 7, 6), 1, 1);
                if (kind == "unzip") Arrow(c, "up", 21, 19, 9); return;
            case "disc":
                c.DrawEllipse(WindowsXPFace.Gradient((0, "#FFFFFF"), (.4, "#C8D8E9"), (.7, "#E7D4E4"), (1, "#A8B7CB")), Outline, new(16, 16), 13, 13);
                c.DrawGeometry(Brush.Parse("#CCFFFFFF"), null, Geometry.Parse("M 16,4 L 22,6 L 18,13 L 15,13 Z M 6,22 L 13,17 L 14,20 L 10,27 Z"));
                c.DrawEllipse(Brush.Parse("#ECE9D8"), Outline, new(16, 16), 3, 3); return;
            case "clip-list":
                Folder(c); c.DrawRectangle(Brushes.White, Outline, new Rect(8, 3, 18, 20));
                for (var y = 7; y < 21; y += 4) { c.DrawRectangle(Green, null, new Rect(10, y, 2, 2)); c.DrawRectangle(Blue, null, new Rect(14, y, 9, 1)); }
                c.DrawGeometry(Gold, Outline, Geometry.Parse("M 3,17 L 30,17 L 26,28 L 2,28 Z")); return;
        }
        var audio = kind == "audio";
        var image = kind is "image" or "image-compress";
        var document = kind is "document" or "text-pdf" || kind.StartsWith("pdf-", StringComparison.Ordinal);
        Paper(c);
        if (document)
        {
            for (var y = 11; y <= 24; y += 3) c.DrawRectangle(Brush.Parse(y == 11 ? "#3B77BC" : "#9AAFC9"), null, new Rect(8, y, y == 23 ? 10 : 15, 1));
            if (kind.Contains("xlsx", StringComparison.Ordinal))
            {
                c.DrawRectangle(Green, Outline, new Rect(5, 13, 21, 12));
                for (var x = 8; x < 25; x += 5) c.DrawLine(new Pen(Brushes.White, .5), new(x, 15), new(x, 24));
                for (var y = 16; y < 24; y += 3) c.DrawLine(new Pen(Brushes.White, .5), new(6, y), new(25, y));
            }
        }
        else if (audio)
        {
            c.DrawGeometry(Blue, Outline, Geometry.Parse("M 13,10 L 24,7 L 24,21 Q 23,26 19,25 Q 15,24 18,21 L 21,20 L 21,12 L 15,14 L 15,24 Q 14,29 10,28 Q 6,27 9,24 L 12,23 Z"));
        }
        else if (image)
        {
            c.DrawRectangle(Brush.Parse("#B0D9F7"), Outline, new Rect(7, 11, 19, 14));
            c.DrawEllipse(Gold, null, new(21, 15), 2, 2);
            c.DrawGeometry(Green, null, Geometry.Parse("M 7,24 L 14,16 L 18,20 L 22,18 L 26,25 Z"));
        }
        else Film(c);
        switch (kind)
        {
            case "download": Arrow(c, "down", 23, 18, 12); break;
            case "rotate":
                c.DrawGeometry(null, new Pen(Green, 3), Geometry.Parse("M 4,16 A 12,12 0 1 1 22,28"));
                c.DrawGeometry(Green, Outline, Geometry.Parse("M 20,23 L 20,31 L 28,27 Z")); break;
            case "crop": case "clip": Scissors(c); break;
            case "erase":
                c.DrawGeometry(Red, Outline, Geometry.Parse("M 13,22 L 23,11 L 31,18 L 21,29 Z"));
                c.DrawGeometry(Brush.Parse("#E9D8BE"), Outline, Geometry.Parse("M 13,22 L 18,17 L 26,24 L 21,29 Z")); break;
            case "join": Action(c, new Size(16, 16), "plus", new Point(17, 16)); break;
            case "split": Scissors(c); break;
            case "frames":
                for (var i = 0; i < 3; i++) c.DrawRectangle(Blue, new Pen(Brushes.White, 1), new Rect(18 + i * 2, 17 + i * 3, 10, 7)); break;
            case "image-compress": Arrow(c, "down", 22, 20, 9); break;
            case "pdf-merge": Action(c, new Size(16, 16), "plus", new Point(17, 16)); break;
            case "pdf-split": Scissors(c); break;
        }
        if (label.Length > 0)
        {
            var text = label.ToUpperInvariant();
            var color = text == "PDF" || text == "DOCX" ? Red : audio ? Green : Blue;
            var width = Math.Min(30, text.Length * 4.2 + 4);
            c.DrawRectangle(color, new Pen(Brush.Parse("#FFFFFF"), .6), new Rect(0, 1, width, 9), 1, 1);
            c.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Tahoma, Arial", FontStyle.Normal, FontWeight.Bold), 7, Brushes.White), new(2, 1));
        }
    }

    private static void Paper(DrawingContext c)
    {
        c.DrawGeometry(WindowsXPFace.Gradient((0, "#FFFFFF"), (1, "#E7EFF9")), Outline, Geometry.Parse("M 5,2 L 21,2 L 28,9 L 28,29 L 5,29 Z"));
        c.DrawGeometry(Brush.Parse("#C4D9F1"), Outline, Geometry.Parse("M 21,2 L 28,9 L 21,9 Z"));
        c.DrawLine(new Pen(Brushes.White, 1), new(6, 3), new(6, 28));
    }
    private static void Folder(DrawingContext c)
    {
        c.DrawGeometry(Gold, new Pen(Brush.Parse("#AB791D"), 1), Geometry.Parse("M 3,7 L 12,7 L 15,10 L 28,10 L 28,27 L 3,27 Z"));
        c.DrawGeometry(Gold, new Pen(Brush.Parse("#AB791D"), 1), Geometry.Parse("M 4,14 L 31,14 L 27,28 L 1,28 Z"));
        c.DrawLine(new Pen(Brushes.White, 1), new(5, 15), new(29, 15));
    }
    private static void Film(DrawingContext c)
    {
        c.DrawRectangle(Brush.Parse("#394B64"), Outline, new Rect(7, 11, 19, 15), 1, 1);
        c.DrawRectangle(Blue, null, new Rect(10, 12, 13, 12));
        for (var y = 12; y < 26; y += 4)
        { c.DrawRectangle(Brushes.White, null, new Rect(8, y, 1.5, 2)); c.DrawRectangle(Brushes.White, null, new Rect(24, y, 1.5, 2)); }
        c.DrawGeometry(Gold, null, Geometry.Parse("M 14,14 L 21,18 L 14,22 Z"));
    }
    private static void Scissors(DrawingContext c)
    {
        c.DrawGeometry(null, new Pen(Brush.Parse("#7C8C9F"), 2), Geometry.Parse("M 16,27 L 29,11 M 26,27 L 14,11"));
        c.DrawEllipse(null, new Pen(Blue, 2), new(14, 27), 3, 3);
        c.DrawEllipse(null, new Pen(Blue, 2), new(27, 27), 3, 3);
    }
    private static void Arrow(DrawingContext c, string direction, double x, double y, double size)
    {
        using var transform = c.PushTransform(Matrix.CreateScale(size / 12, size / 12) * Matrix.CreateTranslation(x - size / 2, y));
        c.DrawGeometry(Green, new Pen(Brush.Parse("#326422"), .8), Geometry.Parse(direction == "up"
            ? "M 6,0 L 12,6 L 9,6 L 9,12 L 3,12 L 3,6 L 0,6 Z"
            : "M 3,0 L 9,0 L 9,6 L 12,6 L 6,12 L 0,6 L 3,6 Z"));
    }
    private static void Gear(DrawingContext c, double x, double y, double radius)
    {
        c.DrawEllipse(WindowsXPFace.Gradient((0, "#E4EAF2"), (1, "#8298B3")), Outline, new(x, y), radius * .75, radius * .75);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            c.DrawLine(new Pen(Brush.Parse("#879BB6"), radius * .3), new(x + Math.Cos(a) * radius * .65, y + Math.Sin(a) * radius * .65), new(x + Math.Cos(a) * radius, y + Math.Sin(a) * radius));
        }
        c.DrawEllipse(Blue, Outline, new(x, y), radius * .32, radius * .32);
    }

    public static void Action(DrawingContext c, Size bounds, string kind, Point origin = default)
    {
        var scale = Math.Min(bounds.Width, bounds.Height) / 24;
        if (scale <= 0) return;
        using var transform = c.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X + (bounds.Width - 24 * scale) / 2, origin.Y + (bounds.Height - 24 * scale) / 2));
        var stroke = new Pen(Brush.Parse("#3A5F87"), 1.2, lineJoin: PenLineJoin.Round);
        if (kind == "folder") { using var small = c.PushTransform(Matrix.CreateScale(.75, .75)); Folder(c); return; }
        if (kind == "gear") { Gear(c, 12, 12, 10); return; }
        var geometry = kind switch
        {
            "play" => "M 6,3 L 20,12 L 6,21 Z",
            "stop" => "M 4,4 L 20,4 L 20,20 L 4,20 Z",
            "pause" => "M 5,3 L 10,3 L 10,21 L 5,21 Z M 14,3 L 19,3 L 19,21 L 14,21 Z",
            "previous" => "M 3,4 L 6,4 L 6,20 L 3,20 Z M 20,4 L 7,12 L 20,20 Z",
            "next" => "M 18,4 L 21,4 L 21,20 L 18,20 Z M 4,4 L 17,12 L 4,20 Z",
            "backward" => "M 11,4 L 1,12 L 11,20 Z M 22,4 L 12,12 L 22,20 Z",
            "forward" => "M 2,4 L 12,12 L 2,20 Z M 13,4 L 23,12 L 13,20 Z",
            "up" => "M 12,2 L 22,12 L 16,12 L 16,22 L 8,22 L 8,12 L 2,12 Z",
            "down" => "M 8,2 L 16,2 L 16,12 L 22,12 L 12,22 L 2,12 L 8,12 Z",
            "plus" => "M 9,3 L 15,3 L 15,9 L 21,9 L 21,15 L 15,15 L 15,21 L 9,21 L 9,15 L 3,15 L 3,9 L 9,9 Z",
            "minus" => "M 3,9 L 21,9 L 21,15 L 3,15 Z",
            "edit" => "M 4,16 L 16,3 L 22,9 L 10,21 L 3,22 Z",
            "clear" => "M 5,2 L 12,9 L 19,2 L 23,6 L 16,13 L 23,20 L 19,24 L 12,17 L 5,24 L 1,20 L 8,13 L 1,6 Z",
            "check" => "M 2,11 L 6,8 L 10,13 L 19,2 L 23,5 L 10,21 Z",
            "eject" => "M 3,15 L 12,4 L 21,15 Z M 3,18 L 21,18 L 21,21 L 3,21 Z",
            _ => ""
        };
        if (geometry.Length > 0)
        {
            var brush = kind is "stop" or "clear" ? Red : kind is "edit" or "eject" ? Gold : kind is "previous" or "next" or "backward" or "forward" or "pause" ? Blue : Green;
            c.DrawGeometry(brush, stroke, Geometry.Parse(geometry)); return;
        }
        if (kind == "remove")
        { c.DrawEllipse(Red, stroke, new(12, 12), 10, 10); c.DrawRectangle(Brushes.White, null, new Rect(5, 10, 14, 4)); return; }
        if (kind is "reset" or "cancel")
        { c.DrawGeometry(null, new Pen(Blue, 3), Geometry.Parse("M 6,4 L 2,9 L 7,12 M 3,9 L 14,9 Q 22,9 22,16 Q 22,21 13,21")); return; }
        if (kind is "speaker" or "muted")
        {
            c.DrawGeometry(Gold, stroke, Geometry.Parse("M 2,9 L 7,9 L 13,4 L 13,20 L 7,15 L 2,15 Z"));
            c.DrawGeometry(null, new Pen(kind == "muted" ? Red : Blue, 2), Geometry.Parse(kind == "muted" ? "M 17,7 L 23,17 M 23,7 L 17,17" : "M 16,7 Q 22,12 16,17 M 19,3 Q 28,12 19,21")); return;
        }
        if (kind is "menu" or "list")
        {
            for (var y = 4; y <= 20; y += 7) { c.DrawRectangle(Green, null, new Rect(2, y, 3, 3)); c.DrawRectangle(Blue, null, new Rect(8, y, 14, 2)); } return;
        }
        if (kind == "wifi")
        { c.DrawGeometry(null, new Pen(Blue, 2), Geometry.Parse("M 2,7 Q 12,0 22,7 M 5,12 Q 12,6 19,12 M 8,16 Q 12,12 16,16")); c.DrawEllipse(Green, null, new(12, 20), 2, 2); return; }
        if (kind == "camera")
        { c.DrawRectangle(WindowsXPFace.Gradient((0, "#E8EEF6"), (1, "#8597AD")), stroke, new Rect(2, 6, 20, 15), 2, 2); c.DrawEllipse(Blue, stroke, new(12, 13), 5, 5); return; }
        if (kind is "fullscreen" or "window")
        { c.DrawRectangle(Blue, stroke, new Rect(2, 3, 20, 18), 1, 1); c.DrawRectangle(Brushes.White, null, new Rect(4, 8, 16, 11)); return; }
        c.DrawEllipse(Blue, stroke, new(12, 12), 10, 10);
        c.DrawEllipse(Brushes.White, null, new(12, 7), 1.5, 1.5); c.DrawRectangle(Brushes.White, null, new Rect(10.5, 11, 3, 7));
    }
}
