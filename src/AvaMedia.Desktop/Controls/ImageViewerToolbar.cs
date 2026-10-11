using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

internal static class ImageViewerToolbar
{
    // Use text when no familiar icon expresses the action; label icons that share playback shapes.
    public static Button Button(string? kind, string label, Action action, bool showLabel = false)
    {
        var button = new Button(); Configure(button, kind, label, showLabel);
        button.Click += (_, _) => action(); return button;
    }
    public static void Configure(Button button, string? kind, string label, bool showLabel = false)
    {
        var hasText = kind is null || showLabel;
        button.Width = hasText ? double.NaN : 34; button.MinWidth = 34;
        button.Height = button.MinHeight = 34;
        button.Padding = new(hasText ? 8 : 7, 7); button.Margin = new(0, 0, 4, 0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        SetAction(button, kind, label, showLabel);
    }
    public static void SetAction(Button button, string? kind, string label, bool showLabel = false)
    {
        if (kind is null) button.Content = Localization.Text(label);
        else
        {
            var icon = new ImageViewerIcon { Kind = kind, Width = 20, Height = 20 };
            icon.Bind(ImageViewerIcon.ForegroundProperty, new Binding(nameof(Avalonia.Controls.Button.Foreground)) { Source = button });
            button.Content = showLabel ? new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { icon, new TextBlock { Text = Localization.Text(label), VerticalAlignment = VerticalAlignment.Center } }
            } : icon;
        }
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
