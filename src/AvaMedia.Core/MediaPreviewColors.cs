namespace AvaMedia.Core;

/// <summary>Deterministic, UI-independent sampling of straight-alpha RGBA thumbnails (at most 64 × 64).</summary>
public static class MediaPreviewColors
{
    public readonly record struct Rgb(double R, double G, double B);
    public sealed record Edges(Rgb? Top, Rgb? Right, Rgb? Bottom, Rgb? Left);

    public static Edges Sample(ReadOnlySpan<byte> rgba, int width, int height, int stride)
    {
        if (width is < 1 or > 64 || height is < 1 or > 64 || stride < width * 4 || rgba.Length < (long)stride * height)
            throw new ArgumentException("Expected an RGBA thumbnail no larger than 64 × 64.");
        return new(
            AverageLine(rgba, 0, 4, width),
            AverageLine(rgba, (width - 1) * 4, stride, height),
            AverageLine(rgba, (height - 1) * stride, 4, width),
            AverageLine(rgba, 0, stride, height));
    }

    /// <summary>Average each outer side of a mosaic, then give the four sides equal weight.
    /// Missing frames and fully transparent edges contribute no colour.</summary>
    public static Rgb? AverageBorder(IReadOnlyList<Edges?> cells, int columns, int rows)
    {
        if (columns < 1 || rows < 1 || cells.Count != checked(columns * rows))
            throw new ArgumentException("Grid dimensions must match the cell count.");
        var sides = new List<Rgb?>[4];
        for (var i = 0; i < sides.Length; i++) sides[i] = [];
        for (var column = 0; column < columns; column++)
        {
            sides[0].Add(cells[column]?.Top);
            sides[2].Add(cells[(rows - 1) * columns + column]?.Bottom);
        }
        for (var row = 0; row < rows; row++)
        {
            sides[1].Add(cells[row * columns + columns - 1]?.Right);
            sides[3].Add(cells[row * columns]?.Left);
        }
        return Average(sides.Select(Average));
    }

    private static Rgb? Average(IEnumerable<Rgb?> colors)
    {
        double r = 0, g = 0, b = 0; var count = 0;
        foreach (var color in colors)
            if (color is { } value) { r += value.R; g += value.G; b += value.B; count++; }
        return count == 0 ? null : new Rgb(r / count, g / count, b / count);
    }

    private static Rgb? AverageLine(ReadOnlySpan<byte> rgba, int offset, int step, int count)
    {
        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i < count; i++, offset += step)
        {
            var alpha = rgba[offset + 3];
            r += rgba[offset] * alpha; g += rgba[offset + 1] * alpha; b += rgba[offset + 2] * alpha; weight += alpha;
        }
        return weight == 0 ? null : new Rgb(r / weight, g / weight, b / weight);
    }
}
