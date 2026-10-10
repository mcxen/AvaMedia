using ImageMagick;

namespace AvaMedia.Core;

public sealed record ImageViewerFrame(byte[] Png, TimeSpan Delay);
public sealed record ImageViewerDocument(int Width, int Height, string Format, ImageViewerFrame[] Frames,
    IReadOnlyDictionary<string, string> Metadata, string? MapUrl, int LoopCount, bool Animated);
public sealed record ImageViewerExport(string Format = "png", int Quality = 90, int MaxDimension = 0,
    int Rotation = 0, bool Flip = false, bool StripMetadata = false);

/// <summary>Bundled cross-platform decoder: primary HEIC, EXIF orientation, RAW and coalesced animation frames.</summary>
public static class ImageViewerCodec
{
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    static ImageViewerCodec()
    {
        ResourceLimits.Memory = 512UL * 1024 * 1024;
        ResourceLimits.Disk = 1024UL * 1024 * 1024;
        ResourceLimits.ListLength = 1024;
        ResourceLimits.Width = ResourceLimits.Height = 32768;
        ResourceLimits.Thread = 2;
    }
    public static async Task<ImageViewerDocument> DecodeAsync(ImageViewerEntry entry, CancellationToken token)
    {
        var bytes = await ImageViewerSource.ReadAsync(entry, token).ConfigureAwait(false);
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => Decode(bytes, token), token).ConfigureAwait(false); }
        finally { DecodeGate.Release(); }
    }
    /// <summary>Compress an already scaled preview without another media decode or FFmpeg process.</summary>
    public static async Task<byte[]> CompressThumbnailAsync(byte[] preview, CancellationToken token)
    {
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var image = new MagickImage(preview, new MagickReadSettings { FrameCount = 1 });
                ValidateSize(image.Width, image.Height);
                if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
                else if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Lab) image.ColorSpace = ColorSpace.sRGB;
                image.Strip(); image.Quality = 82;
                var opaque = image.IsOpaque;
                if (opaque) image.Alpha(AlphaOption.Remove);
                var compressed = image.ToByteArray(opaque ? MagickFormat.Jpeg : MagickFormat.Png);
                token.ThrowIfCancellationRequested();
                return compressed.Length < preview.Length ? compressed : preview;
            }, token).ConfigureAwait(false);
        }
        finally { DecodeGate.Release(); }
    }
    private static ImageViewerDocument Decode(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (AnimatedPng.IsAnimated(bytes)) return AnimatedPng.Decode(bytes, token);
        var info = new MagickImageInfo(bytes);
        ValidateSize(info.Width, info.Height);
        var animated = info.Format is MagickFormat.Gif or MagickFormat.WebP or MagickFormat.APng or MagickFormat.Mng;
        using var images = new MagickImageCollection();
        var settings = new MagickReadSettings();
        var multiple = info.Format is MagickFormat.Tiff or MagickFormat.Ico;
        if (!animated && !multiple) settings.FrameCount = 1;
        images.Read(bytes, settings);
        token.ThrowIfCancellationRequested();
        if (images.Count == 0) throw new InvalidDataException("图片中没有可显示的画面。");
        if (animated) images.Coalesce();
        var first = images[0]; var metadata = Metadata(first); var map = Map(first);
        var frames = new List<ImageViewerFrame>(); long resident = 0;
        foreach (var image in images)
        {
            token.ThrowIfCancellationRequested(); ValidateSize(image.Width, image.Height);
            image.AutoOrient();
            if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
            else if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Lab) image.ColorSpace = ColorSpace.sRGB;
            resident += (long)image.Width * image.Height * 4;
            if (resident > 384L * 1024 * 1024) throw new InvalidDataException("动画解码后超过 384 MB，请缩小动画后打开。");
            var ticks = Math.Max(1, image.AnimationTicksPerSecond);
            frames.Add(new(image.ToByteArray(MagickFormat.Png), TimeSpan.FromMilliseconds(Math.Max(20, image.AnimationDelay * 1000d / ticks))));
            if (!animated && !multiple) break;
        }
        return new((int)first.Width, (int)first.Height, info.Format.ToString(), frames.ToArray(), metadata, map, (int)first.AnimationIterations, animated);
    }
    public static Task<byte[]> ThumbnailAsync(string path, int width, int height, bool pad, CancellationToken token)
        => ThumbnailAsync(new ImageViewerEntry(path), width, height, pad, token);
    public static async Task<byte[]> ThumbnailAsync(ImageViewerEntry entry, int width, int height, bool pad, CancellationToken token)
    {
        var bytes = await ImageViewerSource.ReadAsync(entry, token).ConfigureAwait(false);
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var preview = AnimatedPng.IsAnimated(bytes) ? AnimatedPng.Decode(bytes, token, firstFrameOnly: true).Frames[0].Png : bytes;
                var info = new MagickImageInfo(preview); ValidateSize(info.Width, info.Height);
                using var image = new MagickImage(preview, new MagickReadSettings { FrameCount = 1 });
                image.AutoOrient(); image.Thumbnail((uint)width, (uint)height);
                if (pad) { image.BackgroundColor = MagickColors.Black; image.Extent((uint)width, (uint)height, Gravity.Center); }
                token.ThrowIfCancellationRequested(); return image.ToByteArray(MagickFormat.Png);
            }, token).ConfigureAwait(false);
        }
        finally { DecodeGate.Release(); }
    }
    public static async Task ExportAsync(ImageViewerEntry entry, string destination, ImageViewerExport options, CancellationToken token)
    {
        if (options.Format is not ("jpg" or "png" or "webp" or "tiff" or "bmp" or "avif")) throw new ArgumentException("输出格式无效。");
        if (options.Quality is < 1 or > 100 || options.MaxDimension is < 0 or > 32768) throw new ArgumentException("输出尺寸或质量无效。");
        var bytes = await ImageViewerSource.ReadAsync(entry, token).ConfigureAwait(false);
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var image = new MagickImage(bytes, new MagickReadSettings { FrameCount = 1 });
                ValidateSize(image.Width, image.Height); image.AutoOrient();
                if (options.Flip) image.Flop();
                if (options.Rotation != 0) image.Rotate(options.Rotation);
                if (options.MaxDimension > 0 && Math.Max(image.Width, image.Height) > options.MaxDimension)
                    image.Resize(new MagickGeometry((uint)options.MaxDimension, (uint)options.MaxDimension));
                if (options.StripMetadata) image.Strip();
                image.Quality = (uint)options.Quality;
                if (options.Format == "jpg") { image.BackgroundColor = MagickColors.White; image.Alpha(AlphaOption.Remove); }
                image.Format = options.Format switch
                { "jpg" => MagickFormat.Jpeg, "webp" => MagickFormat.WebP, "tiff" => MagickFormat.Tiff,
                    "bmp" => MagickFormat.Bmp, "avif" => MagickFormat.Avif, _ => MagickFormat.Png };
                image.Write(temporary); token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
            }, token).ConfigureAwait(false);
        }
        finally { DecodeGate.Release(); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<ImageViewerFrame[]> EffectAsync(ImageViewerFrame[] frames, string effect, double gamma, CancellationToken token)
    {
        if (gamma is < .1 or > 5 || effect is not ("none" or "invert" or "soft" or "sharp")) throw new ArgumentException("图片效果参数无效。");
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => frames.Select(frame =>
            {
                token.ThrowIfCancellationRequested(); using var image = new MagickImage(frame.Png);
                if (effect == "invert") image.Negate();
                if (effect is "soft" or "sharp") image.Blur(0, .5);
                if (effect == "sharp") image.Sharpen(0, .8);
                if (gamma != 1) image.GammaCorrect(gamma);
                return new ImageViewerFrame(image.ToByteArray(MagickFormat.Png), frame.Delay);
            }).ToArray(), token).ConfigureAwait(false);
        }
        finally { DecodeGate.Release(); }
    }
    private static void ValidateSize(uint width, uint height)
    {
        if (width == 0 || height == 0 || (long)width * height > 100_000_000)
            throw new InvalidDataException("图片尺寸无效或超过 1 亿像素。");
    }
    private static Dictionary<string, string> Metadata(IMagickImage<byte> image)
    {
        var values = new Dictionary<string, string>();
        var exif = image.GetExifProfile();
        if (exif is not null)
            foreach (var field in exif.Values)
            {
                var value = field.GetValue();
                if (value is byte[] || value is Array array && array.Length > 12) continue;
                var text = value is Array items ? string.Join(", ", items.Cast<object>()) : value?.ToString() ?? "";
                if (text.Length > 0) values[field.Tag.ToString()] = text;
            }
        return values;
    }
    private static string? Map(IMagickImage<byte> image)
    {
        var profile = image.GetExifProfile();
        var lat = profile?.GetValue(ExifTag.GPSLatitude)?.Value; var lon = profile?.GetValue(ExifTag.GPSLongitude)?.Value;
        if (lat is not { Length: 3 } || lon is not { Length: 3 }) return null;
        double Coordinate(Rational[] parts) => parts[0].ToDouble() + parts[1].ToDouble() / 60 + parts[2].ToDouble() / 3600;
        var latitude = Coordinate(lat); var longitude = Coordinate(lon);
        if (profile?.GetValue(ExifTag.GPSLatitudeRef)?.Value == "S") latitude = -latitude;
        if (profile?.GetValue(ExifTag.GPSLongitudeRef)?.Value == "W") longitude = -longitude;
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180) return null;
        return FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={latitude:0.######}&mlon={longitude:0.######}#map=15/{latitude:0.######}/{longitude:0.######}");
    }
}
