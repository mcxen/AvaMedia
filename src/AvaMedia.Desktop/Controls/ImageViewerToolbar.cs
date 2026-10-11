using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

internal static class ImageViewerToolbar
{
    public static Button Button(string kind, string label, Action action)
    {
        var button = new Button(); Configure(button, kind, label);
        button.Click += (_, _) => action(); return button;
    }
    public static void Configure(Button button, string kind, string label)
    {
        button.Width = button.MinWidth = 34; button.Height = button.MinHeight = 34;
        button.Padding = new(7); button.Margin = new(0, 0, 4, 0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        SetAction(button, kind, label);
    }
    public static void SetAction(Button button, string kind, string label)
    {
        var icon = new ImageViewerIcon { Kind = kind, Width = 20, Height = 20 };
        icon.Bind(ImageViewerIcon.ForegroundProperty, new Binding(nameof(Avalonia.Controls.Button.Foreground)) { Source = button });
        button.Content = icon;
        ToolTip.SetTip(button, Localization.Text(label)); AutomationProperties.SetName(button, Localization.Text(label));
    }
    public static Control Divider() => new Border
    {
        Width = 1, Height = 20, Background = Brushes.Gray, Opacity = .3,
        VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0)
    };
}

// Lucide SVGs are bundled locally; render their paths with the active theme's foreground.
internal sealed class ImageViewerIcon : Control
{
    public string Kind { get; init; } = "image";
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<ImageViewerIcon, IBrush?>(nameof(Foreground), Brushes.Black);
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static ImageViewerIcon() => AffectsRender<ImageViewerIcon>(ForegroundProperty);
    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var transform = context.PushTransform(Matrix.CreateScale(scale, scale));
        var pen = new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var geometry in LucideViewerIcons.Get(Kind))
            context.DrawGeometry(Kind == "star-filled" ? Foreground : null, pen, geometry);
    }
}
