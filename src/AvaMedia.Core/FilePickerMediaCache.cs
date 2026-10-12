using System.Text.Json;

namespace AvaMedia.Core;

public sealed record FilePickerMedia(double Duration = 0, int Width = 0, int Height = 0,
    double FrameRate = 0, string VideoCodec = "", string AudioCodec = "", byte[]? Thumbnail = null);

/// <summary>Picker metadata and small covers share the existing bounded memory/disk preview store.</summary>
public sealed class FilePickerMediaCache
{
    private readonly PreviewCacheStore _store;
    private readonly Func<string, CancellationToken, Task<FilePickerMedia>> _read;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim[] _keys = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    public FilePickerMediaCache(Func<string, CancellationToken, Task<FilePickerMedia>> read,
        string? cacheFolder = null, int maxConcurrency = 2)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _read = read; _store = cacheFolder is null ? PreviewCacheStore.Shared : new(cacheFolder);
        _slots = new(maxConcurrency);
    }

    public static FilePickerMediaCache Create(IMediaEngine engine, MediaThumbnailCache thumbnails)
        => new(async (path, token) =>
        {
            using var priority = ProcessRunner.BackgroundWork();
            MediaInfo? media = null; byte[]? cover = null;
            try { media = await engine.Probe(path, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException) { }
            try
            {
                var thumbnail = await thumbnails.GetAsync(path, 480, ct: token).ConfigureAwait(false);
                cover = await File.ReadAllBytesAsync(thumbnail, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new(media?.Duration ?? 0, media?.Width ?? 0, media?.Height ?? 0,
                media?.FrameRate ?? 0, media?.VideoCodec ?? "", media?.AudioCodec ?? "", cover);
        });

    public async Task<FilePickerMedia> GetAsync(string path, ulong? size, DateTimeOffset? modified, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) fullPath = fullPath.ToUpperInvariant();
        var identity = $"picker-media-v1|{fullPath.Length}:{fullPath}|{size}|{modified?.UtcTicks}";
        var key = _keys[(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))[0]) % _keys.Length];
        await key.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (await _store.ReadAsync(identity, token).ConfigureAwait(false) is { } cached)
                try { if (JsonSerializer.Deserialize<FilePickerMedia>(cached) is { } result) return result; }
                catch (JsonException) { }
            await _slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                var result = await _read(path, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await _store.WriteAsync(identity, JsonSerializer.SerializeToUtf8Bytes(result), token).ConfigureAwait(false);
                return result;
            }
            finally { _slots.Release(); }
        }
        finally { key.Release(); }
    }
}
