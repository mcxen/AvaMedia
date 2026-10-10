using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record TaskMediaPreview(MediaInfo Media, byte[]? Cover);

/// <summary>Persistent, compressed queue artwork and summaries, shared by tasks using the same source.</summary>
public sealed class TaskPreviewCache
{
    private const int Width = 228, Height = 144, MaximumEntryBytes = 512 * 1024;
    private const long MaximumCacheBytes = 128L * 1024 * 1024;
    private readonly string _root;
    private readonly SemaphoreSlim _generationSlots = new(2, 2);
    private readonly SemaphoreSlim[] _sourceSlots = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private long _lastPrune;
    public static TaskPreviewCache Shared { get; } = new();

    public TaskPreviewCache(string? root = null) => _root = root ?? Path.Combine(Storage.DefaultRoot, "task-preview-cache");

    public async Task<TaskMediaPreview> GetAsync(IMediaEngine engine, string path, int video, int audio,
        double start, double end, CancellationToken token)
    {
        if (video < 0 || audio < 0 || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < 0)
            throw new ArgumentException("任务预览参数无效。");
        // File metadata can live on a network volume; never inspect it on the UI thread.
        var source = await Task.Run(() => Source.Read(path), token).ConfigureAwait(false);
        var identity = JsonSerializer.Serialize(new { Version = 1, Source = source.Identity, source.Length, source.Modified,
            Video = video, Audio = audio, Start = start, End = end, Width, Height,
            FFmpeg = engine.Settings.FFmpegPath, FFprobe = engine.Settings.FFprobePath });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var file = Path.Combine(_root, Convert.ToHexString(hash).ToLowerInvariant() + ".json");
        var cached = await ReadAsync(file, token).ConfigureAwait(false);
        if (cached is not null && await IsCurrentAsync(source, token).ConfigureAwait(false)) return cached;

        // Coalesce concurrent requests without keeping an unbounded dictionary of media paths.
        var slot = _sourceSlots[hash[0] % _sourceSlots.Length];
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            cached = await ReadAsync(file, token).ConfigureAwait(false);
            if (!await IsCurrentAsync(source, token).ConfigureAwait(false)) throw new IOException("源文件在读取预览时发生变化。");
            if (cached is not null) return cached;
            TaskMediaPreview preview;
            await _generationSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var media = await engine.Probe(source.Path, token, video, audio).ConfigureAwait(false);
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
                        cover = await ImageViewerCodec.CompressThumbnailAsync(cover, token).ConfigureAwait(false);
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
                await WriteAsync(file, preview, token).ConfigureAwait(false);
            return preview;
        }
        finally { slot.Release(); }
    }

    private static Task<bool> IsCurrentAsync(Source source, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var file = new FileInfo(source.Path);
        return file.Exists && file.Length == source.Length && file.LastWriteTimeUtc.Ticks == source.Modified;
    }, token);

    private static async Task<TaskMediaPreview?> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumEntryBytes) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = await JsonSerializer.DeserializeAsync<Entry>(stream, cancellationToken: token).ConfigureAwait(false);
            if (entry?.Media is not { } media || media.RawJson != "" || !double.IsFinite(media.Duration)
                || media.Duration < 0 || media.Width < 0 || media.Height < 0 || !double.IsFinite(media.FrameRate)) return null;
            if (media.HasVideo && entry.Cover is not { Length: > 0 }) return null;
            if (entry.Cover is { } cover && entry.CoverHash != Convert.ToHexString(SHA256.HashData(cover))) return null;
            token.ThrowIfCancellationRequested();
            return new(media, entry.Cover);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task WriteAsync(string path, TaskMediaPreview preview, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            var entry = new Entry(preview.Media, preview.Cover,
                preview.Cover is { } cover ? Convert.ToHexString(SHA256.HashData(cover)) : null);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16384, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: token).ConfigureAwait(false);
                if (stream.Length > MaximumEntryBytes) return;
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            Prune();
        }
        // The cache is optional: storage failures must not hide an otherwise valid preview.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Prune()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastPrune);
        if ((previous != 0 && now - previous < 300_000) || Interlocked.CompareExchange(ref _lastPrune, now, previous) != previous) return;
        long bytes = 0; var count = 0;
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc))
        {
            bytes += file.Length;
            if (++count <= 2048 && bytes <= MaximumCacheBytes) continue;
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Entry(MediaInfo Media, byte[]? Cover, string? CoverHash);
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
