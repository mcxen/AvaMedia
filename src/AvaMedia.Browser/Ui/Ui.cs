using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Browser;

/// <summary>Small UI kit: cards, hairline lists, capsules, icons, pills.</summary>
internal static class Ui
{
    public static LucideIcon Icon(string kind, double size = 15, IBrush? brush = null) => new()
    { Kind = kind, Width = size, Height = size, Foreground = brush ?? Palette.Text, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Text(string text, string? classes = null, bool raw = false, bool trim = false)
    {
        var block = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        if (raw) block.Classes.Add("raw");
        if (classes is not null) foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) block.Classes.Add(name);
        if (trim) { block.TextTrimming = TextTrimming.CharacterEllipsis; block.TextWrapping = TextWrapping.NoWrap; }
        block.Text = text; return block;
    }

    public static Border Card(Control child, Thickness? padding = null) => new()
    {
        Background = Palette.Card, CornerRadius = new(14), Child = child, Padding = padding ?? new Thickness(0),
        BoxShadow = BoxShadows.Parse("0 1 2 0 #0F000000, 0 0 0 1 #0D000000"), ClipToBounds = true
    };

    public static Border Hairline(double left = 0) => new() { Height = 1, Background = Palette.Hairline, Margin = new(left, 0, 0, 0) };

    /// <summary>Grouped list (inset grouped table) with hairlines between rows.</summary>
    public static Border Group(params Control[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows) { if (stack.Children.Count > 0) stack.Children.Add(Hairline(12)); stack.Children.Add(new Border { Child = row, Padding = new(12, 0) }); }
        return new Border { Background = Palette.Card, CornerRadius = new(12), Child = stack, BorderBrush = Palette.Hairline, BorderThickness = new(1), ClipToBounds = true };
    }

    public static Control Content(string text, string? icon, IBrush? iconBrush = null, double iconSize = 14)
    {
        if (icon is null) return Text(text);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(Icon(icon, iconSize, iconBrush)); if (text.Length > 0) panel.Children.Add(Text(text));
        return panel;
    }

    public static Border Pill(Control content, IBrush background, Thickness? padding = null) => new()
    { Background = background, CornerRadius = new(999), Padding = padding ?? new Thickness(10, 3), Child = content, VerticalAlignment = VerticalAlignment.Center };

    public static StackPanel Dot(IBrush color, string text, IBrush textBrush)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = color, VerticalAlignment = VerticalAlignment.Center });
        var label = Text(text, raw: false); label.Foreground = textBrush; label.FontSize = 12; panel.Children.Add(label);
        label.MaxWidth = 200; label.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(label, text);
        return panel;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>A label-value line in grouped option lists: label left, control right.</summary>
    public static Grid Line(string label, Control value, double minHeight = 34)
    {
        var grid = new Grid { ColumnDefinitions = new(value is TextBlock ? "Auto,*" : "*,Auto"), ColumnSpacing = 8, MinHeight = minHeight };
        var text = Text(label); text.TextWrapping = TextWrapping.Wrap; text.FontSize = 13; grid.Children.Add(text);
        value.VerticalAlignment = VerticalAlignment.Center; value.HorizontalAlignment = HorizontalAlignment.Right;
        Avalonia.Automation.AutomationProperties.SetName(value, L.T(label));
        Grid.SetColumn(value, 1); grid.Children.Add(value); return grid;
    }

    public static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1 << 20 ? $"{bytes / 1024d:0.#} KB"
        : bytes < 1L << 30 ? $"{bytes / 1048576d:0.#} MB" : $"{bytes / 1073741824d:0.#} GB";

    public static string FileIcon(string name, bool folder)
    {
        if (folder) return "folder-closed";
        var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" or ".ts" or ".flv" or ".wmv" or ".mpg" or ".mts" => "video",
            ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" or ".ogg" or ".opus" or ".wma" or ".aiff" => "audio-lines",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".bmp" or ".tif" or ".tiff" or ".avif" => "image-file",
            ".pdf" or ".txt" or ".doc" or ".docx" or ".md" or ".srt" => "file-text",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "file-archive",
            _ => "file"
        };
    }
}
