using AvaMedia.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.Classification;

/// <summary>Theme resource keys and small builders for the AI 媒体整理 windows.</summary>
internal static class ClassifyUi
{
    public const string Page = "UiCanvas", Surface = "UiSurface", Fill = "UiFill",
        Hairline = "UiDivider", Ink = "UiText", Muted = "UiTextSecondary", Blue = "UiAccent",
        LightBlue = "UiClassificationNote", BlueSoft = "UiAccentSoft", Green = "UiSuccessFill", GreenText = "UiSuccess",
        Orange = "UiWarning", Red = "UiDanger", Banner = "UiBanner", BannerText = "UiBannerText",
        Track = "UiTrack", Dark = "UiMediaOverlay", White = "UiMediaText", Transparent = "UiTransparent";
    public static DynamicResourceExtension Resource(string? key) => new(key ?? Transparent);
    public static void ApplyToolStyles(Window window)
    {
        window.Classes.Add("ai-specialist");
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AvaMedia.Desktop/"))
        { Source = new("avares://AvaMedia.Desktop/Classification/ClassificationStyles.axaml") });
    }
    public static string L(string text) => Localization.Text(text);

    public static StatusGlyph FileStatus(MediaFileEntry entry)
    {
        var glyph = new StatusGlyph();
        glyph.Bind(StatusGlyph.StateProperty, new Avalonia.Data.Binding(nameof(MediaFileEntry.Status))
        {
            Source = entry,
            Converter = new Avalonia.Data.Converters.FuncValueConverter<string?, string>(status =>
                status?.StartsWith(L("失败"), StringComparison.Ordinal) == true || status == L("错误") ? "failed" : status == L("已完成") || status == L("已分类") || status == L("已重命名") || status == L("已还原") ? "done" : status?.StartsWith(L("分析中"), StringComparison.Ordinal) == true || status?.StartsWith(L("匹配中"), StringComparison.Ordinal) == true || status?.StartsWith(L("识别中"), StringComparison.Ordinal) == true ? "running" : "waiting")
        });
        glyph.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(MediaFileEntry.Details)) { Source = entry });
        return glyph;
    }

    public static TextBlock T(string text, double size = 13, FontWeight weight = FontWeight.Normal, string? brush = null, bool raw = false)
    {
        var block = new TextBlock { Text = raw ? text : L(text), FontSize = Math.Ceiling(size), FontWeight = weight, [!TextBlock.ForegroundProperty] = Resource(brush ?? Ink),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (raw) Localization.SetIsUserText(block, true);
        var font = Math.Ceiling(size) switch
        { 11 => "UiFontTiny", 12 => "UiFontCaption", 13 => "UiFontBody", 14 => "UiFontEmphasis", 15 => "UiFontCardTitle", 17 => "UiFontTitle", _ => null };
        if (font is not null) block.Bind(TextBlock.FontSizeProperty, Resource(font));
        return block;
    }

    public static Border Card(Control child, double padding = 16) => new Card { Child = child };

    public static Button Pill(string text, string? icon = null, string? kind = null, Action? click = null, double height = 28, bool iconAfter = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (icon is not null) content.Children.Add(new LucideIcon { Kind = icon, Width = iconAfter ? 12 : 14, Height = iconAfter ? 12 : 14, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Insert(iconAfter ? 0 : content.Children.Count, new TextBlock { Text = L(text), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, IsDefault = kind == "primary", VerticalContentAlignment = VerticalAlignment.Center };
        button.Classes.Add("ac"); button.Classes.Add(kind ?? "secondary");
        if (height != 28) button.Height = height;
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    public static void SetPillText(Button button, string text)
    {
        if (button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().LastOrDefault() is { } label) label.Text = L(text);
    }

    public static Control Bar(double fraction, string fill, double height = 4, double secondary = 0)
    {
        var grid = new Grid { Height = height, ColumnDefinitions = new() };
        var a = Math.Clamp(fraction, 0, 1); var b = Math.Clamp(secondary, 0, 1 - a);
        grid.ColumnDefinitions.Add(new(a, GridUnitType.Star)); grid.ColumnDefinitions.Add(new(b, GridUnitType.Star));
        grid.ColumnDefinitions.Add(new(Math.Max(0, 1 - a - b), GridUnitType.Star));
        var back = new Border { [!Border.BackgroundProperty] = Resource(Track), CornerRadius = new(height / 2) }; Grid.SetColumnSpan(back, 3);
        grid.Children.Add(back);
        grid.Children.Add(new Border { [!Border.BackgroundProperty] = Resource(fill), CornerRadius = new(height / 2) });
        var second = new Border { [!Border.BackgroundProperty] = Resource(BlueSoft), CornerRadius = new(height / 2) }; Grid.SetColumn(second, 1); grid.Children.Add(second);
        return grid;
    }

    public static Border Chip(string label, string? value = null, string? background = null, bool rawValue = true)
    {
        var text = new TextBlock { [!TextBlock.FontSizeProperty] = Resource("AcCaptionFont"), VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(L(label)) { [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Ink) });
        if (value is not null) text.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + (rawValue ? value : L(value))) { [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Blue), FontWeight = FontWeight.SemiBold });
        return new Border { [!Border.BackgroundProperty] = Resource(background ?? Fill), [!Border.CornerRadiusProperty] = Resource("AcStatusRadius"), [!Border.PaddingProperty] = Resource("AcChipPadding"), Child = text };
    }

    public static Border Badge(string text, string background, string? foreground = null) => new()
    {
        [!Border.BackgroundProperty] = Resource(background), [!Border.CornerRadiusProperty] = Resource("AcListRadius"), Padding = new(5, 1), MinWidth = 18,
        Child = new TextBlock { Text = text, [!TextBlock.FontSizeProperty] = Resource("UiFontTiny"), FontWeight = FontWeight.Bold, [!TextBlock.ForegroundProperty] = Resource(foreground ?? White), HorizontalAlignment = HorizontalAlignment.Center }
    };

    public static Control Dot(string brush, double size = 7) => new Border { Width = size, Height = size, CornerRadius = new(size / 2), [!Border.BackgroundProperty] = Resource(brush), VerticalAlignment = VerticalAlignment.Center };

    public static Border Segmented(IReadOnlyList<string> labels, int selected, Action<int> changed, bool raw = false)
    {
        var segments = new SegmentedControl { ItemsSource = labels.Select(label => raw ? label : L(label)).ToArray(), SelectedIndex = selected };
        segments.SelectionChanged += (_, _) => { if (segments.SelectedIndex >= 0 && segments.SelectedIndex != selected) { selected = segments.SelectedIndex; changed(selected); } };
        return new Border { Child = segments, HorizontalAlignment = HorizontalAlignment.Left };
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024d:0} KB"
    };

    public static string Clock(TimeSpan span) => $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
