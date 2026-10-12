using System.Collections.Concurrent;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed class ResponseCache
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly long _capacity;
    public ResponseCache(string? root = null, long capacity = 512L * 1024 * 1024)
    { _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaMedia", "ai-cache"); _capacity = capacity; }
    public static string Key(string modelId, SegmentJob job, TokenBudget budget) => SummaryCheckpointStore.Hash(new { modelId, promptVersion = 1, schemaVersion = 2, job, budget });
    private string PathFor(string key)
    {
        if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("缓存键无效。");
        return Path.Combine(_root, key[..2], key + ".json");
    }
    public async Task<SegmentResult?> ReadAsync(string key, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(_root, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            var path = PathFor(key); if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 1024 * 1024) return null;
            try
            {
                var value = JsonSerializer.Deserialize<SegmentResult>(await File.ReadAllTextAsync(path, ct), LongSummaryJson.Options);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return value;
            }
            catch (JsonException) { return null; }
        }
        finally { gate.Release(); }
    }
    public async Task WriteAsync(string key, SegmentResult value, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(_root, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            await SummaryCheckpointStore.AtomicWriteAsync(PathFor(key), JsonSerializer.SerializeToUtf8Bytes(value, LongSummaryJson.Options), ct);
            var files = Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories).Select(p => new FileInfo(p)).OrderBy(f => f.LastWriteTimeUtc).ToArray();
            long total = files.Sum(f => f.Length);
            foreach (var file in files) { if (total <= _capacity) break; total -= file.Length; file.Delete(); }
        }
        finally { gate.Release(); }
    }
}
