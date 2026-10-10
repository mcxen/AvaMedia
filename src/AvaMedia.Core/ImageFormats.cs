using System.Collections.Frozen;

namespace AvaMedia.Core;

/// <summary>Still/animated images are not video samples, even when their extension is incorrect.</summary>
public static class ImageFormats
{
    public static IReadOnlySet<string> Extensions { get; } = new[]
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "apng", "gif", "webp", "avif", "heic", "heif", "bmp", "dib",
        "tif", "tiff", "ico", "cur", "psd", "psb", "dds", "jxr", "wdp", "hdp", "j2k", "jp2", "jpc", "jpf",
        "tga", "pcx", "pgm", "pnm", "ppm", "pbm", "pam", "bpg", "jxl", "qoi", "exr", "hdr", "svg",
        "dng", "cr2", "cr3", "crw", "nef", "nrw", "orf", "rw2", "pef", "sr2", "arw", "raf", "srw", "3fr"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public static bool Supports(string path) => Extensions.Contains(Path.GetExtension(path).TrimStart('.')) || HasImageSignature(path);
    public static bool HasImageSignature(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[32];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var count = stream.Read(header); var bytes = header[..count];
            if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) || bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)
                || bytes.StartsWith("BM"u8) || bytes.StartsWith("II*\0"u8) || bytes.StartsWith("MM\0*"u8)
                || bytes.StartsWith("8BPS"u8) || bytes.StartsWith("qoif"u8)) return true;
            // Raw Motion JPEG streams also begin with a JPEG frame.
            if (count >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
                return Path.GetExtension(path).ToLowerInvariant() is not (".mjpeg" or ".mjpg");
            if (count >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return true;
            if (count >= 12 && bytes[4..8].SequenceEqual("ftyp"u8))
                return bytes[8..12].SequenceEqual("heic"u8) || bytes[8..12].SequenceEqual("heix"u8)
                    || bytes[8..12].SequenceEqual("mif1"u8) || bytes[8..12].SequenceEqual("avif"u8);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }
}
