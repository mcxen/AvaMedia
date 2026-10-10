using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace AvaMedia.Core;

public sealed record OutfitAppearanceFrame(double Seconds, int Pixels, double Confidence, double[] Colors)
{
    public double[] Embedding { get; init; } = [];
    public string Encoder { get; init; } = "";
}

/// <summary>Local clothing masks supply color evidence without face, hair or room colors.</summary>
public sealed class OutfitAppearanceService : IDisposable
{
    private const int Size = 256;
    public const string Encoder = "dinov2-clothing-cls";
    private readonly IMediaEngine _engine;
    private readonly ModelLease _lease;
    private readonly ModelInferenceSession _session;
    private readonly ModelLease _featureLease;
    private readonly ModelInferenceSession _features;

    private OutfitAppearanceService(IMediaEngine engine, ModelLease lease, ModelLease featureLease)
    {
        _engine = engine; _lease = lease; _featureLease = featureLease;
        try
        {
            _session = new(Path.Combine(lease.Directory, ModelCatalog.OutfitFile), ModelCatalog.Find(ModelCatalog.OutfitId).Files[0].Sha256, false, 1);
            try { _features = new(Path.Combine(featureLease.Directory, ModelCatalog.OutfitFile), ModelCatalog.Find(ModelCatalog.OutfitFeaturesId).Files[0].Sha256, false, 1); }
            catch { _session.Dispose(); throw; }
        }
        catch { lease.Dispose(); featureLease.Dispose(); throw; }
    }
    public static async Task<OutfitAppearanceService> CreateAsync(IMediaEngine engine, ModelStore store, CancellationToken ct)
    {
        var lease = await store.AcquireAsync(ModelCatalog.OutfitId, ct).ConfigureAwait(false);
        ModelLease features;
        try { features = await store.AcquireAsync(ModelCatalog.OutfitFeaturesId, ct).ConfigureAwait(false); }
        catch { lease.Dispose(); throw; }
        return new(engine, lease, features);
    }

    public static bool IsComplete(MediaTagResult media)
    {
        var times = VideoFormats.IsVideo(media.Path) ? media.Frames.Select(frame => frame.Seconds).ToArray() : [0d];
        return times.Length > 0 && media.OutfitFrames.Count == times.Length
            && times.All(time => media.OutfitFrames.Any(frame => Math.Abs(frame.Seconds - time) < .001
                && frame.Encoder == Encoder && frame.Colors.Length == 12 && frame.Embedding.Length == 384
                && frame.Colors.All(value => double.IsFinite(value) && value is >= 0 and <= 1)
                && frame.Embedding.All(double.IsFinite)));
    }

    public async Task<MediaTagResult> AnalyzeAsync(MediaTagResult media, Action<int, int>? progress, CancellationToken ct)
    {
        MediaTagService.ValidateSource(media);
        var times = VideoFormats.IsVideo(media.Path) ? media.Frames.Select(frame => frame.Seconds).ToArray() : [0d];
        if (times.Length == 0) throw new InvalidDataException("服装采样标签缺失");
        var frames = new List<OutfitAppearanceFrame>();
        foreach (var time in times)
        {
            ct.ThrowIfCancellationRequested();
            var image = await _engine.Thumbnail(media.Path, time, 768, 768, ct, pad: false).ConfigureAwait(false);
            frames.Add(Predict(image, time, ct)); progress?.Invoke(frames.Count, times.Length);
        }
        MediaTagService.ValidateSource(media);
        return media with { OutfitFrames = frames };
    }

