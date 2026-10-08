using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Original small vector icons with stable rendering across platforms.</summary>
public sealed class ActionIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ActionIcon, string>(nameof(Kind), "info");
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ActionIcon, IBrush?>(nameof(Foreground));
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public static readonly StyledProperty<IBrush?> ContrastBrushProperty = AvaloniaProperty.Register<ActionIcon, IBrush?>(nameof(ContrastBrush));
    public IBrush? ContrastBrush { get => GetValue(ContrastBrushProperty); set => SetValue(ContrastBrushProperty, value); }
    public static readonly StyledProperty<bool> IsPlatinumProperty = AvaloniaProperty.Register<ActionIcon, bool>(nameof(IsPlatinum));
    public bool IsPlatinum { get => GetValue(IsPlatinumProperty); set => SetValue(IsPlatinumProperty, value); }
    public static readonly StyledProperty<bool> IsWindowsXPProperty = AvaloniaProperty.Register<ActionIcon, bool>(nameof(IsWindowsXP));
    public bool IsWindowsXP { get => GetValue(IsWindowsXPProperty); set => SetValue(IsWindowsXPProperty, value); }
    static ActionIcon() => AffectsRender<ActionIcon>(KindProperty, ForegroundProperty, ContrastBrushProperty, IsPlatinumProperty, IsWindowsXPProperty);
    public ActionIcon() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPlatinumProperty) RenderOptions.SetEdgeMode(this, IsPlatinum ? EdgeMode.Aliased : EdgeMode.Unspecified);
    }
    public override void Render(DrawingContext context)
    {
        if (IsWindowsXP || ActualThemeVariant == Skin.WindowsXP) { WindowsXPArtwork.Action(context, Bounds.Size, Kind); return; }
        if (IsPlatinum) { RenderPlatinum(context); return; }
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2));
        var pen = new Pen(Foreground, 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var actionPen = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        if (Kind is "play" or "pause" or "stop")
        {
            if (Kind == "play") context.DrawGeometry(Foreground, null, Geometry.Parse("M 7,4 L 20,12 L 7,20 Z"));
            else if (Kind == "stop") context.DrawRectangle(Foreground, null, new Rect(5,5,14,14));
            else { context.DrawRectangle(Foreground, null, new Rect(6,4,4,16)); context.DrawRectangle(Foreground, null, new Rect(14,4,4,16)); }
        }
        else if (Kind is "menu" or "fullscreen" or "window" or "previous" or "next" or "eject")
        {
            var shape = Kind switch
            {
                "menu" => "M 4,6 L 20,6 M 4,12 L 20,12 M 4,18 L 20,18",
                "fullscreen" => "M 9,3 L 3,3 L 3,9 M 15,3 L 21,3 L 21,9 M 3,15 L 3,21 L 9,21 M 15,21 L 21,21 L 21,15",
                "window" => "M 4,4 L 20,4 L 20,20 L 4,20 Z",
                "previous" => "M 5,4 L 5,20 M 19,5 L 8,12 L 19,19 Z",
                "next" => "M 19,4 L 19,20 M 5,5 L 16,12 L 5,19 Z",
                _ => "M 4,15 L 12,5 L 20,15 Z M 4,20 L 20,20"
            };
            context.DrawGeometry(null, actionPen, Geometry.Parse(shape));
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
        else if (Kind == "wifi")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 2,7 Q 12,-1 22,7 M 5,11 Q 12,5 19,11 M 8,15 Q 12,11 16,15"));
            context.DrawEllipse(Foreground, null, new(12, 19), 1.8, 1.8);
        }
        else if (Kind == "folder")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 2,6 L 9,6 L 11,9 L 22,9 L 21,21 L 2,21 Z M 4,5 L 9,5 L 11,8 L 20,8"));
        }
        else if (Kind is "up" or "down")
        {
            var top = Kind == "up" ? 3 : 21; var bottom = Kind == "up" ? 21 : 3; var wing = Kind == "up" ? 10 : 14;
            context.DrawLine(pen, new(12, top), new(12, bottom));
            context.DrawLine(pen, new(5, wing), new(12, top)); context.DrawLine(pen, new(19, wing), new(12, top));
        }
        else if (Kind == "remove")
        {
            context.DrawEllipse(Foreground, null, new(12, 12), 9, 9);
            context.DrawLine(new Pen(ContrastBrush, 2.5, lineCap: PenLineCap.Round), new(7, 12), new(17, 12));
        }
        else if (Kind == "edit")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 4,15 L 15,4 L 20,9 L 9,20 L 3,21 Z M 12,7 L 17,12"));
        }
        else if (Kind == "clear")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 6,6 L 18,18 M 18,6 L 6,18"));
        }
        else if (Kind == "list")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 4,5 L 20,5 M 4,12 L 20,12 M 4,19 L 20,19"));
        }
        else if (Kind == "camera")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 3,7 L 7,7 L 9,4 L 15,4 L 17,7 L 21,7 L 21,20 L 3,20 Z"));
            context.DrawEllipse(null, actionPen, new(12,13), 4, 4);
        }
        else if (Kind == "gear")
        {
            context.DrawEllipse(null, actionPen, new(12,12), 6, 6);
            context.DrawEllipse(null, actionPen, new(12,12), 2, 2);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                context.DrawLine(actionPen, new(12 + 6 * Math.Cos(angle),12 + 6 * Math.Sin(angle)), new(12 + 9 * Math.Cos(angle),12 + 9 * Math.Sin(angle)));
            }
        }
        else if (Kind == "check")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 4,12 L 9,17 L 20,6"));
        }
        else if (Kind is "reset" or "cancel")
        {
            context.DrawGeometry(null, actionPen, Geometry.Parse("M 7,5 L 3,9 L 7,13 M 3,9 L 14,9 Q 21,9 21,16 Q 21,21 14,21"));
        }
        else
        {
            context.DrawRectangle(Foreground, null, new Rect(3, 3, 18, 18), 2, 2);
            context.DrawEllipse(ContrastBrush, null, new(12, 7), 1.3, 1.3);
            context.DrawLine(new Pen(ContrastBrush, 2.5, lineCap: PenLineCap.Round), new(12, 11), new(12, 17));
        }
    }
    private void RenderPlatinum(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 16;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 16 * scale) / 2, (Bounds.Height - 16 * scale) / 2));
        var pen = new Pen(Foreground, 1, lineCap: PenLineCap.Square, lineJoin: PenLineJoin.Miter);
        var shape = Kind switch
        {
            "play" => "M 4,2 L 13,8 L 4,14 Z",
            "stop" => "M 3,3 L 13,3 L 13,13 L 3,13 Z",
            "pause" => "M 4,2 L 6,2 L 6,14 L 4,14 Z M 10,2 L 12,2 L 12,14 L 10,14 Z",
            "previous" => "M 2,2 L 4,2 L 4,14 L 2,14 Z M 13,2 L 5,8 L 13,14 Z",
            "next" => "M 12,2 L 14,2 L 14,14 L 12,14 Z M 3,2 L 11,8 L 3,14 Z",
            "backward" => "M 8,2 L 1,8 L 8,14 Z M 15,2 L 8,8 L 15,14 Z",
            "forward" => "M 1,2 L 8,8 L 1,14 Z M 8,2 L 15,8 L 8,14 Z",
            "eject" => "M 3,10 L 8,3 L 13,10 Z M 3,12 L 13,12 L 13,14 L 3,14 Z",
            "menu" or "list" => "M 2,3 L 14,3 M 2,8 L 14,8 M 2,13 L 14,13",
            "fullscreen" => "M 6,2 L 2,2 L 2,6 M 10,2 L 14,2 L 14,6 M 2,10 L 2,14 L 6,14 M 10,14 L 14,14 L 14,10",
            "window" => "M 2,2 L 14,2 L 14,14 L 2,14 Z M 2,5 L 14,5",
            "clear" => "M 3,3 L 13,13 M 13,3 L 3,13",
            "edit" => "M 2,10 L 10,2 L 14,6 L 6,14 L 1,15 Z M 8,4 L 12,8",
            "plus" => "M 2,8 L 14,8 M 8,2 L 8,14",
            "minus" or "remove" => "M 2,8 L 14,8",
            "up" => "M 8,2 L 8,14 M 3,7 L 8,2 L 13,7",
            "down" => "M 8,2 L 8,14 M 3,9 L 8,14 L 13,9",
            "check" => "M 2,8 L 6,12 L 14,3",
            "reset" or "cancel" => "M 5,2 L 2,5 L 5,8 M 2,5 L 10,5 L 13,8 L 13,12 L 10,14 L 6,14",
            "camera" => "M 2,5 L 5,5 L 6,3 L 10,3 L 11,5 L 14,5 L 14,13 L 2,13 Z",
            "speaker" or "muted" => "M 2,6 L 5,6 L 8,3 L 8,13 L 5,10 L 2,10 Z",
            "folder" => "M 1,4 L 6,4 L 8,6 L 15,6 L 14,14 L 1,14 Z",
            "wifi" => "M 1,5 L 4,3 L 12,3 L 15,5 M 4,8 L 6,6 L 10,6 L 12,8 M 6,11 L 8,9 L 10,11 M 8,13 L 8,14",
            "gear" => "M 6,1 L 10,1 L 10,3 L 12,4 L 14,3 L 15,6 L 13,7 L 13,9 L 15,10 L 14,13 L 12,12 L 10,13 L 10,15 L 6,15 L 6,13 L 4,12 L 2,13 L 1,10 L 3,9 L 3,7 L 1,6 L 2,3 L 4,4 L 6,3 Z",
            _ => "M 2,2 L 14,2 L 14,14 L 2,14 Z M 8,6 L 8,12 M 8,3 L 8,4"
        };
        var filled = Kind is "play" or "pause" or "stop" or "previous" or "next" or "backward" or "forward" or "eject";
        context.DrawGeometry(filled ? Foreground : Kind == "folder" ? Brush.Parse("#DDDDAA") : Kind is "gear" or "camera" ? Brush.Parse("#BBBBBB") : null, pen, Geometry.Parse(shape));
        if (Kind == "folder") context.DrawLine(new Pen(Brushes.White, 1), new(2, 7), new(13, 7));
        if (Kind == "camera") context.DrawEllipse(Brushes.White, pen, new(8, 9), 2, 2);
        if (Kind == "gear") context.DrawEllipse(Brushes.White, pen, new(8, 8), 2, 2);
        if (Kind is "speaker" or "muted") context.DrawGeometry(null, pen, Geometry.Parse(Kind == "muted" ? "M 11,5 L 15,11 M 15,5 L 11,11" : "M 10,5 L 12,7 L 12,9 L 10,11 M 13,3 L 15,6 L 15,10 L 13,13"));
    }
}
