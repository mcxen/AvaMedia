using System.Runtime.InteropServices;
using AvaMedia.Core;
using SkiaSharp;

namespace AvaMedia.Desktop.Controls;

/// <summary>Screenshot projection without executing Skia's GPU-only runtime shader on a raster canvas.</summary>
internal static class PanoramaRaster
{
    public static SKBitmap Project(SKImage frame, PanoramaSettings view, int width, int height)
    {
        view = view.Normalize();
        using var source = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        if (!frame.ReadPixels(source.Info, source.GetPixels(), source.RowBytes, 0, 0))
            throw new InvalidOperationException("无法读取 VR 画面。");
        var stride = source.RowBytes / 4;
        var input = new int[stride * source.Height];
        Marshal.Copy(source.GetPixels(), input, 0, input.Length);
        var output = new int[checked(width * height)];
        var eyeWidth = view.Layout == PanoramaLayout.SideBySide ? frame.Width / 2 : frame.Width;
        var eyeHeight = view.Layout == PanoramaLayout.TopBottom ? frame.Height / 2 : frame.Height;
        var left = view.Layout == PanoramaLayout.SideBySide && view.Eye == PanoramaEye.Right ? eyeWidth : 0;
        var top = view.Layout == PanoramaLayout.TopBottom && view.Eye == PanoramaEye.Right ? eyeHeight : 0;
        var yaw = view.Yaw * Math.PI / 180; var pitch = view.Pitch * Math.PI / 180; var roll = view.Roll * Math.PI / 180;
        var cy = Math.Cos(yaw); var sy = Math.Sin(yaw); var cp = Math.Cos(pitch); var sp = Math.Sin(pitch);
        var cr = Math.Cos(roll); var sr = Math.Sin(roll); var fov = Math.Tan(view.FieldOfView * Math.PI / 360);
        var half = view.Mode == PanoramaMode.HalfSphere;
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var rx = (2 * (x + .5) - width) / height * fov;
                var ry = (height - 2 * (y + .5)) / height * fov;
                var length = Math.Sqrt(rx * rx + ry * ry + 1); rx /= length; ry /= length;
                var rz = 1 / length;
                var ax = cr * rx - sr * ry; var ay = sr * rx + cr * ry;
                var by = cp * ay + sp * rz; var bz = -sp * ay + cp * rz;
                var dx = cy * ax + sy * bz; var dz = -sy * ax + cy * bz;
                double px, py;
                if (view.Projection == PanoramaProjection.Fisheye)
                {
                    var angle = Math.Acos(Math.Clamp(dz, -1, 1));
                    if (angle > Math.PI / 2) { output[y * width + x] = unchecked((int)0xff000000); continue; }
                    var radius = angle / (Math.PI / 2) * Math.Min(eyeWidth, eyeHeight) / 2;
                    var axis = Math.Max(Math.Sqrt(dx * dx + by * by), .000001);
                    px = eyeWidth / 2d + dx / axis * radius - .5;
                    py = eyeHeight / 2d - by / axis * radius - .5;
                }
                else
                {
                    var longitude = Math.Atan2(dx, dz);
                    if (half && Math.Abs(longitude) > Math.PI / 2)
                    { output[y * width + x] = unchecked((int)0xff000000); continue; }
                    var u = longitude / (half ? Math.PI : 2 * Math.PI) + .5;
                    if (!half) u -= Math.Floor(u);
                    px = u * eyeWidth - .5;
                    py = (.5 - Math.Asin(Math.Clamp(by, -1, 1)) / Math.PI) * eyeHeight - .5;
                }
                px = Math.Clamp(px, 0, eyeWidth - 1); py = Math.Clamp(py, 0, eyeHeight - 1);
                var x0 = (int)px; var y0 = (int)py; var x1 = Math.Min(x0 + 1, eyeWidth - 1); var y1 = Math.Min(y0 + 1, eyeHeight - 1);
                var a = input[(top + y0) * stride + left + x0];
                var b = input[(top + y0) * stride + left + x1];
                var c = input[(top + y1) * stride + left + x0];
                var d = input[(top + y1) * stride + left + x1];
                var fx = px - x0; var fy = py - y0; var color = unchecked((int)0xff000000);
                for (var shift = 0; shift < 24; shift += 8)
                    color |= (int)Math.Round(((a >> shift & 255) * (1 - fx) + (b >> shift & 255) * fx) * (1 - fy)
                        + ((c >> shift & 255) * (1 - fx) + (d >> shift & 255) * fx) * fy) << shift;
                output[y * width + x] = color;
            }
        });
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(output, 0, result.GetPixels(), output.Length);
        return result;
    }
}
