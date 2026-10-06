using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AvaMedia.Desktop.Controls;

// Share decoded pixels only while controls or windows actually use them.
internal static class FeatureIconAssets
{
    private static readonly Dictionary<(string Family, string Kind, int Width), Entry> Cache = new();
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "video", "formats", "join", "gear", "split", "crop", "rotate", "clip", "erase", "frames",
        "player", "download", "audio", "image", "document", "archive", "disc", "info", "clip-list",
        "pdf-merge", "pdf-split", "pdf-text", "pdf-docx", "pdf-xlsx", "text-pdf", "zip", "unzip"
    };

    public static int PixelWidth(double pixels) => pixels <= 32 ? 32 : pixels <= 64 ? 64 : pixels <= 128 ? 128 : 256;

    public static Lease? Acquire(string kind, bool classic, int width)
    {
        if (!Kinds.Contains(kind)) return null;
        var family = classic ? "macos9" : "v2";
        var key = (family, kind, width);
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var entry))
            {
                var uri = new Uri($"avares://AvaMedia.Desktop/Assets/FeatureIcons/{family}/{kind}.png");
                if (!AssetLoader.Exists(uri)) return classic ? Acquire(kind, false, width) : null;
                using var stream = AssetLoader.Open(uri);
                entry = new(key, Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality));
                Cache.Add(key, entry);
            }
            entry.Users++;
            return new(entry);
        }
    }

    internal sealed class Entry((string Family, string Kind, int Width) key, Bitmap bitmap)
    {
        public (string Family, string Kind, int Width) Key { get; } = key;
        public Bitmap Bitmap { get; } = bitmap;
        public int Users;
    }

    internal sealed class Lease(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;
        public Bitmap Bitmap => _entry!.Bitmap;
        ~Lease() => Release();
        public void Dispose() { Release(); GC.SuppressFinalize(this); }
        private void Release()
        {
            var owned = Interlocked.Exchange(ref _entry, null);
            if (owned is null) return;
            lock (Cache)
                if (--owned.Users == 0) { Cache.Remove(owned.Key); owned.Bitmap.Dispose(); }
        }
    }
}
