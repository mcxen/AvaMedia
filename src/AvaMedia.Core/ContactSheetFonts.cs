using System.Text;
using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>Export fonts are opened from application assets, including the CJK fallback.</summary>
public sealed class ContactSheetFonts : IDisposable
{
    public static Func<Uri, Stream>? OpenAsset { get; set; }
    private readonly SKTypeface _fallback;
    public SKTypeface Primary { get; }
    public bool Antialias { get; }

    public ContactSheetFonts(ContactSheetOptions options)
    {
        Primary = options.Font switch
        {
            "inter" => Load("Inter/Inter-Regular.ttf"),
            "noto-sans-sc" => Load("NotoSansSC/NotoSansSC-Regular.ttf"),
            "fusion-pixel" => Load("Platinum/fusion-pixel-12px-proportional-zh_hans.ttf"),
            _ => InterfaceTypeface(options.InterfaceFont)
        };
        _fallback = Load("NotoSansSC/NotoSansSC-Regular.ttf");
        Antialias = !Primary.FamilyName.StartsWith("Fusion Pixel", StringComparison.Ordinal);
    }

    private static SKTypeface Load(string path)
    {
        var loader = OpenAsset ?? throw new InvalidOperationException("应用内置字体资源尚未初始化。");
        using var stream = loader(new Uri("avares://AvaMedia.Desktop/Assets/Fonts/" + path));
        return SKTypeface.FromStream(stream) ?? throw new InvalidDataException("无法读取内置字体：" + path);
    }

    private static SKTypeface InterfaceTypeface(string family)
    {
        foreach (var candidate in family.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (candidate.Contains("/Fonts/Inter#", StringComparison.Ordinal)) return Load("Inter/Inter-Regular.ttf");
            if (candidate.Contains("/Fonts/NotoSansSC#", StringComparison.Ordinal)) return Load("NotoSansSC/NotoSansSC-Regular.ttf");
            if (candidate.Contains("/Fonts/Platinum#", StringComparison.Ordinal)) return Load("Platinum/fusion-pixel-12px-proportional-zh_hans.ttf");
            // Matching also finds native hidden families such as macOS's system SF face.
            var system = SKFontManager.Default.MatchFamily(candidate, SKFontStyle.Normal);
            if (system is not null) return system;
        }
        return Load("Inter/Inter-Regular.ttf");
    }

    public void DrawLine(SKCanvas canvas, string text, float x, float baseline, float size, float availableWidth)
    {
        using var paint = new SKPaint { Typeface = Primary, TextSize = size, Color = SKColors.White, IsAntialias = Antialias };
        var runs = new List<(string Text, SKTypeface Face)>();
        var current = new StringBuilder();
        var face = Primary;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.ToString();
            paint.Typeface = Primary;
            var next = paint.ContainsGlyphs(value) ? Primary : _fallback;
            if (next != face && current.Length > 0) { runs.Add((current.ToString(), face)); current.Clear(); }
            current.Append(value); face = next;
        }
        if (current.Length > 0) runs.Add((current.ToString(), face));
        var total = 0f;
        foreach (var run in runs) { paint.Typeface = run.Face; total += paint.MeasureText(run.Text); }
        // Fit long user filenames without cutting Chinese or drawing outside the output.
        paint.TextSize *= Math.Min(1, availableWidth / Math.Max(1, total));
        foreach (var run in runs)
        {
            paint.Typeface = run.Face;
            canvas.DrawText(run.Text, x, baseline, paint);
            x += paint.MeasureText(run.Text);
        }
    }

    public void Dispose() { Primary.Dispose(); _fallback.Dispose(); }
}
