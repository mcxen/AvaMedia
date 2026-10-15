using System.Runtime.InteropServices;
using SkiaSharp;

namespace AvaMedia.Core;

internal static class PersonClipPreview
{
    // Match FFmpeg's scale rounding and centered RGB letterbox in PersonClipFrameSource.Filter.
    internal static (int Left, int Top, int Width, int Height) Content(MediaInfo info, int size)
    {
        var scale = size / (double)Math.Max(info.Width, info.Height);
        var width = Math.Clamp((int)Math.Round(info.Width * scale, MidpointRounding.AwayFromZero), 1, size);
        var height = Math.Clamp((int)Math.Round(info.Height * scale, MidpointRounding.AwayFromZero), 1, size);
        return ((size - width) / 2, (size - height) / 2, width, height);
    }

    internal static byte[] EncodePng(byte[] rgb, int size, (int Left, int Top, int Width, int Height)? content = null)
    {
        var area = content ?? (Left: 0, Top: 0, Width: size, Height: size);
        using var bitmap = new SKBitmap(area.Width, area.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var rgba = new byte[area.Width * area.Height * 4];
        for (var y = 0; y < area.Height; y++)
            for (var x = 0; x < area.Width; x++)
            {
                var source = ((y + area.Top) * size + x + area.Left) * 3;
                var target = (y * area.Width + x) * 4;
                rgba[target] = rgb[source]; rgba[target + 1] = rgb[source + 1]; rgba[target + 2] = rgb[source + 2]; rgba[target + 3] = 255;
            }
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 80);
        return data.ToArray();
    }
}
