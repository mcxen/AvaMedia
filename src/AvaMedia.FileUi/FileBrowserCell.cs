using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.FileUi;

/// <summary>A realized card or list row. Decoded images exist only for realized controls.</summary>
internal sealed class FileBrowserCell : AvaMedia.Controls.ListRow
{
    public FileBrowserEntry Entry { get; }
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Control _placeholder;
    private readonly TextBlock _duration = new();
    private readonly TextBlock _resolution = new();
    private readonly TextBlock _metadata = new();
    private readonly Border _check = new() { IsHitTestVisible = false, Width = 16, Height = 16, BorderThickness = new(1) };
    private readonly Control _mark;
    private readonly bool _grid, _preview;
    private bool _selected, _focused;
    private Bitmap? _bitmap;
    private FileBrowserMedia? _applied;
    private readonly TextBlock _name;
    private readonly TextBlock? _location, _modified;

    public FileBrowserCell(FileBrowserEntry entry, bool grid, IFileBrowserGlyphs? glyphs, Func<string, string> text, bool preview = false)
    {
        Entry = entry; _grid = grid; _preview = preview;
        _mark = glyphs?.Glyph("check-white") ?? new Avalonia.Controls.Shapes.Path
        { Data = Geometry.Parse("M3,8 L6,11 L13,4"), StrokeThickness = 2, Width = 14, Height = 14 };
        if (_mark is Avalonia.Controls.Shapes.Path checkPath) FileBrowserTheme.Bind(checkPath, Avalonia.Controls.Shapes.Shape.StrokeProperty, "CheckText");
        _check.Child = _mark; FileBrowserTheme.Bind(_check, CornerRadiusProperty, "CheckRadius");
        BorderThickness = new(2); Padding = new(8); Margin = new(0, 0, 0, grid ? 12 : 2);
        MinHeight = 0;
        if (grid) { FileBrowserTheme.Bind(this, PaddingProperty, "CardPadding"); FileBrowserTheme.Bind(this, MarginProperty, "RowGap"); }
        FileBrowserTheme.Bind(this, CornerRadiusProperty, grid ? "CardRadius" : "Radius");
        var cover = new Grid(); cover.Children.Add(_image);
        _placeholder = glyphs?.Glyph(FileBrowserKinds.Glyph(entry.Item)) ?? new TextBlock { Text = text(entry.IsFolder ? "文件夹" : FileBrowserKinds.Name(FileBrowserKinds.FromName(entry.Name))), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _placeholder.Width = grid ? 32 : 20; _placeholder.Height = grid ? 32 : 20;
        _placeholder.HorizontalAlignment = HorizontalAlignment.Center; _placeholder.VerticalAlignment = VerticalAlignment.Center;
        cover.Children.Add(_placeholder);
        _duration.FontSize = grid ? 10.5 : 11.5; _duration.Text = ""; _duration.Margin = new(3); _duration.VerticalAlignment = VerticalAlignment.Bottom; _duration.HorizontalAlignment = HorizontalAlignment.Right;
        _duration.FontFeatures = new() { FontFeature.Parse("tnum") };
        if (grid)
        {
            var durationBadge = new Border { Child = _duration, Padding = new(1, 0), Margin = new(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            FileBrowserTheme.Bind(durationBadge, BackgroundProperty, "DurationFill"); FileBrowserTheme.Bind(durationBadge, CornerRadiusProperty, "DurationRadius");
            FileBrowserTheme.Bind(_duration, TextBlock.ForegroundProperty, "DurationText");
            durationBadge.IsVisible = false;
            _duration.PropertyChanged += (_, change) => { if (change.Property == TextBlock.TextProperty) durationBadge.IsVisible = !string.IsNullOrEmpty(_duration.Text) && _duration.Text != "—"; };
            cover.Children.Add(durationBadge);
        }
        var coverHost = new Border { Child = cover, ClipToBounds = true };
        FileBrowserTheme.Bind(coverHost, BackgroundProperty, "Fill"); FileBrowserTheme.Bind(coverHost, CornerRadiusProperty, "Radius");
        var name = _name = new TextBlock { Text = entry.Name, FontSize = preview ? 15 : 12.5, FontWeight = FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        name.Classes.Add("raw"); ToolTip.SetTip(name, entry.Item.Address);
        if (preview) { name.TextWrapping = TextWrapping.Wrap; name.FontWeight = FontWeight.SemiBold; }
        else name.TextTrimming = TextTrimming.CharacterEllipsis;
        _metadata.Text = entry.IsFolder ? text("文件夹") : Size(entry.Size); _metadata.FontSize = 11; FileBrowserTheme.Bind(_metadata, TextBlock.ForegroundProperty, "Muted");
        if (grid)
        {
            var content = new Grid { RowDefinitions = new("Auto,Auto,Auto"), RowSpacing = 4 };
            if (preview) { content.RowDefinitions = new("Auto,Auto"); content.RowSpacing = 12; }
            var imageArea = new Grid { Height = preview ? 140 : 96 };
            SizeChanged += (_, _) => { imageArea.Height = Math.Max(48, (Bounds.Width - (preview ? 0 : 20)) * 9 / 16); if (!preview) Height = imageArea.Height + 70; }; imageArea.Children.Add(coverHost);
            if (!preview) { _check.Margin = new(6); _check.HorizontalAlignment = HorizontalAlignment.Left; _check.VerticalAlignment = VerticalAlignment.Top; imageArea.Children.Add(_check); }
            content.Children.Add(imageArea); Grid.SetRow(name, 1); content.Children.Add(name);
            if (!preview) { Grid.SetRow(_metadata, 2); content.Children.Add(_metadata); FileBrowserTheme.Bind(this, HeightProperty, "GridRowHeight"); }
            else { Height = double.NaN; MinHeight = 0; Padding = new(0); Margin = new(0); BorderThickness = new(0); }
            Child = content;
        }
        else
        {
            FileBrowserTheme.Bind(this, HeightProperty, "RowHeight");
            var content = new Grid { ColumnDefinitions = new("26,50,*,72,106,78,128"), ColumnSpacing = 8 };
            _check.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(_check);
            coverHost.Height = 32; Grid.SetColumn(coverHost, 1); content.Children.Add(coverHost);
            var names = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center }; names.Children.Add(name);
            var location = _location = new TextBlock { Text = System.IO.Path.GetDirectoryName(entry.Item.Address), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            FileBrowserTheme.Bind(location, TextBlock.ForegroundProperty, "Muted"); names.Children.Add(location);
            Grid.SetColumn(names, 2); content.Children.Add(names);
            _duration.Text = "—"; _duration.VerticalAlignment = VerticalAlignment.Center; _duration.HorizontalAlignment = HorizontalAlignment.Left; _duration.Margin = new(0);
            FileBrowserTheme.Bind(_duration, TextBlock.BackgroundProperty, "Surface");
            _resolution.Text = "—"; _resolution.FontSize = 11.5; _resolution.VerticalAlignment = VerticalAlignment.Center;
            _metadata.VerticalAlignment = VerticalAlignment.Center;
            var modified = _modified = new TextBlock { Text = entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            FileBrowserTheme.Bind(modified, TextBlock.ForegroundProperty, "Muted");
            Grid.SetColumn(_duration, 3); content.Children.Add(_duration); Grid.SetColumn(_resolution, 4); content.Children.Add(_resolution);
            Grid.SetColumn(_metadata, 5); content.Children.Add(_metadata); Grid.SetColumn(modified, 6); content.Children.Add(modified); Child = content;
        }
        PointerEntered += (_, _) => UpdateFace(true); PointerExited += (_, _) => UpdateFace(false);
        UpdateSelection(false, false);
    }
    public void UpdateSelection(bool selected, bool focused)
    {
        _selected = selected; _focused = focused; _mark.IsVisible = selected;
        FileBrowserTheme.Bind(_check, BackgroundProperty, selected ? "Accent" : "CheckFace");
        FileBrowserTheme.Bind(_check, BorderBrushProperty, selected ? "Accent" : "CheckLine"); UpdateFace(IsPointerOver);
    }
    private void UpdateFace(bool hover)
    {
        FileBrowserTheme.Bind(this, BackgroundProperty, _selected ? "Selected" : hover && !_preview ? "Hover" : _preview ? "Panel" : "Surface");
        FileBrowserTheme.Bind(this, BorderBrushProperty, _selected && _grid || _focused ? "Accent" : _preview ? "Panel" : "Surface");
        // Classic skins use a dark selection; every child must follow its selection text resource.
        foreach (var label in new[] { _metadata, _resolution, _duration, _location, _modified })
            if (label is not null) FileBrowserTheme.Bind(label, TextBlock.ForegroundProperty, _selected && (!_grid || !ReferenceEquals(label, _duration)) ? "SelectedMuted" : ReferenceEquals(label, _duration) && _grid ? "DurationText" : "Muted");
        FileBrowserTheme.Bind(_name, TextBlock.ForegroundProperty, _selected ? "SelectionText" : "Text");
        FileBrowserTheme.Bind(this, TextElement.ForegroundProperty, _selected ? "SelectionText" : "Text");
        if (!_grid) FileBrowserTheme.Bind(_duration, TextBlock.BackgroundProperty, _selected ? "Selected" : hover ? "Hover" : "Surface");
    }
    public void ApplyMedia(FileBrowserMedia media)
    {
        if (ReferenceEquals(_applied, media)) return;
        _applied = media;
        _duration.Text = Duration(media.Duration); _duration.IsVisible = !_grid || media.Duration > 0;
        _resolution.Text = Resolution(media);
        if (_grid && !_preview) _metadata.Text = (media.Width > 0 ? Resolution(media) + " · " : "") + Size(Entry.Size);
        if (media.Thumbnail is not { Length: > 0 } bytes || _bitmap is not null) return;
        try
        {
            using var stream = new MemoryStream(bytes); _bitmap = Bitmap.DecodeToWidth(stream, _grid ? 480 : 96);
            _image.Source = _bitmap; _placeholder.IsVisible = false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { }
    }
    public void ReleaseImage() { _image.Source = null; _bitmap?.Dispose(); _bitmap = null; _applied = null; _placeholder.IsVisible = true; }
    internal static string Duration(double seconds) => !double.IsFinite(seconds) || seconds <= 0 ? "—" : seconds >= 3600
        ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
    internal static string Resolution(FileBrowserMedia media) => media.Width > 0 && media.Height > 0 ? $"{media.Width} × {media.Height}" : "—";
    private static string Size(ulong? size) => size is not { } bytes ? "—" : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : bytes < 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{bytes / (1024d * 1024 * 1024):0.#} GB";
}
