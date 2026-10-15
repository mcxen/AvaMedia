using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>File thumbnail (or media-type icon) with the hover drag grip at its bottom-right (UISPEC 7.4.2).</summary>
internal sealed class MsFileThumb : Border, IDisposable
{
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, IsVisible = false };
    private readonly LucideIcon _icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly IMediaEngine? _engine;
    private CancellationTokenSource? _load;
    private Bitmap? _bitmap;
    private string _path = "";

    public MsFileThumb(double width, double height, IMediaEngine? engine)
    {
        _engine = engine;
        Classes.Add("ms-file-thumb");
        Width = width; Height = height; ClipToBounds = true; CornerRadius = new(width >= 36 ? 6 : 4);
        Bind(BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
        _icon.Width = _icon.Height = Math.Round(Math.Min(width, height) * .45);
        _icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        var grip = new Border { Classes = { "ms-grip" }, CornerRadius = new(3), Padding = new(1, 2), Margin = new(0, 0, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        grip.Bind(BackgroundProperty, new DynamicResourceExtension("UiMediaOverlay"));
        var glyph = new LucideIcon { Kind = "grip-vertical", Width = 9, Height = 11 };
        glyph.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiMediaText"));
        grip.Child = glyph;
        Child = new Panel { Children = { _icon, _image, grip } };
        DetachedFromVisualTree += (_, _) => Dispose();
        AttachedToVisualTree += (_, _) => { if (_path.Length > 0 && _bitmap is null && _load is null) Load(_path); };
    }

    public IImage? Thumbnail => _bitmap;
    public string Icon => _icon.Kind;

    public void SetPath(string path)
    {
        if (_path == path) return;
        _path = path;
        _icon.Kind = FileDrag.MediaIcon(path);
        Release();
        if (path.Length > 0 && VisualRoot is not null) Load(path);
    }

    private async void Load(string path)
    {
        var load = _load = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var width = (int)Math.Ceiling(Width * 2);
        try
        {
            var bitmap = await Task.Run(async () =>
            {
                if (!File.Exists(path)) return null;
                var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (MediaEngine.IsImage(extension) && extension is not ("heic" or "heif" or "avif"))
                {
                    await using var file = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(file, width);
                }
                if (_engine is null || !(QuickClipBatch.VideoExtensions.Contains(extension) || MediaEngine.IsAudio(extension))) return null;
                var preview = await TaskPreviewCache.Shared.GetAsync(_engine, path, 0, 0, 0, 0, load.Token);
                if (preview.Cover is not { Length: > 0 } bytes) return null;
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, width);
            }, load.Token);
            if (!ReferenceEquals(load, _load) || _path != path) { bitmap?.Dispose(); return; }
            _bitmap = bitmap; _image.Source = bitmap; _image.IsVisible = bitmap is not null; _icon.IsVisible = bitmap is null;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Unreadable media keeps the media-type icon.
        }
        finally { if (ReferenceEquals(load, _load)) _load = null; load.Dispose(); }
    }

    private void Release()
    {
        _load?.Cancel(); _load = null;
        _image.Source = null; _image.IsVisible = false; _icon.IsVisible = true;
        _bitmap?.Dispose(); _bitmap = null;
    }

    public void Dispose() => Release();
}

/// <summary>Copy-only drag of the files a control represents; any press-and-move past the threshold starts it.</summary>
internal static class MsFileDrag
{
    public static void Attach(Control source, Func<IReadOnlyList<string>> paths, Func<FileDragVisual> visual)
    {
        Point? pressed = null; IPointer? pointer = null; var dragging = false;
        void Reset() { pressed = null; var held = pointer; pointer = null; if (held?.Captured == source) held.Capture(null); }
        source.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (dragging || paths().Count == 0 || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
            pressed = e.GetPosition(source); pointer = e.Pointer; e.Pointer.Capture(source); e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (pressed is not { } start || dragging) return;
            if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { Reset(); return; }
            var delta = e.GetPosition(source) - start;
            if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
            dragging = true; pressed = null; e.Handled = true;
            try
            {
                var result = await FileDrag.RunAsync(source, e, paths(), visual(), () => { var held = pointer is not null; Reset(); return held; });
                if (result == FileDragResult.Missing && TopLevel.GetTopLevel(source) is Window window)
                    await Ui.Message(window, "拖拽文件", "文件缺失或不可访问。");
            }
            catch (Exception error)
            {
                AppDiagnostics.Record("Drag task window files", error);
                if (TopLevel.GetTopLevel(source) is Window { IsVisible: true } window) await Ui.Message(window, "拖拽文件失败", error.Message);
            }
            finally { Reset(); dragging = false; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Reset(), RoutingStrategies.Bubble, handledEventsToo: true);
        source.PointerCaptureLost += (_, _) => Reset();
    }
}

/// <summary>Draggable output file chip: thumbnail + file name + size. Only shown for finished outputs.</summary>
internal sealed class MsFileChip : Border
{
    private readonly MsFileThumb _thumb;
    private readonly TextBlock _name = new() { Classes = { "ms-body" }, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, MaxWidth = 240 };
    private readonly TextBlock _size = new() { Classes = { "ms-small" }, MaxLines = 1 };
    private string _path = "";

    public MsFileChip(IMediaEngine? engine)
    {
        Classes.Add("ms-file-chip"); Classes.Add("ms-file-drag");
        _thumb = new MsFileThumb(28, 28, engine);
        Localization.SetIsUserText(_name, true);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            _thumb, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _size } }
        } };
        Cursor = new Cursor(StandardCursorType.Hand);
        MsFileDrag.Attach(this, () => _path.Length > 0 ? [_path] : [], () => new FileDragVisual(_thumb.Thumbnail, _thumb.Icon));
    }

    public string Path => _path;

    public void SetFile(string path)
    {
        if (_path == path) return;
        _path = path;
        _thumb.SetPath(path);
        _name.Text = System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        var size = FileSize(path);
        _size.Text = size; _size.IsVisible = size.Length > 0;
        var tip = Localization.Format($"拖拽到工具或其他应用以复制 · {path}");
        ToolTip.SetTip(this, tip);
        Avalonia.Automation.AutomationProperties.SetName(this, _name.Text);
    }

    internal static string FileSize(string path)
    {
        try
        {
            if (File.Exists(path)) return MsUi.Size(new FileInfo(path).Length);
            if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(10001).ToArray();
                return files.Length > 10000 ? Localization.Text("超过 10000 个文件") : Localization.Format($"{files.Length} 个文件");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return "";
    }
}
