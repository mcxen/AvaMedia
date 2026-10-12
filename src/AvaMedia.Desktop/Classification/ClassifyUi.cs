using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.Classification;

/// <summary>UISPEC macOS 26 light tokens and small builders for the AI 媒体整理 windows.</summary>
internal static class ClassifyUi
{
    public static readonly IBrush Page = Brush("#F2F3F7"), Surface = Brushes.White, Fill = Brush("#F0F0F3"),
        Hairline = Brush("#E5E5EA"), Ink = Brush("#1D1D1F"), Muted = Brush("#86868B"), Blue = Brush("#007AFF"),
        LightBlue = Brush("#EAF3FF"), BlueSoft = Brush("#8CC0FF"), Green = Brush("#34C759"), GreenText = Brush("#248A3D"),
        Orange = Brush("#FF9500"), Red = Brush("#FF3B30"), Banner = Brush("#FFF8E6"), BannerText = Brush("#8A5A00"),
        Track = Brush("#E8E8ED"), Dark = Brush("#CC1D1D1F");
    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    public static string L(string text) => Localization.Text(text);

    public static TextBlock T(string text, double size = 13, FontWeight weight = FontWeight.Normal, IBrush? brush = null, bool raw = false)
    {
        var block = new TextBlock { Text = raw ? text : L(text), FontSize = size, FontWeight = weight, Foreground = brush ?? Ink,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (raw) Localization.SetIsUserText(block, true);
        return block;
    }

    public static Border Card(Control child, double padding = 14) => new()
    {
        Background = Surface, CornerRadius = new(14), Padding = new(padding), Child = child,
        BoxShadow = BoxShadows.Parse("0 1 3 0 #14000000")
    };

    public static Button Pill(string text, string? icon = null, string? kind = null, Action? click = null, double height = 28, bool iconAfter = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fg = kind switch { "primary" => Brushes.White, "danger" => Red, "link" => Blue, _ => Ink };
        if (icon is not null) content.Children.Add(new LucideIcon { Kind = icon, Width = iconAfter ? 12 : 14, Height = iconAfter ? 12 : 14, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Insert(iconAfter ? 0 : content.Children.Count, new TextBlock { Text = L(text), FontSize = 12.5, FontWeight = kind == "primary" ? FontWeight.SemiBold : FontWeight.Medium,
            Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content, Height = height, Padding = new(14, 0), CornerRadius = new(height / 2), BorderThickness = new(0),
            Background = kind switch { "primary" => Blue, "link" => Brushes.Transparent, _ => Fill },
            VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        button.Classes.Add("classify-pill");
        if (kind is not null) button.Classes.Add(kind);
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    public static void SetPillText(Button button, string text)
    {
        if (button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().LastOrDefault() is { } label) label.Text = L(text);
    }

    public static Control Bar(double fraction, IBrush fill, double height = 4, double secondary = 0)
    {
        var grid = new Grid { Height = height, ColumnDefinitions = new() };
        var a = Math.Clamp(fraction, 0, 1); var b = Math.Clamp(secondary, 0, 1 - a);
        grid.ColumnDefinitions.Add(new(a, GridUnitType.Star)); grid.ColumnDefinitions.Add(new(b, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new(Math.Max(0, 1 - a - b), GridUnitType.Star));
        var back = new Border { Background = Track, CornerRadius = new(height / 2) }; Grid.SetColumnSpan(back, 3);
        grid.Children.Add(back);
        grid.Children.Add(new Border { Background = fill, CornerRadius = new(height / 2) });
        var second = new Border { Background = BlueSoft, CornerRadius = new(height / 2) }; Grid.SetColumn(second, 1); grid.Children.Add(second);
        return grid;
    }

    public static Border Chip(string label, string? value = null, IBrush? background = null, bool rawValue = true)
    {
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(L(label)) { Foreground = Ink });
        if (value is not null) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + (rawValue ? value : L(value))) { Foreground = Blue, FontWeight = FontWeight.SemiBold });
        return new Border { Background = background ?? Fill, CornerRadius = new(11), Padding = new(10, 4), Child = text };
    }

    public static Border Badge(string text, IBrush background, IBrush? foreground = null) => new()
    {
        Background = background, CornerRadius = new(8), Padding = new(5, 1), MinWidth = 18,
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = foreground ?? Brushes.White, HorizontalAlignment = HorizontalAlignment.Center }
    };

    public static Control Dot(IBrush brush, double size = 7) => new Border { Width = size, Height = size, CornerRadius = new(size / 2), Background = brush, VerticalAlignment = VerticalAlignment.Center };

    public static Border Segmented(IReadOnlyList<string> labels, int selected, Action<int> changed, bool raw = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var outer = new Border { Background = Fill, CornerRadius = new(9), Padding = new(2), Child = row, HorizontalAlignment = HorizontalAlignment.Left };
        void Render()
        {
            row.Children.Clear();
            for (var i = 0; i < labels.Count; i++)
            {
                var index = i;
                var button = new Button
                {
                    Content = new TextBlock { Text = raw ? labels[i] : L(labels[i]), FontSize = 12, FontWeight = i == selected ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Ink },
                    Background = i == selected ? Surface : Brushes.Transparent, CornerRadius = new(7), BorderThickness = new(0), Padding = new(11, 4)
                };
                button.Click += (_, _) => { if (selected == index) return; selected = index; Render(); changed(index); };
                row.Children.Add(button);
            }
        }
        Render(); return outer;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024d:0} KB"
    };

    public static string Clock(TimeSpan span) => $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
