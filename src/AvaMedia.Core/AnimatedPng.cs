using System.Buffers.Binary;
using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>APNG uses the bundled PNG decoder, without requiring an external ffmpeg delegate.</summary>
internal static class AnimatedPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public static bool IsAnimated(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(Signature)) return false;
        for (var offset = 8; offset + 12 <= bytes.Length;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (size > bytes.Length - offset - 12) return false;
            if (bytes.AsSpan(offset + 4, 4).SequenceEqual("acTL"u8)) return true;
            if (bytes.AsSpan(offset + 4, 4).SequenceEqual("IDAT"u8)) return false;
            offset += checked((int)size + 12);
        }
        return false;
    }
    public static ImageViewerDocument Decode(byte[] bytes, CancellationToken token, bool firstFrameOnly = false)
    {
        var header = bytes.AsSpan(16, 13).ToArray();
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        if (width < 1 || height < 1 || (long)width * height > 100_000_000) throw new InvalidDataException("APNG 图片尺寸无效。");
        using var canvas = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        canvas.Erase(SKColors.Transparent);
        var ancillary = new List<(string Type, byte[] Data)>(); var frames = new List<ImageViewerFrame>();
        using var pixels = new MemoryStream();
        Frame? frame = null; var loops = 0; var declared = 0; var sawPixels = false;
        void Complete()
        {
            if (frame is null || pixels.Length == 0) { pixels.SetLength(0); return; }
            token.ThrowIfCancellationRequested();
            if (frame.Width < 1 || frame.Height < 1 || frame.X < 0 || frame.Y < 0 || frame.Width > width - frame.X || frame.Height > height - frame.Y)
                throw new InvalidDataException("APNG 帧超出画布。");
            if ((long)width * height * 4 * (frames.Count + 1) > 384L * 1024 * 1024) throw new InvalidDataException("APNG 动画解码后超过 384 MB。");
            var before = frame.Dispose == 2 ? canvas.Copy() : null;
            try
            {
                using var encoded = new MemoryStream(); encoded.Write(Signature);
                var localHeader = (byte[])header.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(localHeader, (uint)frame.Width);
                BinaryPrimitives.WriteUInt32BigEndian(localHeader.AsSpan(4), (uint)frame.Height);
                WriteChunk(encoded, "IHDR", localHeader);
                foreach (var item in ancillary) WriteChunk(encoded, item.Type, item.Data);
                WriteChunk(encoded, "IDAT", pixels.ToArray()); WriteChunk(encoded, "IEND", []);
                using var bitmap = SKBitmap.Decode(encoded.ToArray()) ?? throw new InvalidDataException("APNG 帧解码失败。");
                using (var drawing = new SKCanvas(canvas))
                {
                    using var paint = new SKPaint { BlendMode = frame.Blend == 0 ? SKBlendMode.Src : SKBlendMode.SrcOver };
                    drawing.DrawBitmap(bitmap, frame.X, frame.Y, paint);
                }
                using var image = SKImage.FromBitmap(canvas); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                frames.Add(new(data.ToArray(), TimeSpan.FromMilliseconds(Math.Max(20, frame.Numerator * 1000d / (frame.Denominator == 0 ? 100 : frame.Denominator)))));
                using var restore = new SKCanvas(canvas);
                if (frame.Dispose == 1)
                { using var paint = new SKPaint { BlendMode = SKBlendMode.Clear }; restore.DrawRect(frame.X, frame.Y, frame.Width, frame.Height, paint); }
                else if (before is not null)
                { using var paint = new SKPaint { BlendMode = SKBlendMode.Src }; restore.DrawBitmap(before, 0, 0, paint); }
            }
            finally { before?.Dispose(); pixels.SetLength(0); }
        }
        for (var offset = 8; offset + 12 <= bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            if (length > bytes.Length - offset - 12) throw new InvalidDataException("APNG 数据不完整。");
            var data = bytes.AsSpan(offset + 8, (int)length); var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            switch (type)
            {
                case "acTL":
                    if (data.Length != 8) throw new InvalidDataException("APNG 动画头无效。");
                    declared = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data)); loops = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    if (declared is < 1 or > 1024) throw new InvalidDataException("APNG 帧数须在 1–1024 之间。"); break;
                case "fcTL":
                    Complete();
                    if (firstFrameOnly && frames.Count > 0) return new(width, height, "APNG", frames.ToArray(), new Dictionary<string, string>(), null, loops, true);
                    if (data.Length != 26 || data[24] > 2 || data[25] > 1) throw new InvalidDataException("APNG 帧头无效。");
                    frame = new(checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..])), checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[8..])),
                        checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[12..])), checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[16..])),
                        BinaryPrimitives.ReadUInt16BigEndian(data[20..]), BinaryPrimitives.ReadUInt16BigEndian(data[22..]), data[24], data[25]); break;
                case "IDAT": sawPixels = true; if (frame is not null) pixels.Write(data); break;
                case "fdAT":
                    sawPixels = true; if (frame is null || data.Length < 4) throw new InvalidDataException("APNG 帧数据无效。"); pixels.Write(data[4..]); break;
                case "IEND": Complete(); offset = bytes.Length; continue;
                default:
                    if (!sawPixels && type is "PLTE" or "tRNS" or "gAMA" or "sRGB" or "iCCP" or "cHRM" or "sBIT") ancillary.Add((type, data.ToArray())); break;
            }
            offset += checked((int)length + 12);
        }
        if (frames.Count != declared && !firstFrameOnly) throw new InvalidDataException("APNG 帧数据缺失。");
        return new(width, height, "APNG", frames.ToArray(), new Dictionary<string, string>(), null, loops, true);
    }
    private sealed record Frame(int Width, int Height, int X, int Y, int Numerator, int Denominator, byte Dispose, byte Blend);
    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length); output.Write(number);
        var name = System.Text.Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (var value in name.Concat(data))
        { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0); }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); output.Write(number);
    }
}
