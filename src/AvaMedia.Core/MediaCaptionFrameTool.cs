using System.Text.Json;
using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>Read-only frame access to one selected video. The model never chooses a file, command, or URL.</summary>
public sealed class MediaCaptionFrameTool
{
    private readonly IMediaEngine _engine;
    private readonly MediaTagResult _source;
    private readonly int _videoStreamIndex;
    private readonly Action<byte[], string>? _preview;
    private int _remaining = 8;
    public SummaryModelTool Tool { get; }

    public MediaCaptionFrameTool(IMediaEngine engine, MediaTagResult source, int videoStreamIndex = 0,
        Action<byte[], string>? preview = null)
    {
        if (!double.IsFinite(source.DurationSeconds) || source.DurationSeconds <= 0 || videoStreamIndex < 0)
            throw new ArgumentException("视频描述采样时间无效。");
        _engine = engine; _source = source; _videoStreamIndex = videoStreamIndex; _preview = preview;
        var parameters = JsonSerializer.SerializeToElement(new
        {
            type = "object", additionalProperties = false,
            properties = new
            {
                seconds = new { type = "array", minItems = 1, maxItems = 4,
                    items = new { type = "number", minimum = 0, maximum = source.DurationSeconds },
                    description = "需查看的采样时间，单位秒。每次 1–4 个；最多补充 8 帧。" },
                region = new { type = "object", additionalProperties = false,
                    description = "可选局部放大区域，相对完整画面的归一化坐标。省略时返回完整画面。",
                    properties = new { x = new { type = "number", minimum = 0, maximum = 1 },
                        y = new { type = "number", minimum = 0, maximum = 1 },
                        width = new { type = "number", minimum = .02, maximum = 1 },
                        height = new { type = "number", minimum = .02, maximum = 1 } },
                    required = new[] { "x", "y", "width", "height" } }
            }, required = new[] { "seconds" }
        });
        Tool = new("get_video_frames", "查看当前视频指定时刻的实际画面，用于核对动作、遮挡或局部细节。仅访问当前视频，不能推断采样间发生的内容。",
            parameters, ExecuteAsync);
    }

    private async Task<SummaryModelToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(property => property.Name is not ("seconds" or "region"))
            || !arguments.TryGetProperty("seconds", out var times) || times.ValueKind != JsonValueKind.Array || times.GetArrayLength() is < 1 or > 4)
            throw new ArgumentException("补帧参数须包含 1–4 个有效时间点。");
        var seconds = times.EnumerateArray().Select(ReadNumber).ToArray();
        if (seconds.Any(time => time < 0 || time > _source.DurationSeconds))
            throw new ArgumentException("补帧时间须在视频时长范围内。");
        SKRect? region = null;
        if (arguments.TryGetProperty("region", out var crop))
        {
            if (crop.ValueKind != JsonValueKind.Object || crop.EnumerateObject().Count() != 4
                || !crop.TryGetProperty("x", out var x) || !crop.TryGetProperty("y", out var y)
                || !crop.TryGetProperty("width", out var width) || !crop.TryGetProperty("height", out var height))
                throw new ArgumentException("补帧区域须包含 x、y、width、height。");
            var left = ReadNumber(x); var top = ReadNumber(y); var w = ReadNumber(width); var h = ReadNumber(height);
            if (left < 0 || top < 0 || w < .02 || h < .02 || left + w > 1 || top + h > 1)
                throw new ArgumentException("补帧区域须在画面内，宽高至少为画面的 2%。");
            region = new((float)left, (float)top, (float)(left + w), (float)(top + h));
        }
        if (seconds.Length > _remaining) return new("补帧额度已用完。请依据已看到的画面描述，省略无法确认的细节。");
        _remaining -= seconds.Length;
        var images = new List<SummaryModelImage>();
        var metadata = new List<object>();
        foreach (var time in seconds)
        {
            ct.ThrowIfCancellationRequested();
            MediaTagService.ValidateSource(_source);
            // Resolve the actual preceding frame timestamp, including the exact video end.
            var sample = time == 0 ? 0 : await _engine.AdjacentFrameTime(_source.Path, time + .000001, -1, ct,
                _videoStreamIndex).ConfigureAwait(false);
            var size = region is null ? 1024 : 1920;
            var png = await _engine.Thumbnail(_source.Path, Math.Max(0, sample - .000001), size, size, ct, pad: false,
                videoStreamIndex: _videoStreamIndex).ConfigureAwait(false);
            MediaTagService.ValidateSource(_source);
            if (region is { } rect) png = Crop(png, rect);
            var label = "补充画面 · " + MediaTime.Format(sample) + (region is { } area
                ? FormattableString.Invariant($" · 局部 x={area.Left:0.###}, y={area.Top:0.###}, width={area.Width:0.###}, height={area.Height:0.###}") : " · 完整画面");
            images.Add(new(label, png)); metadata.Add(new { seconds = sample, label }); _preview?.Invoke(png, label);
        }
        return new(JsonSerializer.Serialize(new { frames = metadata, remainingFrames = _remaining,
            note = "对应实际图像附在后续用户消息中；只将可见内容作为证据。" }), images);
    }

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new ArgumentException("补帧参数须为有限数值。");
        return number;
    }

    private static byte[] Crop(byte[] png, SKRect region)
    {
        using var image = SKBitmap.Decode(png) ?? throw new InvalidDataException("无法解码补充画面。");
        var pixels = new SKRectI(Math.Clamp((int)Math.Floor(region.Left * image.Width), 0, image.Width - 1),
            Math.Clamp((int)Math.Floor(region.Top * image.Height), 0, image.Height - 1),
            Math.Clamp((int)Math.Ceiling(region.Right * image.Width), 1, image.Width),
            Math.Clamp((int)Math.Ceiling(region.Bottom * image.Height), 1, image.Height));
        using var cropped = new SKBitmap();
        if (!image.ExtractSubset(cropped, pixels)) throw new InvalidDataException("无法裁剪补充画面。");
        using var encoded = cropped.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
