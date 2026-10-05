using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AvaMedia.Desktop.Controls;

internal static class ApplicationArtwork
{
    private static readonly Lazy<Bitmap> Artwork = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://AvaMedia.Desktop/Assets/AppIcon/v2/app.png"));
        return Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
    });

    public static Bitmap Image => Artwork.Value;
}
