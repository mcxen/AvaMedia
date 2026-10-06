using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AvaMedia.Desktop.Controls;

// Cache each skin separately so switching an open window keeps the correct artwork.
internal static class FeatureIconAssets
{
    private static readonly Dictionary<(string Family, string Kind), Bitmap?> Cache = new();
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "video", "formats", "join", "gear", "split", "crop", "rotate", "clip", "erase", "frames",
        "player", "download", "audio", "image", "document", "archive", "disc", "info", "clip-list",
        "pdf-merge", "pdf-split", "pdf-text", "pdf-docx", "pdf-xlsx", "text-pdf", "zip", "unzip"
    };

    public static Bitmap? Get(string kind, bool classic = false)
    {
        if (!Kinds.Contains(kind)) return null;
        var family = classic ? "macos9" : "v2";
        var key = (family, kind);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var bitmap)) return bitmap;
            var uri = new Uri($"avares://AvaMedia.Desktop/Assets/FeatureIcons/{family}/{kind}.png");
            if (!AssetLoader.Exists(uri)) return Cache[key] = classic ? Get(kind) : null;
            using var stream = AssetLoader.Open(uri);
            return Cache[key] = Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
        }
    }
}
