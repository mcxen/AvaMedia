using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>Conservative RGB thumbnail comparison; reuse remains an approximate sampling optimization.</summary>
internal static class VideoFrameSimilarity
{
    private const int Grid = 64;

    public static byte[] FromEncoded(byte[] image)
    {
        using var source = SKBitmap.Decode(image) ?? throw new InvalidDataException("无法读取视频画面。");
        using var reduced = source.Resize(new SKImageInfo(Grid, Grid, SKColorType.Rgba8888, SKAlphaType.Opaque), SKFilterQuality.Medium)
            ?? throw new InvalidDataException("无法缩小视频画面。");
        var signature = new byte[Grid * Grid * 3];
        for (var y = 0; y < Grid; y++)
            for (var x = 0; x < Grid; x++)
            {
                var color = reduced.GetPixel(x, y); var offset = (y * Grid + x) * 3;
                signature[offset] = color.Red; signature[offset + 1] = color.Green; signature[offset + 2] = color.Blue;
            }
        return signature;
    }

    public static byte[] FromRgb(byte[] rgb, int width, int height)
    {
        if (width < Grid || height < Grid || rgb.Length != checked(width * height * 3)) throw new ArgumentException("视频画面尺寸无效。");
        var signature = new byte[Grid * Grid * 3];
        for (var gy = 0; gy < Grid; gy++)
            for (var gx = 0; gx < Grid; gx++)
            {
                var left = gx * width / Grid; var right = (gx + 1) * width / Grid;
                var top = gy * height / Grid; var bottom = (gy + 1) * height / Grid;
                int red = 0, green = 0, blue = 0;
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        var offset = (y * width + x) * 3;
                        red += rgb[offset]; green += rgb[offset + 1]; blue += rgb[offset + 2];
                    }
                var count = (right - left) * (bottom - top); var target = (gy * Grid + gx) * 3;
                signature[target] = (byte)(red / count); signature[target + 1] = (byte)(green / count); signature[target + 2] = (byte)(blue / count);
            }
        return signature;
    }

    public static bool Similar(byte[] left, byte[] right)
    {
        if (left.Length != Grid * Grid * 3 || right.Length != left.Length) return false;
        long difference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            var delta = Math.Abs(left[index] - right[index]);
            // Reject a localized change even when the full-frame mean difference is small.
            if (delta > 8) return false;
            difference += delta;
        }
        return difference <= left.Length * 1.5;
    }
}
