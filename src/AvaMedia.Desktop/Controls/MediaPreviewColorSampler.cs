using AvaMedia.Core;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using System.Security.Cryptography;

namespace AvaMedia.Desktop.Controls;

/// <summary>One background decoder per surface, with a bounded frame cache. Never reads the displayed bitmap.</summary>
internal sealed class MediaPreviewColorSampler
{
    // PNG codecs may decode at source size before reducing. Prefer neutral shadows for costly sources.
    internal const int MaximumEncodedBytes = 2 * 1024 * 1024;
    private readonly SemaphoreSlim _slot = new(1);
    private readonly Dictionary<string, MediaPreviewColors.Edges> _cache = [];
    private readonly Queue<string> _keys = [];

    public Task<MediaPreviewColors.Edges?> SampleAsync(byte[] encoded, string key, CancellationToken token)
        => Task.Run(async () =>
        {
            await _slot.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (encoded.Length > MaximumEncodedBytes) return null;
                // Byte identity also invalidates cached colours when a file is replaced in place.
                key += "|" + Convert.ToHexString(SHA256.HashData(encoded));
                if (_cache.TryGetValue(key, out var cached)) return cached;
                using var data = SKData.CreateCopy(encoded);
                using var codec = SKCodec.Create(data);
                if (codec is null) return null;
                if ((long)codec.Info.Width * codec.Info.Height > 4_000_000) return null;
                var scale = Math.Min(1d, 64d / Math.Max(codec.Info.Width, codec.Info.Height));
                var info = new SKImageInfo(Math.Max(1, (int)(codec.Info.Width * scale)),
                    Math.Max(1, (int)(codec.Info.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var stream = new MemoryStream(encoded);
                using var bitmap = Bitmap.DecodeToWidth(stream, info.Width, BitmapInterpolationMode.LowQuality);
                var size = bitmap.PixelSize;
                using var thumbnail = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                using (var buffer = new LockedFramebuffer(thumbnail.GetPixels(), size,
                           thumbnail.RowBytes, new Vector(96, 96), PixelFormat.Rgba8888, () => { }))
                    bitmap.CopyPixels(buffer, AlphaFormat.Unpremul);
                token.ThrowIfCancellationRequested();
                var colors = MediaPreviewColors.Sample(thumbnail.Bytes, thumbnail.Width, thumbnail.Height, thumbnail.RowBytes);
                if (_keys.Count == 128) _cache.Remove(_keys.Dequeue());
                _cache.Add(key, colors); _keys.Enqueue(key);
                return colors;
            }
            finally { _slot.Release(); }
        }, token);
}
