using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record SummaryArtifact(string InputHash, string ContentHash, string State);
public sealed class SummaryCheckpointStore(string root)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Root { get; } = Path.GetFullPath(root);
    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LongSummaryJson.Write(value)))).ToLowerInvariant();
    public string PathFor(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("检查点路径无效。");
        return path;
    }
    private async Task<Dictionary<string, SummaryArtifact>> ManifestAsync(CancellationToken ct)
    {
        var path = PathFor("manifest.json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, SummaryArtifact>>(await File.ReadAllTextAsync(path, ct), LongSummaryJson.Options) ?? []; }
        catch (JsonException) { return []; }
    }
    public async Task<T?> ReadAsync<T>(string path, string inputHash, CancellationToken ct = default) where T : class
    {
        await _gate.WaitAsync(ct);
        try
        {
            var manifest = await ManifestAsync(ct);
            if (!manifest.TryGetValue(path, out var entry) || entry.InputHash != inputHash || entry.State != "done" || !File.Exists(PathFor(path))) return null;
            var bytes = await File.ReadAllBytesAsync(PathFor(path), ct);
            if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != entry.ContentHash) return null;
            try { return JsonSerializer.Deserialize<T>(bytes, LongSummaryJson.Options); } catch (JsonException) { return null; }
        }
        finally { _gate.Release(); }
    }
    public async Task WriteAsync<T>(string path, string inputHash, T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, LongSummaryJson.Options);
            await AtomicWriteAsync(PathFor(path), bytes, ct);
            var manifest = await ManifestAsync(ct);
            manifest[path] = new(inputHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "done");
            await AtomicWriteAsync(PathFor("manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, LongSummaryJson.Options), ct);
        }
        finally { _gate.Release(); }
    }
    public static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<T> GetOrCreateAsync<T>(string path, string hash, Func<CancellationToken, Task<T>> create, CancellationToken ct) where T : class
    {
        if (await ReadAsync<T>(path, hash, ct) is { } cached) return cached;
        var value = await create(ct); await WriteAsync(path, hash, value, ct); return value;
    }
}
public sealed record SpeechChunk(SubtitleCue[] Cues, VoiceInterval[] VoiceIntervals, string Language);
