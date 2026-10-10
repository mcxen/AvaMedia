using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Exact video frames and timestamps share the bounded preview store.</summary>
internal sealed class MediaPreviewCache
{
    private static readonly SemaphoreSlim FrameSlots = new(2, 2);
    private static readonly SemaphoreSlim[] SourceSlots = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Dictionary<string, MediaInfo> _media = [];
    private readonly object _mediaLock = new();

    public async Task<string?> IdentityAsync(string path, object options, CancellationToken token)
        => await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists) return null;
                return JsonSerializer.Serialize(new { Kind = "video-preview-1",
                    Path = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName,
                    file.Length, Modified = file.LastWriteTimeUtc.Ticks, Options = options });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }, token).ConfigureAwait(false);

    public async Task<MediaInfo> MediaAsync(string? identity, Func<Task<MediaInfo>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (identity is not null)
            lock (_mediaLock) if (_media.TryGetValue(identity, out var cached)) return cached;
        var media = await read().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (identity is not null && media.RawJson.Length <= 256 * 1024)
            lock (_mediaLock)
            {
                _media[identity] = media;
                while (_media.Count > 8) _media.Remove(_media.Keys.First());
            }
        return media;
    }

    public async Task<byte[]> ThumbnailAsync(string? identity, Func<Task<byte[]>> render, CancellationToken token)
    {
        if (identity is null) return await render().ConfigureAwait(false);
        var slot = SourceSlots[SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))[0] % SourceSlots.Length];
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = await PreviewCacheStore.Shared.ReadAsync(identity, token).ConfigureAwait(false);
            if (cached is not null) return cached;
            await FrameSlots.WaitAsync(token).ConfigureAwait(false);
            byte[] frame;
            try { frame = await render().ConfigureAwait(false); }
            finally { FrameSlots.Release(); }
            await PreviewCacheStore.Shared.WriteAsync(identity, frame, token).ConfigureAwait(false);
            return frame;
        }
        finally { slot.Release(); }
    }

    public async Task<double[]> TimesAsync(string? identity, Func<Task<double[]>> read, CancellationToken token)
    {
        if (identity is not null && await PreviewCacheStore.Shared.ReadAsync(identity, token).ConfigureAwait(false) is { } bytes)
            try
            {
                if (JsonSerializer.Deserialize<double[]>(bytes) is { } times && times.All(double.IsFinite)) return times;
            }
            catch (JsonException) { }
        var result = await read().ConfigureAwait(false);
        if (identity is not null && result.Length <= 16384)
            await PreviewCacheStore.Shared.WriteAsync(identity, JsonSerializer.SerializeToUtf8Bytes(result), token).ConfigureAwait(false);
        return result;
    }
}
