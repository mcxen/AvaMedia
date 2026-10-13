using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

/// <summary>Disk-backed FFmpeg thumbnails. The first request owns a shared generation's cancellation;
/// subsequent callers can cancel their own wait. No decoded image enters managed memory.</summary>
public sealed class MediaThumbnailCache
{
    private readonly string _folder;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<string>> _inflight = new(StringComparer.Ordinal);
    public int Pending { get { lock (_gate) return _inflight.Count; } }

    // Zero selects the machine-dependent default (optional arguments must be compile-time constants).
    public MediaThumbnailCache(string? cacheFolder = null, int maxConcurrency = 0)
    {
        _folder = Path.GetFullPath(cacheFolder ?? Path.Combine(Storage.DefaultRoot, "thumb-cache"));
        if (maxConcurrency < 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _slots = new(maxConcurrency == 0 ? Math.Clamp(Environment.ProcessorCount / 2, 1, 4) : maxConcurrency);
    }

    public Task<string> GetAsync(string path, int width, double? seconds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (width is < 2 or > 4096 || seconds is { } time && (!double.IsFinite(time) || time < 0))
            throw new ArgumentException("缩略图尺寸或时间无效。");
        path = Path.GetFullPath(path);
        var key = Hash($"thumb-v2|{Identity(path)}|{width}|{seconds?.ToString("R", CultureInfo.InvariantCulture)}");
        return Cached(key, async temporary =>
        {
            List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1"];
            if (VideoFormats.IsVideo(path)) args.AddRange(["-ss", MediaEngine.Number(seconds ?? 0)]);
            else if (Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg")
                args.AddRange(["-lowres", "3"]); // Native JPEG decoder downsamples before allocating a full frame.
            args.AddRange(["-i", path, "-frames:v", "1", "-an", "-vf", $"scale=w='min({width},iw*sar)':h=-2:reset_sar=1", "-filter_threads", "1",
                "-threads", "1", "-q:v", "5", "-f", "image2", "-y", temporary]);
            await Run(args, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<string> MosaicAsync(IReadOnlyList<string> paths, int width, int height, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (paths.Count == 0 || width is < 4 or > 4096 || height is < 4 or > 4096) throw new ArgumentException("封面尺寸或文件无效。");
        var inputs = paths.Take(3).Select(Path.GetFullPath).ToArray();
        var key = Hash($"mosaic-v1|{width}|{height}|" + string.Join("|", inputs.Select(Identity)));
        // Generate the dependencies before taking a composition slot, so concurrency=1 cannot deadlock.
        return Cached(key, async temporary =>
        {
            var thumbs = await Task.WhenAll(inputs.Select(path => GetAsync(path, width, ct: ct))).ConfigureAwait(false);
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-filter_complex_threads", "1"];
                foreach (var thumb in thumbs) args.AddRange(["-i", thumb]);
                string Fit(int w, int h) => $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1";
                var left = width * 2 / 3; var right = width - left; var top = height / 2;
                var filter = thumbs.Length switch
                {
                    1 => $"[0:v]{Fit(width, height)}[cover]",
                    2 => $"[0:v]{Fit(left, height)}[a];[1:v]split[b0][c0];[b0]{Fit(right, top)}[b];[c0]{Fit(right, height - top)}[c];[b][c]vstack[r];[a][r]hstack[cover]",
                    _ => $"[0:v]{Fit(left, height)}[a];[1:v]{Fit(right, top)}[b];[2:v]{Fit(right, height - top)}[c];[b][c]vstack[r];[a][r]hstack[cover]"
                };
                args.AddRange(["-filter_complex", filter, "-map", "[cover]", "-frames:v", "1", "-threads", "1", "-q:v", "5", "-pix_fmt", "yuvj444p", "-f", "image2", "-y", temporary]);
                await Run(args, ct).ConfigureAwait(false);
            }
            finally { _slots.Release(); }
        }, ct, acquireSlot: false);
    }

    private Task<string> Cached(string key, Func<string, Task> generate, CancellationToken ct, bool acquireSlot = true)
    {
        var target = Path.Combine(_folder, key + ".jpg");
        lock (_gate)
        {
            if (_inflight.TryGetValue(key, out var existing)) return ct.CanBeCanceled ? existing.WaitAsync(ct) : existing;
            if (File.Exists(target)) return Task.FromResult(target);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight.Add(key, completion.Task);
            _ = GenerateAsync();
            return completion.Task;

            async Task GenerateAsync()
            {
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp.jpg";
                var acquired = false;
                Exception? failure = null;
                try
                {
                    Directory.CreateDirectory(_folder);
                    if (acquireSlot) { await _slots.WaitAsync(ct).ConfigureAwait(false); acquired = true; }
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(target))
                    {
                        await generate(temporary).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("缩略图生成失败。");
                        File.Move(temporary, target, true);
                    }
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failure ??= error; }
                    finally
                    {
                        if (acquired) _slots.Release();
                        lock (_gate) _inflight.Remove(key);
                    }
                }
                if (failure is OperationCanceledException) completion.TrySetCanceled(ct);
                else if (failure is not null) completion.TrySetException(failure);
                else completion.TrySetResult(target);
            }
        }
    }
    private static string Identity(string path)
    {
        var file = new FileInfo(path);
        var fullPath = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName;
        return $"{fullPath.Length}:{fullPath}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
    }
    private static string Hash(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task Run(List<string> args, CancellationToken ct)
    {
        var ffmpeg = MediaEngine.Resolve("", "ffmpeg");
        var result = await ProcessRunner.Run(ffmpeg, args, ct, maximumOutputChars: 8192).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("缩略图生成失败：" + result.Error);
    }
}
