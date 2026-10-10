using System.Text.Json;

namespace AvaMedia.Core;

public sealed record ImageCompressionOptions
{
    public string Format { get; init; } = "webp";
    public int Quality { get; init; } = 82;
    public bool Lossless { get; init; }
    public int MaxDimension { get; init; }
    public void Validate()
    {
        if (Format is not ("jpg" or "webp" or "png")) throw new ArgumentException("请选择 JPEG、WebP 或 PNG 输出。");
        if (Quality is < 1 or > 100) throw new ArgumentException("图片质量须在 1–100 之间。");
        if (MaxDimension is < 0 or > 32768) throw new ArgumentException("最长边须在 1–32768 像素之间，或选择保持原尺寸。");
        if (Lossless && Format == "jpg") throw new ArgumentException("JPEG 不支持无损编码，请选择 WebP 或 PNG。");
    }
}

public sealed record ImageCompressionSource(string Path, long Bytes, int Width, int Height, int BitDepth = 8,
    string PixelFormat = "rgba", bool HasOrientation = false, string InputSpecifier = "v:0", string PreFilter = "", string Codec = "");
public sealed record ImageCompressionResult(string Path, long SourceBytes, long OutputBytes, int Width, int Height)
{
    public bool IsSmaller => OutputBytes < SourceBytes;
    public double SavedPercent => SourceBytes > 0 ? (1 - (double)OutputBytes / SourceBytes) * 100 : 0;
}
public sealed record ImageCompressionRequest(string[] Inputs, ImageCompressionOptions Options, string OutputFolder,
    bool OutputToSource = false, bool StartImmediately = false);

/// <summary>The UI and persisted queue use the same actual image encoder.</summary>
public interface IImageCompressor
{
    Task<ImageCompressionSource> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task<ImageCompressionResult> CompressAsync(string input, string output, ImageCompressionOptions options,
        CancellationToken cancellationToken = default, bool keepLargerPreview = false);
}

public static class ImageCompression
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".heic" or ".heif";
    public static (int Width, int Height) Size(ImageCompressionSource source, ImageCompressionOptions options)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (options.MaxDimension == 0 || longest <= options.MaxDimension) return (source.Width, source.Height);
        var scale = (double)options.MaxDimension / longest;
        return (Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }
    public static string Bytes(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:0.##} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    public static IReadOnlyList<Job> CreateJobs(ImageCompressionRequest request, IEnumerable<string>? reserved = null)
    {
        request.Options.Validate();
        if (request.Inputs.Length == 0) throw new ArgumentException("请添加要压缩的图片。");
        if (string.IsNullOrWhiteSpace(request.OutputFolder) && !request.OutputToSource) throw new ArgumentException("请选择保存文件夹。");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var inputs = request.Inputs.Select(Path.GetFullPath).Distinct(comparer).ToArray();
        foreach (var input in inputs)
        {
            if (!Supports(input)) throw new ArgumentException("支持静态 HEIC / HEIF、JPEG、PNG、WebP 和 BMP 图片。");
            if (!File.Exists(input)) throw new FileNotFoundException("图片不存在。", input);
        }
        var used = new HashSet<string>(reserved ?? [], comparer);
        used.UnionWith(inputs);
        var jobs = new List<Job>();
        foreach (var input in inputs)
        {
            var folder = request.OutputToSource ? Path.GetDirectoryName(input)! : Path.GetFullPath(request.OutputFolder);
            var output = MediaEngine.UniqueOutput(folder, Path.GetFileNameWithoutExtension(input) + "_compressed", request.Options.Format, used);
            jobs.Add(new() { FeatureId = "image-compress", Inputs = [input], Output = output,
                Options = new() { Format = request.Options.Format, ImageCompression = request.Options } });
            used.Add(output);
        }
        return jobs;
    }
}

