using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace AvaMedia.Desktop.Controls;

/// <summary>Owns decoded stills; streaming frames remain owned by their playback session.</summary>
internal sealed class MediaFrameView(Image image) : IDisposable
{
    private Bitmap? _still;

    public void Show(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var frame = new Bitmap(stream);
        Show(frame);
        _still = frame;
    }

    public void Show(Bitmap frame)
    {
        if (ReferenceEquals(image.Source, frame)) { image.InvalidateVisual(); return; }
        var old = _still; _still = null;
        image.Source = frame; old?.Dispose(); image.InvalidateVisual();
    }

    public void Clear()
    {
        image.Source = null; _still?.Dispose(); _still = null;
    }

    public void Dispose() => Clear();
}
