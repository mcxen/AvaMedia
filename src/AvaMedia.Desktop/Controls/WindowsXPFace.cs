using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Luna surfaces drawn at device-independent pixel boundaries, without OS bitmap dependencies.</summary>
public sealed class WindowsXPFace : Decorator
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<WindowsXPFace, string>(nameof(Kind), "button");
    public static readonly StyledProperty<bool> IsHotProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsHot));
    public static readonly StyledProperty<bool> IsPressedProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsPressed));
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsSelected));
    public static readonly StyledProperty<bool> IsDefaultFaceProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsDefaultFace));
    public static readonly StyledProperty<bool> IsFocusedFaceProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsFocusedFace));
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsActive), true);
    public static readonly StyledProperty<bool?> IsCheckedProperty = AvaloniaProperty.Register<WindowsXPFace, bool?>(nameof(IsChecked), false);
    public static readonly StyledProperty<bool> IsVerticalProperty = AvaloniaProperty.Register<WindowsXPFace, bool>(nameof(IsVertical));
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public bool IsHot { get => GetValue(IsHotProperty); set => SetValue(IsHotProperty, value); }
    public bool IsPressed { get => GetValue(IsPressedProperty); set => SetValue(IsPressedProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsDefaultFace { get => GetValue(IsDefaultFaceProperty); set => SetValue(IsDefaultFaceProperty, value); }
    public bool IsFocusedFace { get => GetValue(IsFocusedFaceProperty); set => SetValue(IsFocusedFaceProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool? IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public bool IsVertical { get => GetValue(IsVerticalProperty); set => SetValue(IsVerticalProperty, value); }

    static WindowsXPFace()
    {
        AffectsRender<WindowsXPFace>(KindProperty, IsHotProperty, IsPressedProperty,
            IsSelectedProperty, IsDefaultFaceProperty, IsFocusedFaceProperty, IsActiveProperty, IsCheckedProperty, IsVerticalProperty, IsEffectivelyEnabledProperty);
        AffectsArrange<WindowsXPFace>(IsPressedProperty);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var area = new Rect(finalSize).Deflate(Padding);
        if (IsPressed && Kind is "button" or "toolbar" or "arrow") area = area.Translate(new Vector(1, 1));
        Child?.Arrange(area);
        return finalSize;
    }

    internal static IBrush Gradient(params (double Offset, string Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset));
        return brush;
    }

    public override void Render(DrawingContext context)
    {
        var width = Math.Floor(Bounds.Width); var height = Math.Floor(Bounds.Height);
        if (width < 2 || height < 2) return;
        var rect = new Rect(.5, .5, width - 1, height - 1);
        var disabled = !IsEffectivelyEnabled;
        if (Kind == "focus") { if (IsFocusedFace) DrawFocus(context, rect); return; }
        var down = IsPressed || IsSelected;
        if (Kind is "check" or "radio") { DrawChoice(context, rect, disabled); return; }
        if (Kind is "close" or "minimize" or "maximize" or "restore") { DrawCaption(context, rect, disabled); return; }
        if (Kind == "grip") { DrawGrip(context, width, height); return; }
        if (Kind == "category-toggle")
        {
            context.DrawEllipse(Gradient((0, "#FFFFFF"), (1, "#D8E5FB")), new Pen(Brush.Parse("#99B6E0"), 1), rect);
            var arrow = IsSelected ? "M 5,8 L 8,5 L 11,8 M 5,12 L 8,9 L 11,12" : "M 5,5 L 8,8 L 11,5 M 5,9 L 8,12 L 11,9";
            context.DrawGeometry(null, new Pen(Brush.Parse("#215DC6"), 1), Geometry.Parse(arrow));
            return;
        }
        if (Kind == "expander")
        {
            context.DrawRectangle(Brushes.White, new Pen(Brush.Parse("#7F9DB9"), 1), rect);
            context.DrawLine(new Pen(Brush.Parse("#215DC6"), 1), new(3, height / 2), new(width - 3, height / 2));
            if (!IsSelected) context.DrawLine(new Pen(Brush.Parse("#215DC6"), 1), new(width / 2, 3), new(width / 2, height - 3));
            return;
        }

        var radius = Kind is "tab" or "button" ? 3 : 2;
        IBrush fill; string outline;
        switch (Kind)
        {
            case "toolbar":
            case "tile":
                if (!IsHot && !down && !IsFocusedFace) return;
                fill = down ? Brush.Parse("#CEDFF5") : Brush.Parse("#E8F2FF");
                outline = "#316AC5"; radius = 0;
                break;
            case "category":
                fill = Gradient((0, "#FFFFFF"), (.45, "#F1F6FF"), (1, "#C6D3F7"));
                outline = "#FFFFFF";
                break;
            case "tab":
                fill = IsSelected ? Brushes.White : Gradient((0, "#FFFFFF"), (1, "#E0DFD7"));
                outline = "#919B9C";
                break;
            case "scroll":
            case "arrow":
                fill = disabled ? Brush.Parse("#ECE9D8") : down
                    ? Gradient((0, "#8CAEE8"), (1, "#B7CFF7"))
                    : IsHot ? Gradient((0, "#E2ECFF"), (1, "#A8C5F4"))
                    : Gradient((0, "#D5E2FC"), (.5, "#BED3F9"), (1, "#A6C0F0"));
                outline = disabled ? "#CAC8BB" : "#7F9DB9";
                break;
            case "slider":
                fill = Gradient((0, "#FFFFFF"), (1, down ? "#C4D3E5" : "#D9E2EE"));
                outline = "#7F9DB9";
                break;
            default:
                fill = disabled ? Brush.Parse("#F5F4EA") : down
                    ? Gradient((0, "#E5E4D6"), (1, "#F3F2E9"))
                    : Gradient((0, "#FFFFFF"), (.5, "#F5F4EA"), (1, "#ECE9D8"));
                outline = disabled ? "#C9C7BA" : "#003C74";
                break;
        }
        context.DrawRectangle(fill, new Pen(Brush.Parse(outline), 1), rect, radius, radius);
        if (width > 5 && height > 5)
        {
            var inner = new Rect(1.5, 1.5, width - 3, height - 3);
            var emphasized = !disabled && (IsHot || IsDefaultFace);
            var rim = !disabled && IsHot && (Kind is "button" or "tab") ? "#F6B73C" : !disabled && IsDefaultFace ? "#6982EE" : "#FFFFFF";
            context.DrawRectangle(null, new Pen(Brush.Parse(rim), emphasized ? 2 : 1), inner, Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        }
        if (Kind == "tab" && (IsSelected || IsHot))
            context.DrawRectangle(Brush.Parse("#E68B2C"), null, new Rect(3, 1, Math.Max(0, width - 6), 2));
        if (Kind == "scroll")
        {
            var center = new Point(Math.Floor(width / 2), Math.Floor(height / 2));
            for (var i = -3; i <= 3; i += 3)
            {
                var a = IsVertical ? new Point(center.X - 3, center.Y + i + .5) : new Point(center.X + i + .5, center.Y - 3);
                var b = IsVertical ? new Point(center.X + 3, center.Y + i + .5) : new Point(center.X + i + .5, center.Y + 3);
                context.DrawLine(new Pen(Brush.Parse("#6B8FCB"), 1), a, b);
                context.DrawLine(new Pen(Brushes.White, 1), a + new Vector(1, 1), b + new Vector(1, 1));
            }
        }
        if (Kind == "slider")
        {
            if (IsVertical) context.DrawRectangle(Brush.Parse(IsHot ? "#F6B73C" : "#4BAD4B"), null, new Rect(width - 4, 3, 2, Math.Max(0, height - 6)));
            else context.DrawRectangle(Brush.Parse(IsHot ? "#F6B73C" : "#4BAD4B"), null, new Rect(3, height - 4, Math.Max(0, width - 6), 2));
        }
        if (IsFocusedFace && !disabled) DrawFocus(context, new Rect(4.5, 4.5, Math.Max(0, width - 9), Math.Max(0, height - 9)));
    }

    private void DrawChoice(DrawingContext c, Rect r, bool disabled)
    {
        var border = Brush.Parse(disabled ? "#CAC8BB" : IsHot ? "#E6A02A" : "#1C5180");
        var fill = disabled ? Brush.Parse("#EBE8D8") : IsPressed ? Brush.Parse("#DCDACB")
            : Gradient((0, "#DCDCD7"), (.45, "#F4F4F1"), (1, "#FFFFFF"));
        if (Kind == "radio") c.DrawEllipse(fill, new Pen(border, 1), r);
        else c.DrawRectangle(fill, new Pen(border, 1), r);
        var inner = r.Deflate(1.5);
        if (IsHot)
        {
            if (Kind == "radio") c.DrawEllipse(null, new Pen(Brush.Parse("#FFD77C"), 2), inner);
            else c.DrawRectangle(null, new Pen(Brush.Parse("#FFD77C"), 2), inner);
        }
        var green = disabled ? Brush.Parse("#B6B4A5") : Brush.Parse("#21A121");
        if (IsChecked == true)
        {
            if (Kind == "radio") c.DrawEllipse(Gradient((0, "#8BE58B"), (1, "#078407")), null, r.Center, 3, 3);
            else c.DrawGeometry(null, new Pen(green, 2, lineCap: PenLineCap.Square), Geometry.Parse("M 3,6 L 5,9 L 10,3"));
        }
        else if (IsChecked is null) c.DrawRectangle(green, null, r.Deflate(3));
    }

    private void DrawCaption(DrawingContext c, Rect r, bool disabled)
    {
        var close = Kind == "close";
        var fill = close ? IsPressed ? Gradient((0, "#AC321B"), (1, "#DA6045"))
            : IsHot ? Gradient((0, "#FFBEA0"), (.4, "#F46A47"), (1, "#B9321C"))
            : Gradient((0, "#EFA18B"), (.35, "#E76A4B"), (1, "#B6331C"))
            : IsPressed ? Gradient((0, "#174DAD"), (1, "#5C89D7"))
            : IsHot ? Gradient((0, "#A0C5FF"), (.35, "#669DFF"), (1, "#235CC1"))
            : Gradient((0, "#89B4FB"), (.35, "#4F8AF1"), (1, "#2458BB"));
        if (!IsActive) fill = close ? Gradient((0, "#DAB1A7"), (1, "#B98076")) : Gradient((0, "#AFC5E4"), (1, "#6F90BF"));
        c.DrawRectangle(fill, new Pen(Brushes.White, 1), r, 3, 3);
        c.DrawRectangle(null, new Pen(Brush.Parse(close ? "#D75337" : "#2C6AC5"), 1), r.Deflate(1), 2, 2);
        var offset = IsPressed ? 1 : 0;
        using var transform = c.PushTransform(Matrix.CreateTranslation(offset + (Bounds.Width - 21) / 2, offset + (Bounds.Height - 21) / 2));
        var pen = new Pen(disabled ? Brush.Parse("#CDD9EC") : Brushes.White, Kind == "close" ? 2 : 1.5, lineCap: PenLineCap.Square);
        switch (Kind)
        {
            case "close": c.DrawLine(pen, new(6, 6), new(15, 15)); c.DrawLine(pen, new(15, 6), new(6, 15)); break;
            case "minimize": c.DrawRectangle(pen.Brush, null, new Rect(5, 14, 8, 3)); break;
            case "restore":
                c.DrawRectangle(null, pen, new Rect(8, 5, 8, 7));
                c.DrawRectangle(fill, pen, new Rect(5, 9, 8, 7));
                c.DrawLine(new Pen(pen.Brush, 2), new(5, 10), new(13, 10)); break;
            default:
                c.DrawRectangle(null, pen, new Rect(5, 5, 11, 11));
                c.DrawLine(new Pen(pen.Brush, 2), new(5, 6), new(16, 6)); break;
        }
    }

    internal static void DrawFocus(DrawingContext c, Rect r) => c.DrawRectangle(null,
        new Pen(Brushes.Black, 1, new DashStyle(new double[] { 1, 1 }, 0)), r);

    private static void DrawGrip(DrawingContext c, double width, double height)
    {
        for (var row = 0; row < 3; row++)
            for (var column = 0; column <= row; column++)
            {
                var x = width - 4 - column * 4; var y = height - 4 - (2 - row) * 4;
                c.DrawRectangle(Brushes.White, null, new Rect(x + 1, y + 1, 2, 2));
                c.DrawRectangle(Brush.Parse("#ACA899"), null, new Rect(x, y, 2, 2));
            }
    }
}

/// <summary>XP's green blocks, clipped by the existing ProgressBar indicator and its animation.</summary>
public sealed class WindowsXPProgressChunks : Control
{
    public static readonly StyledProperty<bool> IsVerticalProperty = AvaloniaProperty.Register<WindowsXPProgressChunks, bool>(nameof(IsVertical));
    public bool IsVertical { get => GetValue(IsVerticalProperty); set => SetValue(IsVerticalProperty, value); }
    static WindowsXPProgressChunks() => AffectsRender<WindowsXPProgressChunks>(IsVerticalProperty);
    public override void Render(DrawingContext context)
    {
        var brush = WindowsXPFace.Gradient((0, "#A7E58B"), (.25, "#54CC32"), (.7, "#2CB315"), (1, "#78D65A"));
        var length = IsVertical ? Bounds.Height : Bounds.Width;
        using var clip = context.PushClip(new Rect(Bounds.Size));
        for (var position = 0d; position < length; position += 9)
            context.DrawRectangle(brush, null, IsVertical
                ? new Rect(0, position, Bounds.Width, 7) : new Rect(position, 0, 7, Bounds.Height));
    }
}
