using System.Buffers.Binary;
using ImageMagick;

namespace AvaMedia.Core;

public sealed record ImageEncodingOptions(string Format = "png", int Quality = 90, int MaxDimension = 0,
    int Rotation = 0, bool Flip = false, bool StripMetadata = false, bool Lossless = false)
{
    public void Validate()
    {
        if (Format is not ("jpg" or "png" or "webp" or "tiff" or "bmp" or "avif")) throw new ArgumentException("输出格式无效。");
        if (Quality is < 1 or > 100 || MaxDimension is < 0 or > 32768) throw new ArgumentException("输出尺寸或质量无效。");
        if (Lossless && Format == "jpg") throw new ArgumentException("JPEG 不支持无损编码。");
    }
}
public sealed record ImageEncodingResult(long Bytes, int Width, int Height);

public static partial class ImageCodec
{
    /// <summary>Header-only inspection shared with compression; ordinary still images need no FFprobe.</summary>
    public static async Task<ImageCompressionSource> InspectStaticAsync(string path, CancellationToken token)
    {
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var image = new MagickImage(); image.Ping(path, new MagickReadSettings { FrameCount = 1 });
                ValidateSize(image.Width, image.Height);
                if (image.Format is not (MagickFormat.Jpeg or MagickFormat.Png or MagickFormat.WebP or MagickFormat.Bmp or MagickFormat.Bmp2 or MagickFormat.Bmp3))
                    throw new InvalidDataException("文件不是支持的静态图片；动画图片请使用对应格式转换。");
                if (HasAnimation(path, out var pngDepth, out var pngColorType)) throw new InvalidDataException("这是动画图片，压缩功能不会丢弃动画帧；请使用动画格式转换。");
                var width = (int)image.Width; var height = (int)image.Height;
                if (image.Orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom)
                    (width, height) = (height, width);
                // Read PNG's source depth directly: the shared decoder is a Q8 build.
                var depth = Math.Max(8, pngDepth > 0 ? pngDepth : (int)image.Depth);
                var gray = pngColorType is 0 or 4 || pngColorType < 0 && image.ColorType is ColorType.Grayscale or ColorType.GrayscaleAlpha;
                var alpha = pngColorType is 4 or 6 || image.HasAlpha;
                var pixels = gray ? alpha ? depth > 8 ? "ya16be" : "ya8" : depth > 8 ? "gray16be" : "gray"
                    : alpha ? depth > 8 ? "rgba64be" : "rgba" : depth > 8 ? "rgb48be" : "rgb24";
                token.ThrowIfCancellationRequested();
                return new ImageCompressionSource(Path.GetFullPath(path), new FileInfo(path).Length, width, height, depth, pixels,
                    image.Orientation is not (OrientationType.Undefined or OrientationType.TopLeft), Codec: image.Format switch
                    { MagickFormat.Jpeg => "mjpeg", MagickFormat.Png => "png", MagickFormat.WebP => "webp", _ => "bmp" });
            }, token).ConfigureAwait(false);
        }
        finally { DecodeGate.Release(); }
    }

    /// <summary>The viewer and compressor share transforms, formats and atomic non-overwriting writes.</summary>
    public static async Task<ImageEncodingResult> ExportAsync(ImageViewerEntry entry, string destination,
        ImageEncodingOptions options, CancellationToken token, long? smallerThan = null)
    {
        options.Validate(); destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("输出文件已存在，请选择新的名称。");
        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = await ImageViewerSource.ReadAsync(entry, token).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var first = AnimatedPng.IsAnimated(bytes) ? AnimatedPng.Decode(bytes, token, firstFrameOnly: true).Frames[0].Png : bytes;
                using var image = new MagickImage(first, new MagickReadSettings { FrameCount = 1 });
                ValidateSize(image.Width, image.Height); image.AutoOrient();
                if (options.Flip) image.Flop();
                if (options.Rotation != 0) image.Rotate(options.Rotation);
                if (options.MaxDimension > 0 && Math.Max(image.Width, image.Height) > options.MaxDimension)
                    image.Resize(new MagickGeometry((uint)options.MaxDimension, (uint)options.MaxDimension));
                if (options.Format == "webp" && Math.Max(image.Width, image.Height) > 16383)
                    throw new ArgumentException("WebP 最长边最多 16383 像素；请缩小尺寸或选择 PNG / JPEG。");
                ConfigureEncoding(image, options);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); image.Write(temporary);
                var result = new ImageEncodingResult(new FileInfo(temporary).Length, (int)image.Width, (int)image.Height);
                if (result.Bytes == 0) throw new InvalidDataException("图片编码没有生成有效文件。");
                if (smallerThan is { } sourceBytes && result.Bytes >= sourceBytes)
                    throw new InvalidOperationException($"当前设置未压小：原图 {ImageCompression.Bytes(sourceBytes)}，编码后 {ImageCompression.Bytes(result.Bytes)}。已保留原图，未写入输出；可降低质量、缩小尺寸或改用 WebP。");
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: false);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally
        {
            DecodeGate.Release();
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void NormalizeColor(IMagickImage<byte> image)
    {
        if (image.GetColorProfile() is not null) image.TransformColorSpace(ColorProfiles.SRGB);
        else if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Lab) image.ColorSpace = ColorSpace.sRGB;
    }

    private static void ConfigureEncoding(IMagickImage<byte> image, ImageEncodingOptions options)
    {
        if (options.Format == "jpg" || options.StripMetadata) NormalizeColor(image);
        if (options.Format == "jpg")
        { image.BackgroundColor = MagickColors.White; image.Alpha(AlphaOption.Remove); }
        if (options.StripMetadata) image.Strip();
        image.Quality = (uint)options.Quality;
        image.Format = options.Format switch
        { "jpg" => MagickFormat.Jpeg, "webp" => MagickFormat.WebP, "tiff" => MagickFormat.Tiff,
            "bmp" => MagickFormat.Bmp, "avif" => MagickFormat.Avif, _ => MagickFormat.Png };
        if (options.Format == "png") image.Settings.SetDefines(new ImageMagick.Formats.PngWriteDefines
        { CompressionLevel = 9, PreserveColorMap = true });
        if (options.Format == "webp")
            image.Settings.SetDefines(new ImageMagick.Formats.WebPWriteDefines { Lossless = options.Lossless, Exact = true });
    }

    private static bool HasAnimation(string path, out int pngDepth, out int pngColorType)
    {
        pngDepth = 0; pngColorType = -1;
        using var stream = File.OpenRead(path); Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12) return false;
        if (header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8))
        {
            Span<byte> chunk = stackalloc byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                stream.ReadExactly(chunk); var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (chunk[..4].SequenceEqual("ANIM"u8) || chunk[..4].SequenceEqual("ANMF"u8)) return true;
                stream.Seek((long)length + (length & 1), SeekOrigin.Current);
            }
        }
        else if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            stream.Position = 16; Span<byte> ihdr = stackalloc byte[10]; stream.ReadExactly(ihdr);
            pngDepth = ihdr[8]; pngColorType = ihdr[9];
            stream.Position = 8; Span<byte> chunk = stackalloc byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                stream.ReadExactly(chunk); if (chunk[4..].SequenceEqual("acTL"u8)) return true;
                stream.Seek((long)BinaryPrimitives.ReadUInt32BigEndian(chunk[..4]) + 4, SeekOrigin.Current);
            }
        }
        return false;
    }
}