public sealed class ImageCompressor(IMediaEngine engine) : IImageCompressor
{
    public async Task<ImageCompressionSource> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!ImageCompression.Supports(path)) throw new ArgumentException("支持静态 HEIC / HEIF、JPEG、PNG、WebP 和 BMP 图片。");
        if (HeifImage.Supports(path))
            return OperatingSystem.IsMacOS() ? await AppleImageIO.InspectAsync(path, cancellationToken).ConfigureAwait(false) :
                await InspectHeifAsync(path, cancellationToken).ConfigureAwait(false);
        return await ImageCodec.InspectStaticAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImageCompressionSource> InspectHeifAsync(string path, CancellationToken token)
    {
        var info = await engine.Probe(path, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(info.RawJson);
        var primary = HeifImage.Read(json.RootElement);
        if (primary.Width < 1 || primary.Height < 1 || info.HasAudio) throw new InvalidDataException("HEIC / HEIF 主图无效。");
        return new(Path.GetFullPath(path), new FileInfo(path).Length, primary.Width, primary.Height,
            primary.BitDepth, primary.PixelFormat, primary.HasOrientation, primary.Specifier, primary.PreFilter);
    }

    public async Task<ImageCompressionResult> CompressAsync(string input, string output, ImageCompressionOptions options,
        CancellationToken cancellationToken = default, bool keepLargerPreview = false)
    {
        options.Validate();
        var source = HeifImage.Supports(input) ? await InspectHeifAsync(input, cancellationToken).ConfigureAwait(false) :
            await InspectAsync(input, cancellationToken).ConfigureAwait(false);
        if (source.BitDepth > 8 && options.Lossless && options.Format == "webp")
            throw new ArgumentException("WebP 无损编码不能保留高于 8 位的通道，请选择 PNG 无损输出。");
        output = Path.GetFullPath(output);
        if (string.Equals(source.Path, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("压缩输出不能覆盖原图。");
        if (File.Exists(output)) throw new IOException("输出文件已存在，请选择新的名称。");
        // Q8 handles ordinary still images locally. Keep the complete HEIF primary/grid decode and
        // high-depth PNG path, where an 8-bit image backend would discard source precision.
        if (!HeifImage.Supports(input) && !(source.BitDepth > 8 && options.Format == "png"))
        {
            var encoded = await ImageCodec.ExportAsync(new(source.Path), output,
                new ImageEncodingOptions(options.Format, options.Quality, options.MaxDimension,
                    StripMetadata: true, Lossless: options.Lossless), cancellationToken,
                smallerThan: keepLargerPreview ? null : source.Bytes).ConfigureAwait(false);
            return new(output, source.Bytes, encoded.Bytes, encoded.Width, encoded.Height);
        }
        var folder = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".AvaMedia-image-" + Guid.NewGuid().ToString("N") + "." + options.Format);
        var decoded = options.Format == "webp" ? temporary + ".png" : null;
        var size = ImageCompression.Size(source, options);
        if (options.Format == "webp" && Math.Max(size.Width, size.Height) > 16383)
            throw new ArgumentException("WebP 最长边最多 16383 像素；请缩小尺寸或选择 PNG / JPEG。");
        try
        {
            var args = Arguments(source, size, decoded ?? temporary, options);
            var encoded = await ProcessRunner.Run(engine.FFmpeg, args, cancellationToken).ConfigureAwait(false);
            if (encoded.ExitCode != 0) throw new InvalidOperationException("图片编码失败：" + encoded.Error);
            if (decoded is not null)
                await ImageCodec.ExportAsync(new(decoded), temporary,
                    new ImageEncodingOptions("webp", options.Quality, StripMetadata: true, Lossless: options.Lossless), cancellationToken).ConfigureAwait(false);
            var length = new FileInfo(temporary).Length;
            if (length == 0) throw new InvalidDataException("图片编码没有生成有效文件。");
            var result = new ImageCompressionResult(output, source.Bytes, length, size.Width, size.Height);
            if (!result.IsSmaller && !keepLargerPreview)
                throw new InvalidOperationException($"当前设置未压小：原图 {ImageCompression.Bytes(source.Bytes)}，编码后 {ImageCompression.Bytes(length)}。已保留原图，未写入输出；可降低质量、缩小尺寸或改用 WebP。");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output); // No overwrite; the original is never the destination.
            return result;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { if (decoded is not null && File.Exists(decoded)) File.Delete(decoded); }
        }
    }

    private static List<string> Arguments(ImageCompressionSource source, (int Width, int Height) size,
        string output, ImageCompressionOptions options)
    {
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin", "-n", "-filter_complex_threads", "1"];
        if (source.PreFilter.Length > 0) args.Add("-noautorotate");
        args.AddRange(["-i", source.Path]);
        var prefix = source.PreFilter.Length > 0 ? source.PreFilter + "," : "";
        var resized = size.Width != source.Width || size.Height != source.Height;
        var scale = resized ? $",scale={size.Width}:{size.Height}:flags=lanczos" : "";
        if (options.Format == "jpg")
        {
            // Explicit white compositing keeps transparent input from becoming black JPEG pixels.
            args.AddRange(["-filter_complex",
                $"[0:{source.InputSpecifier}]{prefix}format=rgba,split[fg][base];[base]lutrgb=r=255:g=255:b=255:a=255[bg];[bg][fg]overlay=shortest=1:format=auto{scale},format=yuvj444p[out]", "-map", "[out]"]);
        }
        else
        {
            var pixels = options.Format == "webp" ? "rgba" : PngPixels(source, resized);
            HeifImage.AppendVideo(args, HeifImage.Map(source.InputSpecifier), $"{prefix}format={pixels}{scale}");
            args.AddRange(["-filter_threads", "1"]);
        }
        args.AddRange(["-an", "-sn", "-dn", "-frames:v", "1", "-map_metadata", "-1", "-threads", "2"]);
        if (options.Format == "jpg") args.AddRange(["-c:v", "mjpeg", "-q:v", MediaEngine.Number(2 + (100 - options.Quality) * 29d / 99)]);
        else if (options.Format == "png") args.AddRange(["-c:v", "png", "-compression_level", "9", "-pred", "mixed"]);
        // HEIF and high-depth PNG retain their specialized media decode; WebP uses the shared image encoder.
        else args.AddRange(["-c:v", "png", "-compression_level", "1"]);
        args.Add(output);
        return args;
    }

    private static string PngPixels(ImageCompressionSource source, bool resized) => source.PixelFormat switch
    {
        "rgb24" or "rgba" or "rgb48be" or "rgba64be" or "gray" or "ya8" or "gray16be" or "ya16be" => source.PixelFormat,
        "rgb48le" => "rgb48be", "rgba64le" => "rgba64be", "gray16le" => "gray16be", "ya16le" => "ya16be",
        "pal8" when !resized => "pal8",
        _ => source.BitDepth > 8 ? "rgba64be" : "rgba"
    };

}
