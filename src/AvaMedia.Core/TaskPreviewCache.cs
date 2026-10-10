using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record TaskMediaPreview(MediaInfo Media, byte[]? Cover);

/// <summary>Persistent, compressed queue artwork and summaries, shared by tasks using the same source.</summary>
public sealed class TaskPreviewCache
{
    private const int Width = 228, Height = 144;
    private readonly PreviewCacheStore _store;
    private readonly SemaphoreSlim _generationSlots = new(2, 2);
    private readonly SemaphoreSlim[] _sourceSlots = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public static TaskPreviewCache Shared { get; } = new();

    public TaskPreviewCache(string? root = null) => _store = root is null ? PreviewCacheStore.Shared : new(root);

    public async Task<TaskMediaPreview> GetAsync(IMediaEngine engine, string path, int video, int audio,
        double start, double end, CancellationToken token)
    {
        if (video < 0 || audio < 0 || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < 0)
            throw new ArgumentException("任务预览参数无效。");
        // File metadata can live on a network volume; never inspect it on the UI thread.
        var source = await Task.Run(() => Source.Read(path), token).ConfigureAwait(false);
        var identity = JsonSerializer.Serialize(new { Kind = "task-preview-1", Source = source.Identity, source.Length, source.Modified,
            Video = video, Audio = audio, Start = start, End = end, Width, Height,
            FFmpeg = engine.Settings.FFmpegPath, FFprobe = engine.Settings.FFprobePath });
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        var cached = await ReadAsync(identity, token).ConfigureAwait(false);
        if (cached is not null && await IsCurrentAsync(source, token).ConfigureAwait(false)) return cached;

        // Coalesce concurrent requests without keeping an unbounded dictionary of media paths.
        var slot = _sourceSlots[hash[0] % _sourceSlots.Length];
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = await ReadAsync(identity, token).ConfigureAwait(false);
            if (!await IsCurrentAsync(source, token).ConfigureAwait(false)) throw new IOException("源文件在读取预览时发生变化。");
            if (cached is not null) return cached;
            TaskMediaPreview preview;
            await _generationSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var media = await InspectAsync(engine, source.Path, video, audio, token).ConfigureAwait(false);
                byte[]? cover = null;
                if (media.HasVideo)
                {
                    try
                    {
                        var stop = end > start ? Math.Min(end, media.Duration) : media.Duration;
                        var position = media.Duration > 0
                            ? Math.Clamp(start + Math.Min(1, Math.Max(0, stop - start) * .1), 0, Math.Max(0, media.Duration - .05)) : 0;
                        cover = await engine.Thumbnail(source.Path, position, Width, Height, token, pad: false,
                            videoStreamIndex: media.VideoStreamIndex).ConfigureAwait(false);
                        cover = await ImageCodec.CompressThumbnailAsync(cover, token).ConfigureAwait(false);
                    }
                    // Keep the media summary (and the original frame if only compression failed).
                    catch (Exception error) when (error is not OperationCanceledException) { }
                }
                // Queue rows only use this summary; never persist the complete probe/chapter JSON.
                preview = new(media with { RawJson = "" }, cover);
            }
            finally { _generationSlots.Release(); }
            if (!await IsCurrentAsync(source, token).ConfigureAwait(false)) throw new IOException("源文件在读取预览时发生变化。");
            if (!preview.Media.HasVideo || preview.Cover is { Length: > 0 })
                await _store.WriteAsync(identity, JsonSerializer.SerializeToUtf8Bytes(preview), token).ConfigureAwait(false);
            return preview;
        }
        finally { slot.Release(); }
    }

    private static async Task<MediaInfo> InspectAsync(IMediaEngine engine, string path, int video, int audio, CancellationToken token)
    {
        if (video == 0 && audio == 0 && ImageCompression.Supports(path) && !HeifImage.Supports(path))
        {
            try
            {
                var image = await ImageCodec.InspectStaticAsync(path, token).ConfigureAwait(false);
                return new(0, image.Width, image.Height, false, true, "", image.Codec);
            }
            // Animated/multimedia containers still need their media streams and duration.
            catch (Exception error) when (error is InvalidDataException or ImageMagick.MagickException) { }
        }
        return await engine.Probe(path, token, video, audio).ConfigureAwait(false);
    }

    private static Task<bool> IsCurrentAsync(Source source, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var file = new FileInfo(source.Path);
        return file.Exists && file.Length == source.Length && file.LastWriteTimeUtc.Ticks == source.Modified;
    }, token);

    private async Task<TaskMediaPreview?> ReadAsync(string identity, CancellationToken token)
    {
        var bytes = await _store.ReadAsync(identity, token).ConfigureAwait(false);
        if (bytes is null) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<TaskMediaPreview>(bytes);
            if (entry?.Media is not { } media || media.RawJson != "" || !double.IsFinite(media.Duration)
                || media.Duration < 0 || media.Width < 0 || media.Height < 0 || !double.IsFinite(media.FrameRate)) return null;
            if (media.HasVideo && entry.Cover is not { Length: > 0 }) return null;
            token.ThrowIfCancellationRequested();
            return entry;
        }
        catch (JsonException) { return null; }
    }

    private sealed record Source(string Path, long Length, long Modified)
    {
        public string Identity => OperatingSystem.IsWindows() ? Path.ToUpperInvariant() : Path;
        public static Source Read(string path)
        {
            var file = new FileInfo(System.IO.Path.GetFullPath(path));
            if (!file.Exists) throw new FileNotFoundException("源文件缺失或不可访问", file.FullName);
            return new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
        }
    }
}
