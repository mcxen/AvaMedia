using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Identity of dragged files: the thumbnail (or a media-type icon when there is none).</summary>
internal sealed record FileDragVisual(IImage? Thumbnail, string Icon);

internal enum FileDragResult { Missing, Cancelled, Dragged }

/// <summary>
/// Copy-only file drags from task rows and task windows (UISPEC 7.4.2). Avalonia 11.3 has no API for a custom
/// platform drag image, so the thumbnail ghost (stacked with a count badge for several files) is drawn in the
/// adorner layer of every AvaMedia window the pointer crosses; outside the app the platform's default drag image shows.
/// </summary>
internal static class FileDrag
{
    private static Session? _session;
    private static readonly Dictionary<TopLevel, Ghost> Ghosts = [];

    /// <summary>Lucide icon for a file by media type; used when no thumbnail can be shown.</summary>
    public static string MediaIcon(string path)
    {
        if (path.Length == 0) return "file";
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return "globe";
        if (Directory.Exists(path)) return "folder";
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (QuickClipBatch.VideoExtensions.Contains(extension)) return "film";
        if (MediaEngine.IsAudio(extension)) return "music";
        if (MediaEngine.IsImage(extension) || extension is "jpeg" or "tif" or "gif" or "heic") return "photo";
        if (extension is "pdf" or "txt" or "md" or "doc" or "docx" or "srt" or "vtt" or "ass" or "json") return "file-text";
        if (extension is "zip" or "7z" or "rar" or "tar" or "gz") return "file-archive";
        return "file";
    }

