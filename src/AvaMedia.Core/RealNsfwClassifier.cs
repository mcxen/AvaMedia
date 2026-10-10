using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace AvaMedia.Core;

public sealed record RealNsfwFrame(double Seconds, double Score);
public sealed record RealNsfwResult(double Average, double Maximum, IReadOnlyList<RealNsfwFrame> Frames,
    string Backend, string? FallbackReason)
{
    public string Model => ModelCatalog.NsfwId;
    public double Threshold => .5;
    public bool Suspected => Maximum >= Threshold;
}

/// <summary>Marqo's independent binary classifier. Its scores never replace JoyTag's action scores.</summary>
internal sealed class RealNsfwClassifier : IDisposable
{
    private const int Size = 384;
    private readonly ModelInferenceSession _session;
    internal string Backend => _session.Backend;
    internal RealNsfwClassifier(string directory)
    {
        _session = new(Path.Combine(directory, ModelCatalog.NsfwFile),
            ModelCatalog.Find(ModelCatalog.NsfwId).Files[0].Sha256, 1);
    }

    public RealNsfwResult Analyze(byte[][] images, double[] seconds, int[] samples, CancellationToken ct,
        Action<int, int>? progress = null)
    {
        if (images.Length == 0 || seconds.Length == 0 || seconds.Length != samples.Length
            || samples.Any(index => index < 0 || index >= images.Length)) throw new ArgumentException("NSFW 采样画面无效。");
        var scores = new double[images.Length];
        for (var index = 0; index < images.Length; index++)
        {
            scores[index] = Predict(images[index], ct);
            progress?.Invoke(index + 1, images.Length);
        }
        var frames = seconds.Select((second, index) => new RealNsfwFrame(second, scores[samples[index]])).ToArray();
        return new(frames.Average(frame => frame.Score), frames.Max(frame => frame.Score), frames, _session.Backend, _session.FallbackReason);
    }

    private double Predict(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var source = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("NSFW 图片解码失败。");
        // Follow this pinned ONNX export's direct RGB resize and [-1, 1] normalization.
        using var resized = new SKBitmap(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(resized))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, SKRect.Create(Size, Size), paint);
        }
        const int plane = Size * Size;
        var input = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        var values = input.Buffer.Span;
        var pixels = resized.GetPixelSpan();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var pixel = y * resized.RowBytes + x * 4;
                var target = y * Size + x;
                for (var channel = 0; channel < 3; channel++) values[target + channel * plane] = pixels[pixel + channel] / 127.5f - 1;
            }
        using var output = _session.Run(NamedOnnxValue.CreateFromTensor(_session.InputName, input), ct);
        var logits = output.First().AsTensor<float>();
        if (logits.Rank != 2 || logits.Dimensions[0] != 1 || logits.Dimensions[1] != 2)
            throw new InvalidDataException("NSFW 模型输出维度无效。");
        var nsfw = logits[0, 0]; var safe = logits[0, 1];
        if (!float.IsFinite(nsfw) || !float.IsFinite(safe)) throw new InvalidDataException("NSFW 模型输出无效。");
        // Class order is NSFW, SFW; stable two-class softmax, not independent sigmoid.
        return 1 / (1 + Math.Exp(Math.Clamp((double)safe - nsfw, -80, 80)));
    }

    public void Dispose() => _session.Dispose();
}
