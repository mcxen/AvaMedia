using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Lucide actions with the existing Mac OS 9 and Windows XP artwork.</summary>
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
        LucideIcon.Draw(context, Bounds.Size, Foreground, Kind switch
        { "previous" => "skip-back", "next" => "skip-forward", _ => Kind });
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
