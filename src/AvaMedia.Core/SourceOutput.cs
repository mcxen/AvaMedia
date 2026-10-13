namespace AvaMedia.Core;

/// <summary>Resolve a shared source-format selection separately for every output file.</summary>
public static class SourceOutput
{
    public const string Original = "原格式";
    public static string[] CompressionVideoFormats { get; } = ["mp4", "mov", "m4v", "mkv", "ts", "mts", "m2ts", "m2t"];
    public static string[] CompressionImageFormats { get; } = ["jpg", "jpeg", "png", "webp", "bmp"];
    public static string[] ImageEncodingFormats { get; } = ["jpg", "jpeg", "png", "webp", "tif", "tiff", "bmp", "avif"];

    public static string VideoFormat(string selection, string? path, bool compression = false)
    {
        if (selection != Original) return selection;
        var extension = Extension(path);
        return (compression ? CompressionVideoFormats.Contains(extension) : VideoFormats.OriginalOutputExtensions.Contains(extension))
            ? extension : "mp4";
    }

    public static string ImageFormat(string selection, string? path, bool compression = false)
    {
        if (selection != Original) return selection;
        var extension = Extension(path);
        return (compression ? CompressionImageFormats : ImageEncodingFormats).Contains(extension) ? extension : "png";
    }

    public static ConversionOptions Resolve(Feature feature, ConversionOptions options, string? path)
    {
        if (options.Format is SourceVideoExport.Original or SourceVideoExport.FastRotation)
            return SourceVideoExport.WithOutput(options, options.Format, path ?? "");
        var result = options.Clone();
        if (feature.Operation == Operation.VideoCompress) result.VideoCompression ??= new() { Format = result.Format };
        if (feature.Operation == Operation.VideoSlim) result.VideoSlimming ??= new() { Format = result.Format };
        if (feature.Operation == Operation.ImageCompress) result.ImageCompression ??= new() { Format = result.Format };
        if (result.VideoCompression is { } compression)
        {
            result.Format = VideoFormat(compression.Format == Original ? result.Format : compression.Format, path, compression: true);
            result.VideoCompression = compression with { Format = result.Format };
        }
        else if (result.VideoSlimming is { } slimming)
        {
            result.Format = VideoFormat(slimming.Format == Original ? result.Format : slimming.Format, path, compression: true);
            result.VideoSlimming = slimming with { Format = result.Format };
        }
        else if (result.ImageCompression is { } image)
        {
            result.Format = ImageFormat(image.Format == Original ? result.Format : image.Format, path, compression: true);
            result.ImageCompression = image with { Format = result.Format };
        }
        else if (result.Format == Original)
        {
            result.Format = feature.Category == "图片" ? ImageFormat(Original, path) :
                feature.Operation == Operation.Transcribe && (result.Transcription?.RecognitionOnly == true || !VideoFormats.IsVideo(path ?? "")) ? "srt" : VideoFormat(Original, path);
        }
        return result;
    }

    private static string Extension(string? path) => Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant();
}
