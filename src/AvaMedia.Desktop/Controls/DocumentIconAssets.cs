using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AvaMedia.Desktop.Controls;

// Document actions have dedicated artwork, including an independent Platinum family.
internal static class DocumentIconAssets
{
    private static readonly Dictionary<string, Bitmap?> ClassicCache = new(StringComparer.Ordinal);
    public static bool IsSupported(string kind) => kind is
        "pdf-merge" or "pdf-split" or "pdf-text" or "pdf-docx" or "pdf-xlsx" or "text-pdf" or "zip" or "unzip";

    public static Bitmap? Get(string kind, bool classic)
    {
        if (!IsSupported(kind)) return null;
        if (!classic) return FeatureIconAssets.Get(kind);
        lock (ClassicCache)
        {
            if (ClassicCache.TryGetValue(kind, out var image)) return image;
            var uri = new Uri($"avares://AvaMedia.Desktop/Assets/FeatureIcons/macos9/{kind}.png");
            if (!AssetLoader.Exists(uri)) return ClassicCache[kind] = FeatureIconAssets.Get(kind);
            using var stream = AssetLoader.Open(uri);
            return ClassicCache[kind] = Bitmap.DecodeToWidth(stream, 256, BitmapInterpolationMode.HighQuality);
        }
    }
}