    /// <summary>Expands folders, then runs one platform drag that only offers <see cref="DragDropEffects.Copy"/>.
    /// <paramref name="beginDrag"/> runs after the files are resolved and can abort when the press already ended.</summary>
    public static async Task<FileDragResult> RunAsync(Visual source, PointerEventArgs e, IEnumerable<string> paths, FileDragVisual visual, Func<bool>? beginDrag = null)
    {
        if (TopLevel.GetTopLevel(source) is not { } top) return FileDragResult.Missing;
        var requested = paths.ToArray();
        List<IStorageFile> files = [];
        try
        {
            foreach (var path in await Task.Run(() => Expand(requested)))
                if (await top.StorageProvider.TryGetFileFromPathAsync(path) is { } file) files.Add(file);
            if (files.Count == 0) return FileDragResult.Missing;
            if (beginDrag is not null && !beginDrag()) return FileDragResult.Cancelled;
            var transfer = new DataTransfer();
            foreach (var file in files) transfer.Add(DataTransferItem.CreateFile(file));
            Begin(visual, files.Count);
            // Dragging never moves or deletes the source; targets receive file references to copy.
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy);
            return FileDragResult.Dragged;
        }
        finally
        {
            End();
            foreach (var file in files) file.Dispose();
        }
    }

    public static string[] Expand(IEnumerable<string> paths)
    {
        List<string> files = [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in paths)
        {
            if (File.Exists(path)) files.Add(Path.GetFullPath(path));
            else if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path, "*", options).Order(VideoFolderScanner.PathComparer));
        }
        return files.Distinct(VideoFolderScanner.PathComparer).ToArray();
    }

    /// <summary>Drop-target hint ("will create …") shown next to the pointer, for in-app and external drags.</summary>
    public static void ShowHint(DragEventArgs e, Visual target, string text, bool invalid)
    {
        if (TopLevel.GetTopLevel(target) is not { } top || GhostFor(top) is not { } ghost) return;
        ghost.SetHint(text, invalid);
        ghost.MoveTo(e.GetPosition(top), top.Bounds.Size, _session?.Visual);
    }

    /// <summary>Design captures: draw the ghost (and hint) at <paramref name="pointer"/> without a platform drag; null clears it.</summary>
    internal static void Preview(TopLevel top, Point pointer, FileDragVisual? visual, int count, string hint = "")
    {
        if (GhostFor(top) is not { } ghost) return;
        if (visual is null && hint.Length == 0) { ghost.SetHint("", false); ghost.Hide(); return; }
        ghost.SetHint(hint, false);
        ghost.MoveTo(pointer, top.Bounds.Size, visual, count);
    }

    public static void ClearHint(Visual target)
    {
        if (TopLevel.GetTopLevel(target) is not { } top || !Ghosts.TryGetValue(top, out var ghost)) return;
        ghost.SetHint("", false);
        if (_session is null) ghost.Hide();
    }

    private static void Begin(FileDragVisual visual, int count)
    {
        End();
        var session = _session = new Session(visual, count);
        IEnumerable<TopLevel> tops = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop ? desktop.Windows : [];
        foreach (var top in tops) session.Attach(top);
    }

    private static void End()
    {
        var session = _session; _session = null;
        session?.Detach();
        foreach (var ghost in Ghosts.Values) ghost.Remove();
        Ghosts.Clear();
    }

    private static Ghost? GhostFor(TopLevel top)
    {
        if (Ghosts.TryGetValue(top, out var ghost)) return ghost;
        var anchor = top is ContentControl { Presenter: { } presenter } ? presenter : top.Content as Visual;
        if (anchor is null || AdornerLayer.GetAdornerLayer(anchor) is not { } layer) return null;
        ghost = new Ghost(layer);
        Ghosts[top] = ghost;
        top.Closed += (_, _) => { if (Ghosts.Remove(top, out var closed)) closed.Remove(); };
        return ghost;
    }

    private sealed class Session(FileDragVisual visual, int count)
    {
        private readonly List<(TopLevel Top, bool EnabledDrop)> _tops = [];
        public FileDragVisual Visual { get; } = visual;
        public int Count { get; } = count;

        public void Attach(TopLevel top)
        {
            // Windows without their own drop handling still report the pointer so the ghost follows it there.
            var enabled = !DragDrop.GetAllowDrop(top);
            if (enabled) DragDrop.SetAllowDrop(top, true);
            top.AddHandler(DragDrop.DragEnterEvent, Over, RoutingStrategies.Bubble, handledEventsToo: true);
            top.AddHandler(DragDrop.DragOverEvent, Over, RoutingStrategies.Bubble, handledEventsToo: true);
            top.AddHandler(DragDrop.DragLeaveEvent, Leave, RoutingStrategies.Bubble, handledEventsToo: true);
            top.AddHandler(DragDrop.DropEvent, Dropped, RoutingStrategies.Bubble, handledEventsToo: true);
            _tops.Add((top, enabled));
        }

        public void Detach()
        {
            foreach (var (top, enabled) in _tops)
            {
                top.RemoveHandler(DragDrop.DragEnterEvent, Over); top.RemoveHandler(DragDrop.DragOverEvent, Over);
                top.RemoveHandler(DragDrop.DragLeaveEvent, Leave); top.RemoveHandler(DragDrop.DropEvent, Dropped);
                if (enabled) DragDrop.SetAllowDrop(top, false);
            }
            _tops.Clear();
        }

        private bool Enabled(object? sender) => _tops.Any(item => ReferenceEquals(item.Top, sender) && item.EnabledDrop);

        private void Over(object? sender, DragEventArgs e)
        {
            if (sender is not TopLevel top) return;
            if (Enabled(top) && ReferenceEquals(e.Source, top)) e.DragEffects = DragDropEffects.None;
            foreach (var (other, ghost) in Ghosts) if (!ReferenceEquals(other, top)) ghost.Hide();
            GhostFor(top)?.MoveTo(e.GetPosition(top), top.Bounds.Size, Visual, Count);
        }

        private void Leave(object? sender, DragEventArgs e)
        { if (sender is TopLevel top && Ghosts.TryGetValue(top, out var ghost)) ghost.Hide(); }

        private void Dropped(object? sender, DragEventArgs e)
        {
            if (sender is not TopLevel top) return;
            if (Enabled(top) && ReferenceEquals(e.Source, top)) e.DragEffects = DragDropEffects.None;
            if (Ghosts.TryGetValue(top, out var ghost)) ghost.Hide();
        }
    }

    /// <summary>Thumbnail stack with count badge and an optional drop hint, drawn above a window's content.</summary>
    private sealed class Ghost
    {
        private const double ThumbWidth = 64, ThumbHeight = 46, Stack = 5;
        private readonly AdornerLayer _layer;
        private readonly Canvas _canvas = new() { IsHitTestVisible = false };
        private readonly StackPanel _root = new() { Spacing = 6, IsHitTestVisible = false };
        private readonly Grid _stack = new() { Width = ThumbWidth + Stack * 2, Height = ThumbHeight + Stack * 2, HorizontalAlignment = HorizontalAlignment.Left, Opacity = .94 };
        private readonly Border _back1 = Card(), _back2 = Card(), _front = Card();
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        private readonly Image _image1 = new() { Stretch = Stretch.UniformToFill }, _image2 = new() { Stretch = Stretch.UniformToFill };
        private readonly LucideIcon _icon = new() { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _badge = new() { MinWidth = 18, Height = 18, CornerRadius = new(9), Padding = new(5, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _count = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _hint = new() { CornerRadius = new(6), Padding = new(8, 4), BorderThickness = new(0.5), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 320 };
        private readonly TextBlock _hintText = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

        public Ghost(AdornerLayer layer)
        {
            _layer = layer;
            _back2.Margin = new(Stack * 2, 0, 0, Stack * 2); _back1.Margin = new(Stack, Stack, Stack, Stack); _front.Margin = new(0, Stack * 2, Stack * 2, 0);
            // Several files: two offset copies of the thumbnail behind the front card.
            _back1.Opacity = .8; _back2.Opacity = .55;
            _back1.Child = _image1; _back2.Child = _image2;
            _icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
            _front.Child = new Panel { Children = { _icon, _image } };
            _badge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiAccent"));
            _count.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiAccentText"));
            _count.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
            _badge.Child = _count;
            _stack.Children.Add(_back2); _stack.Children.Add(_back1); _stack.Children.Add(_front); _stack.Children.Add(_badge);
            _hint.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
            _hint.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider"));
            _hint.Bind(Border.BoxShadowProperty, new DynamicResourceExtension("UiCardShadow"));
            _hint.Child = _hintText; _hint.IsVisible = false;
            _root.Children.Add(_stack); _root.Children.Add(_hint);
            _canvas.Children.Add(_root); _canvas.IsVisible = false;
            _layer.Children.Add(_canvas);
        }

        private static Border Card()
        {
            var card = new Border { CornerRadius = new(6), BorderThickness = new(0.5), ClipToBounds = true, Width = ThumbWidth, Height = ThumbHeight };
            card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
            card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider"));
            card.Bind(Border.BoxShadowProperty, new DynamicResourceExtension("UiCardShadow"));
            return card;
        }

        public void SetHint(string text, bool invalid)
        {
            _hintText.Text = text; _hint.IsVisible = text.Length > 0;
            _hintText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(invalid ? "UiDanger" : "UiText"));
        }

        public void MoveTo(Point pointer, Size bounds, FileDragVisual? visual, int count = 0)
        {
            _stack.IsVisible = visual is not null;
            if (visual is not null)
            {
                _image.Source = _image1.Source = _image2.Source = visual.Thumbnail; _image.IsVisible = visual.Thumbnail is not null;
                _icon.Kind = visual.Icon; _icon.IsVisible = visual.Thumbnail is null;
                if (count > 0)
                {
                    var many = count > 1;
                    _back1.IsVisible = _back2.IsVisible = _badge.IsVisible = many;
                    _count.Text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            if (!_stack.IsVisible && !_hint.IsVisible) { Hide(); return; }
            _canvas.IsVisible = true;
            _root.Measure(Size.Infinity);
            var size = _root.DesiredSize;
            var x = Math.Min(pointer.X + 14, Math.Max(0, bounds.Width - size.Width - 4));
            var y = Math.Min(pointer.Y + 10, Math.Max(0, bounds.Height - size.Height - 4));
            Canvas.SetLeft(_root, x); Canvas.SetTop(_root, y);
        }

        public void Hide() => _canvas.IsVisible = false;
        public void Remove() { _canvas.IsVisible = false; _layer.Children.Remove(_canvas); }
    }
}
