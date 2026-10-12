using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

// Lucide 1.55.0, ISC/MIT. Original SVGs and license notices are shipped with the application.
internal static class LucideIcons
{
    private static readonly Dictionary<string, Geometry[]> Cache = new();
    public static Geometry[] Get(string action)
    {
        var name = action switch
        {
            "image" => "image-plus", "folder" => "folder-open", "recent" => "history",
            "star-filled" => "star", "photo" => "image", "library" => "panel-left", "fullscreen" => "maximize",
            "previous" => "chevron-left", "next" => "chevron-right", "fit" => "expand", "actual" => "scaling",
            "rotate-left" => "rotate-ccw", "rotate-right" => "rotate-cw", "flip" => "flip-horizontal",
            "spread" => "book-open", "stop" => "square", "settings" => "sliders-horizontal", "close" => "x",
            "more" => "ellipsis", "window" => "square",
            "backward" => "rewind", "forward" => "fast-forward", "speaker" => "volume-2", "muted" => "volume-x",
            "minus" => "minus", "up" => "arrow-up", "down" => "arrow-down", "remove" => "circle-minus",
            "edit" => "pencil", "clear" or "cancel" => "x", "gear" => "settings", "reset" => "refresh-cw",
            _ => action
        };
        if (Cache.TryGetValue(name, out var geometries)) return geometries;
        using var stream = typeof(LucideIcons).Assembly.GetManifestResourceStream($"AvaMedia.Desktop.Lucide.{name}.svg")
            ?? throw new InvalidOperationException("Missing Lucide icon: " + name);
        geometries = XDocument.Load(stream).Root!.Elements().Select(Parse).ToArray();
        Cache.Add(name, geometries); return geometries;
    }
    private static Geometry Parse(XElement element)
    {
        double Number(string name, double fallback = 0) => element.Attribute(name) is { } attribute
            ? double.Parse(attribute.Value, CultureInfo.InvariantCulture) : fallback;
        var x = Number("x"); var y = Number("y");
        return element.Name.LocalName switch
        {
            "path" => Geometry.Parse(element.Attribute("d")!.Value),
            "circle" => new EllipseGeometry(new Rect(Number("cx") - Number("r"), Number("cy") - Number("r"), Number("r") * 2, Number("r") * 2)),
            "line" => Geometry.Parse(FormattableString.Invariant($"M {Number("x1")},{Number("y1")} L {Number("x2")},{Number("y2")}")),
            "rect" => Rectangle(x, y, Number("width"), Number("height"), Number("rx"), Number("ry", Number("rx"))),
            _ => throw new InvalidOperationException("Unsupported Lucide SVG element: " + element.Name.LocalName)
        };
    }
    private static Geometry Rectangle(double x, double y, double width, double height, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0) return new RectangleGeometry(new Rect(x, y, width, height));
        return Geometry.Parse(FormattableString.Invariant($"M {x + rx},{y} H {x + width - rx} A {rx},{ry} 0 0 1 {x + width},{y + ry} V {y + height - ry} A {rx},{ry} 0 0 1 {x + width - rx},{y + height} H {x + rx} A {rx},{ry} 0 0 1 {x},{y + height - ry} V {y + ry} A {rx},{ry} 0 0 1 {x + rx},{y} Z"));
    }
}
