using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AvaMedia.Browser;

/// <summary>Shared server preview frames for file rows and task details.</summary>
internal sealed class MediaThumbnail : Border
{
    private static readonly Dictionary<string, Task<Bitmap?>> Frames = new(StringComparer.Ordinal);
    private string? _path;
    public string? Path
    {
        get => _path;
        set { if (_path == value) return; _path = value; _ = Load(value); }
    }

    public MediaThumbnail(double width, double height)
    {
        Width = width; Height = height; CornerRadius = new(7); ClipToBounds = true;
        Background = Palette.Fill; VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Child = Ui.Icon("video", 22, Palette.Secondary);
    }

    private async Task Load(string? path)
    {
        Child = Ui.Icon(Ui.FileIcon(path ?? "", false), 22, Palette.Secondary);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Frames.TryGetValue(path, out var frame)) Frames[path] = frame = Read(path);
        var bitmap = await frame;
        if (_path == path && bitmap is not null) Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
    }

    private static async Task<Bitmap?> Read(string path)
    {
        try
        {
            var (bytes, _) = await RemotePreview.Read(path, 1, 160, 0, 0, CancellationToken.None);
            using var stream = new MemoryStream(bytes); return new Bitmap(stream);
        }
        catch (Exception) { return null; }
    }
}
