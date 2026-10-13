using System.Globalization;
using System.Text.Json;
using SkiaSharp;

namespace AvaMedia.Core;

// CellWidth/CellHeight bound the long/short edges; each video's display ratio determines the actual cell size.
public sealed record ContactSheetOptions(int Columns = 3, int Rows = 3, int CellWidth = 320, int CellHeight = 180, int SheetsPerVideo = 1, string Format = "jpg", bool Timestamps = true, double StartSeconds = 0, double EndSeconds = 0, string Font = "default", string InterfaceFont = "");
public sealed record ContactSheetProgress(string Input, int Sheet, double Percent, string Message);

/// <summary>Video collection and contact-sheet generation.</summary>
public static class BatchVideoTools
{
    public static string[] CollectVideos(IEnumerable<string> paths, bool recursive)
    {
        var result = new HashSet<string>(BatchRename.PathComparer);
        foreach (var input in paths)
        {
            var path = Path.GetFullPath(input);
            if (File.Exists(path))
            {
                if (VideoFormats.IsVideo(path)) result.Add(path);
            }
            else if (Directory.Exists(path))
            {
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden
                };
                foreach (var file in Directory.EnumerateFiles(path, "*", enumeration))
                    if (VideoFormats.IsVideo(file)) result.Add(Path.GetFullPath(file));
            }
        }
        return result.OrderBy(p => p, BatchRename.PathComparer).ToArray();
    }

    public static void ValidateContactSheet(ContactSheetOptions o)
    {
        if (o.Columns is < 1 or > 10 || o.Rows is < 1 or > 10 || o.SheetsPerVideo is < 1 or > 100) throw new ArgumentException("行列数须在 1–10，每个视频拼图数量须在 1–100。");
        if (Math.Min(o.CellWidth, o.CellHeight) < 64 || Math.Max(o.CellWidth, o.CellHeight) > 1920 || Math.Min(o.CellWidth, o.CellHeight) > 1080 || (long)o.CellWidth * o.CellHeight * o.Rows * o.Columns > 40000000)
            throw new ArgumentException("单格长边上限须在 64–1920，短边上限须在 64–1080；整张拼图不能超过 4000 万像素。");
        if (o.Font is not ("default" or "inter" or "noto-sans-sc" or "fusion-pixel")) throw new ArgumentException("请选择内置文字字体或默认字体。");
        if (o.Format is not ("jpg" or "png")) throw new ArgumentException("截图格式须为 JPG 或 PNG。");
        if (!double.IsFinite(o.StartSeconds) || !double.IsFinite(o.EndSeconds) || o.StartSeconds < 0 || o.EndSeconds < 0 || o.EndSeconds > 0 && o.EndSeconds <= o.StartSeconds)
            throw new ArgumentException("结束时间必须晚于开始时间；结束为 0 表示视频末尾。");
    }

    public static async Task<byte[]> PreviewContactSheetAsync(IMediaEngine engine, string input, ContactSheetOptions options, CancellationToken token)
    {
        var info = await ContactSheetInfo(engine, input, options, token).ConfigureAwait(false);
        var (width, height) = ContactSheetCellSize(info, options);
        var scale = Math.Min(1, 900d / Math.Max(width * options.Columns, height * options.Rows));
        using var bitmap = await RenderContactSheet(engine, input, info, options, 0,
            Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)), null, token).ConfigureAwait(false);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("截图编码失败。");
        token.ThrowIfCancellationRequested();
        return encoded.ToArray();
    }

    public static async Task<string[]> GenerateContactSheets(IMediaEngine engine, string input, string outputFolder, ContactSheetOptions options, IProgress<ContactSheetProgress>? progress = null, CancellationToken ct = default)
    {
        var info = await ContactSheetInfo(engine, input, options, ct).ConfigureAwait(false);
        var (width, height) = ContactSheetCellSize(info, options);
        Directory.CreateDirectory(outputFolder);
        var outputs = new List<string>();
        for (var sheet = 0; sheet < options.SheetsPerVideo; sheet++)
        {
            using var bitmap = await RenderContactSheet(engine, input, info, options, sheet, width, height, progress, ct).ConfigureAwait(false);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(options.Format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 95)
                ?? throw new InvalidOperationException("拼图编码失败。");
            var target = MediaEngine.UniqueOutput(outputFolder, Path.GetFileNameWithoutExtension(input) + $"-grid-{options.Columns}x{options.Rows}-{sheet + 1:000}", options.Format);
            var staged = Path.Combine(outputFolder, ".AvaMedia-contactsheet-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                ct.ThrowIfCancellationRequested();
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write)) encoded.SaveTo(output);
                ct.ThrowIfCancellationRequested();
                File.Move(staged, target, false);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            outputs.Add(target);
            progress?.Report(new(input, sheet + 1, (sheet + 1) * 100d / options.SheetsPerVideo, "已生成：" + target));
        }
        return outputs.ToArray();
    }

    private static async Task<MediaInfo> ContactSheetInfo(IMediaEngine engine, string input, ContactSheetOptions options, CancellationToken ct)
    {
        ValidateContactSheet(options);
        ct.ThrowIfCancellationRequested();
        var info = await engine.Probe(input, ct).ConfigureAwait(false);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0)
            throw new InvalidDataException("文件不含可截图的视频或无法读取时长。");
        var (duration, _) = VideoTiming(info);
        if (options.StartSeconds >= (options.EndSeconds > 0 ? Math.Min(options.EndSeconds, duration) : duration))
            throw new ArgumentException("开始时间超出视频时长。");
        return info;
    }

    private static async Task<SKBitmap> RenderContactSheet(IMediaEngine engine, string input, MediaInfo info,
        ContactSheetOptions options, int sheet, int width, int height, IProgress<ContactSheetProgress>? progress, CancellationToken ct)
    {
        var (duration, fps) = VideoTiming(info);
        var end = options.EndSeconds > 0 ? Math.Min(options.EndSeconds, duration) : duration;
        var count = options.Columns * options.Rows;
        var total = count * options.SheetsPerVideo;
        using var fonts = new ContactSheetFonts(options);
        var bitmap = new SKBitmap(width * options.Columns, height * options.Rows);
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            for (var cell = 0; cell < count; cell++)
            {
                ct.ThrowIfCancellationRequested();
                var index = sheet * count + cell;
                var requested = options.StartSeconds + (end - options.StartSeconds) * (index + .5) / total;
                // Keep every cell within the final frame, including short and low-frame-rate sources.
                var seconds = Math.Min(requested, Math.Max(0, duration - Math.Max(.08, 1 / fps)));
                var data = await engine.Thumbnail(input, seconds, width, height, ct, pad: false, videoStreamIndex: info.VideoStreamIndex).ConfigureAwait(false);
                using var frame = SKBitmap.Decode(data) ?? throw new InvalidDataException("无法读取截图画面。");
                canvas.Save();
                canvas.Translate(cell % options.Columns * width, cell / options.Columns * height);
                canvas.DrawBitmap(frame, new SKRect(0, 0, width, height));
                if (options.Timestamps) DrawContactSheetTimestamp(canvas, width, height, seconds, fonts, 1);
                canvas.Restore();
                progress?.Report(new(input, sheet + 1, (sheet + (cell + 1) * .95 / count) * 100 / options.SheetsPerVideo, $"抽帧 {index + 1}/{total}"));
            }
            ct.ThrowIfCancellationRequested();
            canvas.Flush();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    /// <summary>Geometry and timing shared by the incremental UI preview and the generated sheets.</summary>
    public static (int Width, int Height, double Duration, double FrameRate) ContactSheetPreviewLayout(MediaInfo info, ContactSheetOptions options)
    {
        ValidateContactSheet(options);
        var (width, height) = ContactSheetCellSize(info, options);
        var (duration, frameRate) = VideoTiming(info);
        return (width, height, duration, frameRate);
    }

    private static (int Width, int Height) ContactSheetCellSize(MediaInfo info, ContactSheetOptions options)
    {
        double width = info.Width, height = info.Height;
        try
        {
            using var json = JsonDocument.Parse(info.RawJson);
            if (json.RootElement.TryGetProperty("streams", out var streams))
            {
                var video = streams.EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "video").ElementAtOrDefault(info.VideoStreamIndex);
                if (video.ValueKind != JsonValueKind.Undefined)
                {
                    // Start from encoded dimensions: MediaInfo has already swapped right-angle rotations.
                    width = video.GetProperty("width").GetInt32(); height = video.GetProperty("height").GetInt32();
                    if (video.TryGetProperty("sample_aspect_ratio", out var sar))
                    {
                        var parts = (sar.GetString() ?? "").Split(':');
                        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0 && double.IsFinite(numerator / denominator))
                            width *= numerator / denominator;
                    }
                    double rotation = 0;
                    if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var tag))
                        double.TryParse(tag.GetString(), CultureInfo.InvariantCulture, out rotation);
                    if (video.TryGetProperty("side_data_list", out var sides))
                        foreach (var side in sides.EnumerateArray())
                            if (side.TryGetProperty("rotation", out var angle)) rotation = angle.GetDouble();
                    if (MediaStreams.IsQuarterTurn(rotation)) (width, height) = (height, width);
                }
            }
        }
        catch (JsonException) { }
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidDataException("无法读取视频画面尺寸。");
        var longEdge = Math.Max(options.CellWidth, options.CellHeight);
        var shortEdge = Math.Min(options.CellWidth, options.CellHeight);
        var maxWidth = width >= height ? longEdge : shortEdge;
        var maxHeight = width >= height ? shortEdge : longEdge;
        var scale = Math.Min(maxWidth / width, maxHeight / height);
        return (Math.Clamp((int)Math.Round(width * scale), 1, maxWidth), Math.Clamp((int)Math.Round(height * scale), 1, maxHeight));
    }

    private static void DrawContactSheetTimestamp(SKCanvas canvas, int width, int height, double seconds, ContactSheetFonts fonts, int textScale)
    {
        using var textPaint = new SKPaint { Typeface = fonts.Primary, IsAntialias = fonts.Antialias, Color = SKColors.White,
            TextSize = Math.Min(Math.Max(12 * textScale, width / 22), height / 4f) };
        var text = TimeSpan.FromSeconds(seconds).ToString(width >= 140 ? @"hh\:mm\:ss\.fff" : @"hh\:mm\:ss");
        var inset = Math.Min(8 * textScale, Math.Min(width, height) / 8);
        var border = Math.Min(4 * textScale, inset / 2);
        var bounds = new SKRect();
        textPaint.MeasureText(text, ref bounds);
        var availableWidth = width - 2 * (inset + border);
        var availableHeight = height - 2 * (inset + border);
        var scale = Math.Min(1, Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height));
        textPaint.TextSize *= scale;
        textPaint.MeasureText(text, ref bounds);
        var x = width - inset - border - bounds.Right;
        var y = height - inset - border - bounds.Bottom;
        using var background = new SKPaint { Color = new SKColor(0, 0, 0, 166) };
        canvas.DrawRect(new SKRect(x + bounds.Left - border, y + bounds.Top - border,
            x + bounds.Right + border, y + bounds.Bottom + border), background);
        canvas.DrawText(text, x, y, textPaint);
    }

    internal static (double Duration, double FrameRate) VideoTiming(MediaInfo info)
    {
        double duration = info.Duration, frameRate = 25;
        try
        {
            using var json = JsonDocument.Parse(info.RawJson);
            var video = json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "video").ElementAtOrDefault(info.VideoStreamIndex);
            if (video.ValueKind == JsonValueKind.Undefined) return (duration, frameRate);
            var streamDuration = MediaStreams.StreamDuration(video);
            if (streamDuration > 0)
                duration = Math.Min(duration, streamDuration);
            var rate = MediaStreams.FrameRate(video);
            if (rate > 0) frameRate = rate;
        }
        catch (JsonException) { }
        return (duration, frameRate);
    }
}
