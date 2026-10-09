using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>
/// Gemma 4 / EmbeddingGemma 2 image preprocessing (preprocessor image_processor config): RGB, aspect-preserving resize into
/// max_soft_tokens × pooling² patches of 16 px with sides divisible by 48, PIL-compatible bicubic (a = −0.5, antialiased),
/// rescale to [0, 1] without normalization, 16×16 patches flattened as (y, x, channel), padded with position −1.
/// </summary>
internal static class GemmaImageProcessor
{
    public const int PatchSize = 16, Pooling = 3, Channels = 3;
    public sealed record Result(float[] Pixels, long[] Positions, int MaxPatches, int SoftTokens, int Width, int Height);

    public static (int Height, int Width) TargetSize(int height, int width, int maxSoftTokens)
    {
        var maxPatches = maxSoftTokens * Pooling * Pooling;
        var factor = Math.Sqrt((double)maxPatches * PatchSize * PatchSize / ((double)height * width));
        var side = Pooling * PatchSize;
        var targetHeight = (int)Math.Floor(factor * height / side) * side;
        var targetWidth = (int)Math.Floor(factor * width / side) * side;
        var maxSide = maxPatches / (Pooling * Pooling) * side;
        if (targetHeight == 0 && targetWidth == 0) throw new InvalidDataException("画面尺寸无效。");
        if (targetHeight == 0) { targetHeight = side; targetWidth = Math.Min(width / height * side, maxSide); }
        else if (targetWidth == 0) { targetWidth = side; targetHeight = Math.Min(height / width * side, maxSide); }
        return (targetHeight, targetWidth);
    }

    public static Result Process(byte[] encoded, int maxSoftTokens = 280)
    {
        using var source = SKBitmap.Decode(encoded) ?? throw new InvalidDataException("无法解码画面。");
        using var rgba = source.ColorType == SKColorType.Rgba8888 && source.AlphaType != SKAlphaType.Premul ? null
            : source.Copy(SKColorType.Rgba8888) ?? throw new InvalidDataException("无法转换画面颜色。");
        var bitmap = rgba ?? source;
        int width = bitmap.Width, height = bitmap.Height;
        var rgb = new float[width * height * Channels];
        var pixels = bitmap.GetPixelSpan();
        for (var index = 0; index < width * height; index++)
            for (var channel = 0; channel < Channels; channel++) rgb[index * Channels + channel] = pixels[index * 4 + channel];
        var (targetHeight, targetWidth) = TargetSize(height, width, maxSoftTokens);
        var resized = targetWidth == width && targetHeight == height ? rgb : Resize(rgb, width, height, targetWidth, targetHeight);
        var maxPatches = maxSoftTokens * Pooling * Pooling;
        var patchWidth = targetWidth / PatchSize; var patchHeight = targetHeight / PatchSize;
        var patches = patchWidth * patchHeight;
        var patchLength = PatchSize * PatchSize * Channels;
        var output = new float[maxPatches * patchLength];
        var positions = new long[maxPatches * 2];
        Array.Fill(positions, -1);
        for (var py = 0; py < patchHeight; py++)
            for (var px = 0; px < patchWidth; px++)
            {
                var patch = py * patchWidth + px;
                positions[patch * 2] = px; positions[patch * 2 + 1] = py;
                var offset = patch * patchLength;
                for (var y = 0; y < PatchSize; y++)
                {
                    var row = ((py * PatchSize + y) * targetWidth + px * PatchSize) * Channels;
                    for (var value = 0; value < PatchSize * Channels; value++)
                        output[offset + y * PatchSize * Channels + value] = resized[row + value] / 255f;
                }
            }
        return new(output, positions, maxPatches, patches / (Pooling * Pooling), targetWidth, targetHeight);
    }

    /// <summary>Separable resampling equivalent to PIL Image.resize(BICUBIC); intermediate values are rounded to 8 bits like PIL.</summary>
    internal static float[] Resize(float[] rgb, int width, int height, int targetWidth, int targetHeight)
    {
        var horizontal = new float[height * targetWidth * Channels];
        var (xBounds, xWeights, xSize) = Coefficients(width, targetWidth);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < targetWidth; x++)
                for (var c = 0; c < Channels; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < xBounds[x].Count; k++) sum += rgb[(y * width + xBounds[x].Start + k) * Channels + c] * xWeights[x * xSize + k];
                    horizontal[(y * targetWidth + x) * Channels + c] = Clip(sum);
                }
        var result = new float[targetHeight * targetWidth * Channels];
        var (yBounds, yWeights, ySize) = Coefficients(height, targetHeight);
        for (var y = 0; y < targetHeight; y++)
            for (var x = 0; x < targetWidth; x++)
                for (var c = 0; c < Channels; c++)
                {
                    double sum = 0;
                    for (var k = 0; k < yBounds[y].Count; k++) sum += horizontal[((yBounds[y].Start + k) * targetWidth + x) * Channels + c] * yWeights[y * ySize + k];
                    result[(y * targetWidth + x) * Channels + c] = Clip(sum);
                }
        return result;
    }

    private static float Clip(double value) => (float)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static double Bicubic(double x)
    {
        const double a = -.5;
        x = Math.Abs(x);
        if (x < 1) return ((a + 2) * x - (a + 3)) * x * x + 1;
        if (x < 2) return (((x - 5) * x + 8) * x - 4) * a;
        return 0;
    }

    // Mirrors PIL's precompute_coeffs (Resample.c) for a 2-pixel bicubic support.
    private static ((int Start, int Count)[] Bounds, double[] Weights, int Size) Coefficients(int input, int output)
    {
        var scale = (double)input / output;
        var filterScale = Math.Max(scale, 1);
        var support = 2 * filterScale;
        var size = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new (int, int)[output];
        var weights = new double[output * size];
        for (var index = 0; index < output; index++)
        {
            var center = (index + .5) * scale;
            var min = Math.Max((int)(center - support + .5), 0);
            var max = Math.Min((int)(center + support + .5), input) - min;
            double total = 0;
            for (var k = 0; k < max; k++) total += weights[index * size + k] = Bicubic((k + min - center + .5) / filterScale);
            if (total != 0) for (var k = 0; k < max; k++) weights[index * size + k] /= total;
            bounds[index] = (min, max);
        }
        return (bounds, weights, size);
    }
}
