using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

/// <summary>Shared bounded storage for derived images and queue summaries.</summary>
internal sealed class PreviewCacheStore(string? root = null)
{
    private const int MaximumEntryBytes = 8 * 1024 * 1024;
    private readonly string _root = root ?? Path.Combine(Storage.DefaultRoot, "preview-cache");
    private readonly object _memoryLock = new();
    private readonly Dictionary<string, byte[]> _memory = [];
    private readonly Queue<string> _memoryOrder = [];
    private long _memoryBytes;
    private long _lastPrune;
    public static PreviewCacheStore Shared { get; } = new();
    private string FileName(string identity) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant() + ".cache");

    public async Task<byte[]?> ReadAsync(string identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_memoryLock) if (_memory.TryGetValue(identity, out var cached)) return cached;
        try
        {
            var path = FileName(identity); var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 32 or > MaximumEntryBytes + 32) return null;
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (bytes.Length <= 32 || bytes.Length > MaximumEntryBytes + 32
                || !SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0, 32))) return null;
            var payload = bytes[32..]; Remember(identity, payload); return payload;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task WriteAsync(string identity, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is 0 or > MaximumEntryBytes) return;
        token.ThrowIfCancellationRequested(); Remember(identity, bytes);
        var path = FileName(identity); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(SHA256.HashData(bytes), token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
            Prune();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Remember(string identity, byte[] bytes)
    {
        lock (_memoryLock)
        {
            if (_memory.Remove(identity, out var old)) _memoryBytes -= old.Length;
            else _memoryOrder.Enqueue(identity);
            _memory[identity] = bytes; _memoryBytes += bytes.Length;
            while (_memory.Count > 256 || _memoryBytes > 16 * 1024 * 1024)
                if (_memory.Remove(_memoryOrder.Dequeue(), out var removed)) _memoryBytes -= removed.Length;
        }
    }

    private void Prune()
    {
        var now = Environment.TickCount64; var previous = Interlocked.Read(ref _lastPrune);
        if ((previous != 0 && now - previous < 300_000) || Interlocked.CompareExchange(ref _lastPrune, now, previous) != previous) return;
        long bytes = 0; var count = 0;
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*.cache").OrderByDescending(file => file.LastWriteTimeUtc))
        {
            bytes += file.Length;
            if (++count <= 2048 && bytes <= 128L * 1024 * 1024) continue;
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
