using System.Globalization;
using System.Text.Json;
using SkiaSharp;

namespace AvaMedia.Core;

// CellWidth/CellHeight bound the long/short edges; each video's display ratio determines the actual cell size.
public sealed record ContactSheetOptions(int Columns = 3, int Rows = 3, int CellWidth = 320, int CellHeight = 180, int SheetsPerVideo = 1, string Format = "jpg", bool Timestamps = true, double StartSeconds = 0, double EndSeconds = 0);
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
        if (o.Format is not ("jpg" or "png")) throw new ArgumentException("截图格式须为 JPG 或 PNG。");
        if (!double.IsFinite(o.StartSeconds) || !double.IsFinite(o.EndSeconds) || o.StartSeconds < 0 || o.EndSeconds < 0 || o.EndSeconds > 0 && o.EndSeconds <= o.StartSeconds)
            throw new ArgumentException("结束时间必须晚于开始时间；结束为 0 表示视频末尾。");
    }

    public static async Task<string[]> GenerateContactSheets(IMediaEngine engine, string input, string outputFolder, ContactSheetOptions options, IProgress<ContactSheetProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateContactSheet(options);
        var info = await engine.Probe(input, ct);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new InvalidDataException("文件不含可截图的视频或无法读取时长。");
        var (cellWidth, cellHeight) = ContactSheetCellSize(info, options);
        var (videoDuration, frameRate) = VideoTiming(info);
        var end = options.EndSeconds > 0 ? Math.Min(options.EndSeconds, videoDuration) : videoDuration;
        if (options.StartSeconds >= end) throw new ArgumentException("开始时间超出视频时长。");
        Directory.CreateDirectory(outputFolder);
        var scratch = Path.Combine(Path.GetTempPath(), "AvaMedia-contactsheet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var outputs = new List<string>();
        var count = checked(options.Rows * options.Columns);
        var total = checked(count * options.SheetsPerVideo);
        try
        {
            for (var sheet = 0; sheet < options.SheetsPerVideo; sheet++)
            {
                for (var cell = 0; cell < count; cell++)
                {
                    ct.ThrowIfCancellationRequested();
                    var index = sheet * count + cell;
                    var requested = options.StartSeconds + (end - options.StartSeconds) * (index + .5) / total;
                    // Even very short/low-frame-rate clips must fill every cell: avoid seeking past their last frame.
                    var seconds = Math.Min(requested, Math.Max(0, videoDuration - Math.Max(.08, 1 / frameRate)));
                    var frame = Path.Combine(scratch, $"frame-{cell:0000}.png");
                    // Dimensions already match display aspect, including pixel shape and autorotation.
                    // Scale to the whole cell: no letterboxing, crop, gutter or outer border.
                    var vf = $"scale={cellWidth}:{cellHeight},setsar=1";
                    var captured = await ProcessRunner.Run(engine.FFmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", MediaEngine.Number(seconds), "-i", input, "-map", $"0:v:{info.VideoStreamIndex}", "-an", "-frames:v", "1", "-vf", vf, "-update", "1", frame], ct);
                    if (captured.ExitCode != 0 || !File.Exists(frame) || new FileInfo(frame).Length == 0) throw new InvalidOperationException("抽帧失败：" + captured.Error);
                    if (options.Timestamps)
                        await Task.Run(() => DrawContactSheetTimestamp(frame, seconds, ct), ct).ConfigureAwait(false);
                    progress?.Report(new(input, sheet + 1, (index + 1) * 95d / total, $"抽帧 {index + 1}/{total}"));
                }
                var target = MediaEngine.UniqueOutput(outputFolder, Path.GetFileNameWithoutExtension(input) + $"-grid-{options.Columns}x{options.Rows}-{sheet + 1:000}", options.Format);
                var stagedOutput = Path.Combine(scratch, "sheet." + options.Format);
                List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-framerate", "1", "-start_number", "0", "-i", Path.Combine(scratch, "frame-%04d.png"), "-vf", $"tile={options.Columns}x{options.Rows}:nb_frames={count}:padding=0:margin=0", "-frames:v", "1", "-update", "1"];
                if (options.Format == "jpg") args.AddRange(["-q:v", "2"]);
                args.Add(stagedOutput);
                var assembled = await ProcessRunner.Run(engine.FFmpeg, args, ct);
                if (assembled.ExitCode != 0 || !File.Exists(stagedOutput)) throw new InvalidOperationException("拼图失败：" + assembled.Error);
                ct.ThrowIfCancellationRequested();
                File.Move(stagedOutput, target, false);
                outputs.Add(target);
                progress?.Report(new(input, sheet + 1, (sheet + 1) * 100d / options.SheetsPerVideo, "已生成：" + target));
            }
            return outputs.ToArray();
        }
        finally
        {
            // This directory is a newly allocated task scratch path, never an input/output folder.
            try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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
                    if (Math.Abs(rotation) % 180 == 90) (width, height) = (height, width);
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

    private static void DrawContactSheetTimestamp(string frame, double seconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var bitmap = SKBitmap.Decode(frame) ?? throw new InvalidDataException("无法读取截图画面。");
        using var canvas = new SKCanvas(bitmap);
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var textPaint = new SKPaint { Typeface = typeface, IsAntialias = true, Color = SKColors.White,
            TextSize = Math.Min(Math.Max(12, bitmap.Width / 22), bitmap.Height / 4) };
        var text = TimeSpan.FromSeconds(seconds).ToString(bitmap.Width >= 140 ? @"hh\:mm\:ss\.fff" : @"hh\:mm\:ss");
        var inset = Math.Min(8, Math.Min(bitmap.Width, bitmap.Height) / 8);
        var border = Math.Min(4, inset / 2);
        var bounds = new SKRect();
        textPaint.MeasureText(text, ref bounds);
        var availableWidth = bitmap.Width - 2 * (inset + border);
        var availableHeight = bitmap.Height - 2 * (inset + border);
        var scale = Math.Min(1, Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height));
        textPaint.TextSize *= scale;
        textPaint.MeasureText(text, ref bounds);
        var x = bitmap.Width - inset - border - bounds.Right;
        var y = bitmap.Height - inset - border - bounds.Bottom;
        using var background = new SKPaint { Color = new SKColor(0, 0, 0, 166) };
        canvas.DrawRect(new SKRect(x + bounds.Left - border, y + bounds.Top - border,
            x + bounds.Right + border, y + bounds.Bottom + border), background);
        canvas.DrawText(text, x, y, textPaint);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("截图时间戳编码失败。");
        ct.ThrowIfCancellationRequested();
        using var output = File.Create(frame);
        data.SaveTo(output);
    }

    internal static (double Duration, double FrameRate) VideoTiming(MediaInfo info)
    {
        double duration = info.Duration, frameRate = 25;
        try
        {
            using var json = JsonDocument.Parse(info.RawJson);
            var video = json.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
            if (video.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var streamDuration) && streamDuration > 0)
                duration = Math.Min(duration, streamDuration);
            if (video.TryGetProperty("avg_frame_rate", out var rate))
            {
                var parts = (rate.GetString() ?? "").Split('/');
                if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && numerator > 0 && denominator > 0)
                    frameRate = numerator / denominator;
            }
        }
        catch (JsonException) { }
        return (duration, frameRate);
    }
}
