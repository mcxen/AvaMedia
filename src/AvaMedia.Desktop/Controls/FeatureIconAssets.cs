using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AvaMedia.Desktop.Controls;

// Cache one display-sized decode per kind; full-resolution source PNGs stay embedded.
internal static class FeatureIconAssets
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "video", "formats", "join", "gear", "split", "crop", "rotate", "clip", "erase", "frames",
        "record", "player", "download", "audio", "image", "document", "archive", "disc", "info", "clip-list"
    };

    public static Bitmap? Get(string kind)
    {
        if (!Kinds.Contains(kind)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var bitmap)) return bitmap;
            var uri = new Uri($"avares://AvaMedia.Desktop/Assets/FeatureIcons/v2/{kind}.png");
            if (!AssetLoader.Exists(uri)) return Cache[kind] = null;
            using var stream = AssetLoader.Open(uri);
            return Cache[kind] = Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
        }
    }
}
