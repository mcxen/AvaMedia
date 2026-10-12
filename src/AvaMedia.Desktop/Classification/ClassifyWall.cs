using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Desktop.Classification.ClassifyUi;

namespace AvaMedia.Desktop.Classification;

/// <summary>Cover mosaic like a macOS folder cover: one large tile left and two stacked right, or a fanned stack.</summary>
internal sealed class Mosaic : Grid
{
    private readonly ClassifyThumb[] _thumbs;
    public Mosaic(double width, double height, IReadOnlyList<string> paths, bool stacked = false)
    {
        Width = width; Height = height;
        if (stacked)
        {
            _thumbs = Enumerable.Range(0, 3).Select(i => new ClassifyThumb(48)
            {
                Width = height * .85, Height = height * .85, Margin = new(i * 6, 3 - i * 1.5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, BorderBrush = Brushes.White, BorderThickness = new(1.5), CornerRadius = new(5)
            }).ToArray();
            foreach (var thumb in _thumbs) Children.Add(thumb);
        }
        else
        {
            ColumnDefinitions = new("2*,2,*"); RowDefinitions = new("*,2,*");
            _thumbs = [new ClassifyThumb((int)width) { CornerRadius = new(6, 0, 0, 6) }, new ClassifyThumb(48) { CornerRadius = new(0, 6, 0, 0) }, new ClassifyThumb(48) { CornerRadius = new(0, 0, 6, 0) }];
            Grid.SetRowSpan(_thumbs[0], 3); Grid.SetColumn(_thumbs[1], 2); Grid.SetColumn(_thumbs[2], 2); Grid.SetRow(_thumbs[2], 2);
            foreach (var thumb in _thumbs) Children.Add(thumb);
        }
        SetPaths(paths);
    }
    public void SetPaths(IReadOnlyList<string> paths)
    {
        for (var i = 0; i < _thumbs.Length; i++)
        {
            var path = paths.Count == 0 ? null : paths[Math.Min(i, paths.Count - 1)];
            _thumbs[i].SetSource(path, path is not null && VideoFormats.IsVideo(path) ? 1 : null);
            _thumbs[i].IsVisible = path is not null || i == 0;
        }
    }
}

/// <summary>Virtualized file flow wall. Only realized rows hold tiles; tiles request thumbnails while visible.</summary>
internal sealed class FileWall : ListBox
{
    public const double Tile = 44, Gap = 6;
    private IReadOnlyList<FolderFileVisual> _files = [];
    private int _columns = 1;
    private readonly HashSet<WallRow> _realized = [];
    protected override Type StyleKeyOverride => typeof(ListBox);

    public FileWall()
    {
        Background = Brushes.Transparent; BorderThickness = new(0); Padding = new(0);
        SelectionMode = SelectionMode.Single; Focusable = false;
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        ItemTemplate = new FuncDataTemplate<int>((_, _) => new WallRow(this), supportsRecycling: true);
        ItemContainerTheme = null;
        ContainerPrepared += (_, args) => { if (args.Container is ListBoxItem item) { item.Padding = new(0); item.MinHeight = 0; item.Background = Brushes.Transparent; } };
        SizeChanged += (_, _) => Relayout();
    }

    public void Update(IReadOnlyList<FolderFileVisual> files)
    {
        var countChanged = files.Count != _files.Count; _files = files;
        if (countChanged) Relayout();
        foreach (var row in _realized) row.Refresh();
    }

    private void Relayout()
    {
        var columns = Math.Max(1, (int)((Bounds.Width - 4 + Gap) / (Tile + Gap)));
        var rows = (_files.Count + columns - 1) / columns;
        if (columns == _columns && ItemCount == rows) return;
        _columns = columns; ItemsSource = Enumerable.Range(0, rows).ToArray();
    }