    private OutfitAppearanceFrame Predict(byte[] encoded, double seconds, CancellationToken ct)
    {
        using var source = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("服装图片解码失败。");
        using var resized = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(resized))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        { canvas.Clear(SKColors.White); canvas.DrawBitmap(source, SKRect.Create(Size, Size), paint); }
        var tensor = new DenseTensor<float>(new[] { 1, Size, Size, 3 });
        var input = tensor.Buffer.Span; var rgb = resized.GetPixelSpan();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                for (var channel = 0; channel < 3; channel++)
                    input[(y * Size + x) * 3 + channel] = rgb[y * resized.RowBytes + x * 4 + channel] / 255f;
        ct.ThrowIfCancellationRequested();
        using var result = _session.Run(NamedOnnxValue.CreateFromTensor(_session.InputName, tensor), ct);
        var logits = result.First().AsTensor<float>();
        if (logits.Rank != 4 || logits.Dimensions[0] != 1 || logits.Dimensions[3] != 6)
            throw new InvalidDataException("服装模型输出维度无效。");
        var height = logits.Dimensions[1]; var width = logits.Dimensions[2]; var scores = logits.ToArray();
        var colors = new double[12]; var pixels = 0; var confidence = 0d; var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 6; var label = 0; var maximum = float.NegativeInfinity;
                for (var category = 0; category < 6; category++)
                {
                    var value = scores[offset + category];
                    if (!float.IsFinite(value)) throw new InvalidDataException("服装模型分数无效。");
                    if (value > maximum) { maximum = value; label = category; }
                }
                if (label != 4) continue;
                var probability = 1 / Enumerable.Range(0, 6).Sum(category => Math.Exp(scores[offset + category] - maximum));
                if (probability < .6) continue;
                var sx = Math.Min(Size - 1, (int)((x + .5) * Size / width)); var sy = Math.Min(Size - 1, (int)((y + .5) * Size / height));
                var pixel = sy * resized.RowBytes + sx * 4;
                var color = Color(rgb[pixel], rgb[pixel + 1], rgb[pixel + 2]);
                mask[y * width + x] = true; colors[color]++; pixels++; confidence += probability;
            }
        }
        return new(seconds, pixels, pixels == 0 ? 0 : confidence / pixels, colors.Select(value => pixels == 0 ? 0 : value / pixels).ToArray())
        { Embedding = pixels < 128 ? new double[384] : Embed(source, mask, width, height, ct), Encoder = Encoder };
    }

    private double[] Embed(SKBitmap source, bool[] mask, int width, int height, CancellationToken ct)
    {
        const int side = 224, patch = 14, count = side / patch;
        var points = Enumerable.Range(0, mask.Length).Where(index => mask[index]).ToArray();
        var left = points.Min(index => index % width) * source.Width / (double)width;
        var top = points.Min(index => index / width) * source.Height / (double)height;
        var right = (points.Max(index => index % width) + 1) * source.Width / (double)width;
        var bottom = (points.Max(index => index / width) + 1) * source.Height / (double)height;
        var scale = Math.Min(side / (right - left), side / (bottom - top));
        var xpad = (side - (right - left) * scale) / 2; var ypad = (side - (bottom - top) * scale) / 2;
        var tensor = new DenseTensor<float>(new[] { 1, 3, side, side }); var values = tensor.Buffer.Span;
        double[] mean = [.485, .456, .406], std = [.229, .224, .225];
        for (var y = 0; y < side; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < side; x++)
            {
                var sx = left + (x + .5 - xpad) / scale; var sy = top + (y + .5 - ypad) / scale;
                var visible = sx >= left && sx < right && sy >= top && sy < bottom
                    && mask[Math.Clamp((int)(sy * height / source.Height), 0, height - 1) * width + Math.Clamp((int)(sx * width / source.Width), 0, width - 1)];
                var pixel = visible ? source.GetPixel(Math.Clamp((int)sx, 0, source.Width - 1), Math.Clamp((int)sy, 0, source.Height - 1)) : new SKColor(128, 128, 128);
                for (var channel = 0; channel < 3; channel++) values[channel * side * side + y * side + x] = (float)(((channel == 0 ? pixel.Red : channel == 1 ? pixel.Green : pixel.Blue) / 255d - mean[channel]) / std[channel]);
            }
        }
        using var output = _features.Run(NamedOnnxValue.CreateFromTensor(_features.InputName, tensor), ct);
        var hidden = output.First(item => item.Name == "last_hidden_state").AsTensor<float>();
        if (hidden.Rank != 3 || hidden.Dimensions[0] != 1 || hidden.Dimensions[1] != count * count + 1 || hidden.Dimensions[2] != 384)
            throw new InvalidDataException("服装图像特征维度无效。");
        // DINOv2's CLS token represents the whole garment crop, including its shape and pattern.
        var embedding = hidden.ToArray().Take(384).Select(value => (double)value).ToArray();
        if (embedding.Any(value => !double.IsFinite(value))) throw new InvalidDataException("服装图像特征无效。");
        var norm = Math.Sqrt(embedding.Sum(value => value * value));
        return norm == 0 ? embedding : embedding.Select(value => value / norm).ToArray();
    }

    // Same twelve-color order as the clothing tag vocabulary in FolderOutfitClassification.
    private static int Color(byte red, byte green, byte blue)
    {
        var r = red / 255d; var g = green / 255d; var b = blue / 255d;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b)); var delta = max - min;
        var saturation = max == 0 ? 0 : delta / max;
        if (max < .22) return 1;
        if (saturation < .17) return max > .82 ? 0 : 9;
        var hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        if (hue is >= 15 and < 45 && max < .68) return 8;
        if (hue < 15 || hue >= 345) return saturation < .55 && max > .6 ? 6 : 2;
        return hue < 42 ? 10 : hue < 72 ? 5 : hue < 165 ? 4 : hue < 270 ? 3 : hue < 315 ? 7 : 6;
    }
    public void Dispose() { _features.Dispose(); _session.Dispose(); _featureLease.Dispose(); _lease.Dispose(); }
}
