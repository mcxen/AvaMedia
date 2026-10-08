namespace AvaMedia.Core;

public sealed partial class Storage
{
    private static readonly object PlayerViewWrite = new();

    public PanoramaSettings LoadPlayerView(string path)
    {
        lock (PlayerViewWrite)
        {
            var views = Read<Dictionary<string, PanoramaSettings>>("player-views.json");
            var key = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return views?.FirstOrDefault(pair => string.Equals(pair.Key, key, comparison)).Value?.Normalize() ?? new();
        }
    }

    public void SavePlayerView(string path, PanoramaSettings settings)
    {
        lock (PlayerViewWrite)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var views = new Dictionary<string, PanoramaSettings>(Read<Dictionary<string, PanoramaSettings>>("player-views.json") ?? [], comparer);
            var key = Path.GetFullPath(path);
            views.Remove(key); // Keep the most recently remembered entries at the end.
            views.Add(key, settings.Normalize());
            while (views.Count > 512) views.Remove(views.Keys.First());
            Write("player-views.json", views);
        }
    }
}
