using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AvaMedia.Core;

/// <summary>System ImageIO metadata and orientation-aware downsampling, without a media process.</summary>
internal static class AppleImageIO
{
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> ImageLibrary = new(() => NativeLibrary.Load(ImageIO));
    private static readonly Lazy<nint> CoreLibrary = new(() => NativeLibrary.Load(Core));
    public static bool Supports(string path) => OperatingSystem.IsMacOS() &&
        Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff";
    public static Task<ImageCompressionSource> InspectAsync(string path, CancellationToken token) =>
        Task.Run(() => { token.ThrowIfCancellationRequested(); using var source = Open(path); return Inspect(path, source.Value, token); }, token);
    public static Task<byte[]> ThumbnailAsync(string path, int width, int height, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var source = Open(path);
        var info = Inspect(path, source.Value, token);
        var scale = Math.Min(1, Math.Min((double)width / info.Width, (double)height / info.Height));
        var maximum = Math.Max(1, (int)Math.Floor(Math.Max(info.Width, info.Height) * scale));
        using var number = new Reference(CFNumberCreate(0, 3, ref maximum));
        using var options = new Reference(CFDictionaryCreateMutable(0, 0, 0, 0));
        CFDictionarySetValue(options.Value, Symbol(ImageLibrary.Value, "kCGImageSourceThumbnailMaxPixelSize"), number.Value);
        CFDictionarySetValue(options.Value, Symbol(ImageLibrary.Value, "kCGImageSourceCreateThumbnailFromImageAlways"), True);
        CFDictionarySetValue(options.Value, Symbol(ImageLibrary.Value, "kCGImageSourceCreateThumbnailWithTransform"), True);
        CFDictionarySetValue(options.Value, Symbol(ImageLibrary.Value, "kCGImageSourceShouldCacheImmediately"), True);
        using var image = new Reference(CGImageSourceCreateThumbnailAtIndex(source.Value, CGImageSourceGetPrimaryImageIndex(source.Value), options.Value));
        if (image.IsInvalid) throw new InvalidDataException("ImageIO 无法解码这张图片。");
        token.ThrowIfCancellationRequested();
        using var data = new Reference(CFDataCreateMutable(0, 0));
        using var type = new Reference(CFStringCreateWithCString(0, "public.png", 0x08000100));
        using var destination = new Reference(CGImageDestinationCreateWithData(data.Value, type.Value, 1, 0));
        if (destination.IsInvalid) throw new InvalidOperationException("ImageIO 无法创建预览编码器。");
        CGImageDestinationAddImage(destination.Value, image.Value, 0);
        if (!CGImageDestinationFinalize(destination.Value)) throw new InvalidDataException("ImageIO 预览编码失败。");
        token.ThrowIfCancellationRequested();
        var result = new byte[checked((int)CFDataGetLength(data.Value))];
        Marshal.Copy(CFDataGetBytePtr(data.Value), result, 0, result.Length);
        return result;
    }, token);
    private static ImageCompressionSource Inspect(string path, nint source, CancellationToken token)
    {
        var index = CGImageSourceGetPrimaryImageIndex(source);
        using var properties = new Reference(CGImageSourceCopyPropertiesAtIndex(source, index, 0));
        if (properties.IsInvalid) throw new InvalidDataException("ImageIO 无法读取图片信息。");
        var width = Number(properties.Value, "kCGImagePropertyPixelWidth");
        var height = Number(properties.Value, "kCGImagePropertyPixelHeight");
        var orientation = Number(properties.Value, "kCGImagePropertyOrientation");
        if (orientation is >= 5 and <= 8) (width, height) = (height, width);
        if (width < 1 || height < 1) throw new InvalidDataException("图片尺寸无效。");
        var depth = Math.Max(8, Number(properties.Value, "kCGImagePropertyDepth"));
        token.ThrowIfCancellationRequested();
        return new(Path.GetFullPath(path), new FileInfo(path).Length, width, height, depth,
            depth > 8 ? "rgba64be" : "rgba", orientation > 1);
    }
    private static int Number(nint properties, string name)
    {
        var value = CFDictionaryGetValue(properties, Symbol(ImageLibrary.Value, name));
        return value != 0 && CFNumberGetValue(value, 3, out var number) ? number : 0;
    }
    private static Reference Open(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        using var url = new Reference(CFURLCreateFromFileSystemRepresentation(0, bytes, bytes.Length, false));
        var source = new Reference(CGImageSourceCreateWithURL(url.Value, 0));
        if (source.IsInvalid) { source.Dispose(); throw new InvalidDataException("ImageIO 无法打开这张图片。"); }
        return source;
    }
    private static nint Symbol(nint library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    private static nint True => Symbol(CoreLibrary.Value, "kCFBooleanTrue");
    private sealed class Reference : SafeHandleZeroOrMinusOneIsInvalid
    {
        public nint Value => handle;
        public Reference(nint value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() { CFRelease(handle); return true; }
    }
    [DllImport(Core)] private static extern void CFRelease(nint value);
    [DllImport(Core)] private static extern nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] path, nint length, [MarshalAs(UnmanagedType.I1)] bool directory);
    [DllImport(Core)] private static extern nint CFNumberCreate(nint allocator, int type, ref int value);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(nint number, int type, out int value);
    [DllImport(Core)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    [DllImport(Core)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(Core)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(Core)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(Core)] private static extern nint CFDataCreateMutable(nint allocator, nint capacity);
    [DllImport(Core)] private static extern nint CFDataGetLength(nint data);
    [DllImport(Core)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(ImageIO)] private static extern nint CGImageSourceCreateWithURL(nint url, nint options);
    [DllImport(ImageIO)] private static extern nuint CGImageSourceGetPrimaryImageIndex(nint source);
    [DllImport(ImageIO)] private static extern nint CGImageSourceCopyPropertiesAtIndex(nint source, nuint index, nint options);
    [DllImport(ImageIO)] private static extern nint CGImageSourceCreateThumbnailAtIndex(nint source, nuint index, nint options);
    [DllImport(ImageIO)] private static extern nint CGImageDestinationCreateWithData(nint data, nint type, nuint count, nint options);
    [DllImport(ImageIO)] private static extern void CGImageDestinationAddImage(nint destination, nint image, nint properties);
    [DllImport(ImageIO)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGImageDestinationFinalize(nint destination);
}