    private sealed class WallRow : StackPanel
    {
        private readonly FileWall _owner;
        private readonly List<WallTile> _tiles = [];
        public WallRow(FileWall owner) { _owner = owner; Orientation = Orientation.Horizontal; Spacing = Gap; Margin = new(0, 0, 0, Gap); }
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _owner._realized.Add(this); Refresh(); }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _owner._realized.Remove(this); base.OnDetachedFromVisualTree(e); }
        protected override void OnDataContextChanged(EventArgs e) { base.OnDataContextChanged(e); Refresh(); }
        public void Refresh()
        {
            if (DataContext is not int row) return;
            var start = row * _owner._columns; var count = Math.Clamp(_owner._files.Count - start, 0, _owner._columns);
            while (_tiles.Count < count) { var tile = new WallTile(); _tiles.Add(tile); Children.Add(tile); }
            for (var i = 0; i < _tiles.Count; i++)
            {
                _tiles[i].IsVisible = i < count;
                if (i < count) _tiles[i].Set(_owner._files[start + i]);
            }
        }
    }

    internal sealed class WallTile : Panel
    {
        private readonly ClassifyThumb _thumb = new(48) { Width = Tile, Height = Tile };
        private readonly Border _ring = new() { CornerRadius = new(8), BorderBrush = Blue, BorderThickness = new(2), Margin = new(-2) };
        private readonly Border _badge = new() { Width = 16, Height = 16, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 3), BorderBrush = Brushes.White, BorderThickness = new(1.5) };
        private string? _key;
        public WallTile()
        {
            Width = Tile; Height = Tile; Children.Add(_thumb); Children.Add(_ring); Children.Add(_badge);
            ToolTip.SetShowDelay(this, 400);
        }
        public void Set(FolderFileVisual file)
        {
            var key = file.Path + file.State;
            if (key == _key) return; _key = key;
            _thumb.SetSource(file.Path, file.IsVideo ? 1 : null);
            ToolTip.SetTip(this, file.Name);
            _thumb.Opacity = file.State == FolderFileState.Queued ? .35 : 1;
            _ring.IsVisible = file.State == FolderFileState.Processing;
            (IBrush? fill, string? icon, string? text) badge = file.State switch
            {
                FolderFileState.Done => (Green, "check", null),
                FolderFileState.Pending => (Orange, null, "?"),
                FolderFileState.Failed => (Red, null, "!"),
                FolderFileState.Processing => (Brushes.White, "loader-circle", null),
                _ => (null, null, null)
            };
            _badge.IsVisible = badge.fill is not null; _badge.Background = badge.fill;
            _badge.Child = badge.icon is { } icon ? new LucideIcon { Kind = icon, Width = 10, Height = 10, Foreground = file.State == FolderFileState.Processing ? Blue : Brushes.White }
                : new TextBlock { Text = badge.text, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
    }
}

/// <summary>Eight files around the current source, in input order, without realizing the full queue.</summary>
internal sealed class FileFlow : Grid
{
    private readonly FileWall.WallTile[] _tiles = new FileWall.WallTile[8];
    public FileFlow()
    {
        Height = FileWall.Tile; VerticalAlignment = VerticalAlignment.Top; ColumnSpacing = FileWall.Gap;
        for (var i = 0; i < _tiles.Length; i++)
        {
            ColumnDefinitions.Add(new(1, GridUnitType.Star));
            _tiles[i] = new() { HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(_tiles[i], i); Children.Add(_tiles[i]);
        }
    }
    public void Update(IReadOnlyList<FolderFileVisual> files)
    {
        var current = -1;
        for (var i = 0; i < files.Count; i++) if (files[i].State == FolderFileState.Processing) { current = i; break; }
        if (current < 0)
            for (var i = 0; i < files.Count; i++) if (files[i].State == FolderFileState.Queued) { current = i; break; }
        if (current < 0) current = files.Count - 1;
        var start = Math.Clamp(current - 5, 0, Math.Max(0, files.Count - _tiles.Length));
        for (var i = 0; i < _tiles.Length; i++)
        {
            _tiles[i].IsVisible = start + i < files.Count;
            if (_tiles[i].IsVisible) _tiles[i].Set(files[start + i]);
        }
    }
}
