using System.Runtime.InteropServices;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace AvaMedia.Core;

public sealed record PdfPageSelection(int InputIndex, int PageNumber, int Rotation = 0, bool BreakAfter = false);
public sealed class PdfToolOptions
{
    public List<PdfPageSelection> Pages { get; set; } = [];
    public bool SplitEveryPage { get; set; }
    public bool ExtractAsOne { get; set; }
    public int Quality { get; set; } = 75;
    public int LongestEdge { get; set; } = 1800;
    public bool Rasterize { get; set; }
    public int Age { get; set; } = 35;
    public int Grain { get; set; } = 15;
    public int Folds { get; set; } = 55;
    public int Stains { get; set; } = 35;
    public double Skew { get; set; } = .2;
    public string Paper { get; set; } = "warm";
    public string PageSize { get; set; } = "a4";
    public bool Landscape { get; set; }
    public int Margin { get; set; } = 20;
    public int FontSize { get; set; } = 12;
    public PdfToolOptions Clone() => new()
    {
        Pages = [.. Pages], SplitEveryPage = SplitEveryPage, ExtractAsOne = ExtractAsOne,
        Quality = Quality, LongestEdge = LongestEdge, Rasterize = Rasterize,
        Age = Age, Grain = Grain, Folds = Folds, Stains = Stains, Skew = Skew, Paper = Paper,
        PageSize = PageSize, Landscape = Landscape, Margin = Margin, FontSize = FontSize
    };
}

public static class PdfTools
{
    public static bool Supports(Operation operation) => operation is Operation.PdfMerge or Operation.PdfSplit or Operation.PdfText or Operation.PdfDocx or Operation.PdfXlsx or Operation.TextPdf or Operation.ImagesPdf or Operation.PdfAge or Operation.PdfCompress;

    public static void Validate(Job job)
    {
        var options = job.Options.Pdf ?? throw new ArgumentException("缺少 PDF 页面设置。");
        var operation = Catalog.Find(job.FeatureId).Operation;
        if (operation is Operation.PdfCompress or Operation.TextPdf && job.Inputs.Length != 1) throw new ArgumentException("此工具一次处理一个文件，请先清空。");
        if (options.Pages.Count == 0) throw new ArgumentException("请至少选择一页。");
        if (options.Pages.Any(p => p.InputIndex < 0 || p.InputIndex >= job.Inputs.Length || p.PageNumber < 1 || p.Rotation is not (0 or 90 or 180 or 270)))
            throw new ArgumentException("PDF 页面设置无效。");
        if (options.Quality is < 10 or > 100 || options.LongestEdge is < 600 or > 4096 || options.Age is < 0 or > 100 || options.Grain is < 0 or > 100 || options.Folds is < 0 or > 100 || options.Stains is < 0 or > 100 || !double.IsFinite(options.Skew) || Math.Abs(options.Skew) > 2 || options.Paper is not ("warm" or "gray" or "sepia") || options.PageSize is not ("source" or "a4" or "letter") || options.Margin is < 0 or > 144 || options.FontSize is < 6 or > 48)
            throw new ArgumentException("PDF 参数超出范围。");
    }

    public static PdfPageInfo ReadImagePage(string path)
    {
        using var image=XImage.FromFile(path);
        return new(1,image.PointWidth,image.PointHeight);
    }

    public static (double Width, double Height) PaperSize(double width, double height, PdfToolOptions options)
    {
        if (options.PageSize == "source") return (width + options.Margin * 2, height + options.Margin * 2);
        (width, height) = options.PageSize == "letter" ? (612, 792) : (595.28, 841.89);
        return options.Landscape ? (height, width) : (width, height);
    }

    public static IReadOnlyList<IReadOnlyList<PdfPageSelection>> Groups(PdfToolOptions options)
    {
        if (options.ExtractAsOne) return new[] { (IReadOnlyList<PdfPageSelection>)options.Pages };
        var groups = new List<IReadOnlyList<PdfPageSelection>>(); var current = new List<PdfPageSelection>();
        foreach (var page in options.Pages)
        {
            current.Add(page);
            if (options.SplitEveryPage || page.BreakAfter) { groups.Add(current.ToArray()); current.Clear(); }
        }
        if (current.Count > 0) groups.Add(current.ToArray());
        return groups;
    }

    public static void Arrange(Job job, Action<double> progress, CancellationToken ct)
    {
        var options = job.Options.Pdf!; Validate(job);
        var sources = new Dictionary<int, PdfDocument>();
        try
        {
            foreach (var source in options.Pages.Select(p => p.InputIndex).Distinct())
            {
                ct.ThrowIfCancellationRequested();
                sources.Add(source, PdfReader.Open(job.Inputs[source], PdfDocumentOpenMode.Import));
            }
            var split = Catalog.Find(job.FeatureId).Operation == Operation.PdfSplit;
            var groups = split ? Groups(options) : new[] { (IReadOnlyList<PdfPageSelection>)options.Pages };
            if (split) Directory.CreateDirectory(job.Output);
            var completed = 0;
            for (var index = 0; index < groups.Count; index++)
            {
                using var output = new PdfDocument();
                foreach (var selection in groups[index])
                {
                    ct.ThrowIfCancellationRequested();
                    var source = sources[selection.InputIndex];
                    if (selection.PageNumber > source.PageCount) throw new ArgumentException("页码超出范围，请重新载入文档。");
                    var page = output.AddPage(source.Pages[selection.PageNumber - 1]);
                    page.Rotate = (page.Rotate + selection.Rotation) % 360;
                    progress(++completed * 95d / options.Pages.Count);
                }
                SaveNew(output, split ? Path.Combine(job.Output, $"part-{index + 1:0000}.pdf") : job.Output, ct);
            }
        }
        finally { foreach (var source in sources.Values) source.Dispose(); }
    }

    public static void Rasterize(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job); var options = job.Options.Pdf!;
        using var pdf = new PdfDocument();
        var metadata = options.Pages.Select(p => p.InputIndex).Distinct().ToDictionary(i => i, i => PdfRasterizer.ReadPages(job.Inputs[i], ct));
        for (var index = 0; index < options.Pages.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); var selection = options.Pages[index];
            var info = metadata[selection.InputIndex].ElementAtOrDefault(selection.PageNumber - 1) ?? throw new ArgumentException("页码超出范围。");
            using var rendered = PdfRasterizer.Render(job.Inputs[selection.InputIndex], selection.PageNumber, options.LongestEdge, selection.Rotation, ct);
            using var processed = Catalog.Find(job.FeatureId).Operation == Operation.PdfAge ? Aged(rendered, options, selection.PageNumber, ct) : rendered.Copy();
            using var image = SKImage.FromBitmap(processed); using var data = image.Encode(SKEncodedImageFormat.Jpeg, options.Quality);
            using var stream = new MemoryStream(data.ToArray()); using var pdfImage = XImage.FromStream(stream);
            var page = pdf.AddPage();
            var width = info.Width; var height = info.Height;
            if (selection.Rotation is 90 or 270) (width, height) = (height, width);
            page.Width = XUnit.FromPoint(width); page.Height = XUnit.FromPoint(height);
            using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawImage(pdfImage, 0, 0, width, height);
            progress((index + 1) * 95d / options.Pages.Count);
        }
        SaveNew(pdf, job.Output, ct);
    }

    // Deterministic page coordinates keep the live preview and export grain consistent.
    public static SKBitmap Aged(SKBitmap source, PdfToolOptions options, int seed, CancellationToken ct = default)
    {
        var output = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            using (var canvas = new SKCanvas(output))
            {
                canvas.Clear(SKColors.White); canvas.Translate(source.Width / 2f, source.Height / 2f);
                canvas.RotateDegrees((float)options.Skew); canvas.Translate(-source.Width / 2f, -source.Height / 2f);
                canvas.DrawBitmap(source, 0, 0);
            }
            var pixels = new byte[output.ByteCount]; Marshal.Copy(output.GetPixels(), pixels, 0, pixels.Length);
            var amount = options.Age / 100d; var grain = options.Grain / 100d;
            for (var y = 0; y < output.Height; y++)
            {
                if (y % 64 == 0) ct.ThrowIfCancellationRequested();
                var ny = (y + .5) / output.Height;
                for (var x = 0; x < output.Width; x++)
                {
                    var nx = (x + .5) / output.Width;
                    var edge = Math.Pow(Math.Max(Math.Abs(nx - .5), Math.Abs(ny - .5)) * 2, 10);
                    var hash = unchecked((uint)((int)(nx * 2200) * 374761393 + (int)(ny * 2200) * 668265263 + seed * 144269));
                    hash = (hash ^ (hash >> 13)) * 1274126177u;
                    var noise = ((hash & 255) / 255d - .5) * grain * 34;
                    var shade = amount * (8 + edge * 28 + 3 * Math.Sin(nx * 17 + ny * 11));
                    var offset = y * output.RowBytes + x * 4;
                    var blue = pixels[offset]; var green = pixels[offset + 1]; var red = pixels[offset + 2];
                    if (options.Paper == "gray")
                    {
                        var gray = Channel(red * .299 + green * .587 + blue * .114 - shade + noise);
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
                    }
                    else
                    {
                        var strength = options.Paper == "sepia" ? 1.6 : 1;
                        pixels[offset] = Channel(blue - shade * strength - amount * 17 * strength + noise);
                        pixels[offset + 1] = Channel(green - shade - amount * 4 * strength + noise);
                        pixels[offset + 2] = Channel(red - shade * .6 + noise);
                    }
                    pixels[offset + 3] = 255;
                }
            }
            Marshal.Copy(pixels, 0, output.GetPixels(), pixels.Length);
            using (var canvas = new SKCanvas(output))
            {
                // Normalized geometry follows the same page at thumbnail and export resolutions.
                canvas.Scale(output.Width, output.Height);
                DrawStains(canvas, options, seed, ct);
                DrawFolds(canvas, options, seed);
            }
            ct.ThrowIfCancellationRequested(); return output;
        }
        catch { output.Dispose(); throw; }
        static byte Channel(double value) => (byte)Math.Clamp(value, 0, 255);
    }

    private static SKColor PaperMark(PdfToolOptions options, double opacity)
    {
        var alpha = (byte)Math.Clamp(opacity, 0, 255);
        return options.Paper == "gray" ? new SKColor(95, 95, 95, alpha) : new SKColor(117, 78, 33, alpha);
    }

    private static float PaperNoise(int seed, int index)
    {
        var hash = unchecked((uint)(seed * 144269 + index * 374761393));
        hash = (hash ^ (hash >> 13)) * 1274126177u;
        return (hash & 65535) / 65535f;
    }

    private static void DrawStains(SKCanvas canvas, PdfToolOptions options, int seed, CancellationToken ct)
    {
        var strength = options.Stains / 100d;
        if (strength <= 0) return;
        SKPoint[] centers = [new(.015f,.12f), new(.99f,.36f), new(.07f,.85f), new(.8f,.98f), new(.78f,.19f), new(.56f,.68f)];
        static SKPoint Middle(SKPoint a, SKPoint b) => new((a.X+b.X)*.5f,(a.Y+b.Y)*.5f);
        for (var index = 0; index < centers.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var center = centers[index] + new SKPoint((PaperNoise(seed,index*13)-.5f)*.09f, (PaperNoise(seed,index*13+1)-.5f)*.08f);
            var radius = .075f + PaperNoise(seed,index*13+2)*.1f;
            var opacity = strength * (index < 4 ? 1 : .45);
            var points = new SKPoint[24];
            for (var point = 0; point < points.Length; point++)
            {
                var angle = point * MathF.Tau / points.Length;
                var distance = radius * (.86f + .08f*MathF.Sin(angle*3+index) + .06f*MathF.Cos(angle*5+seed));
                points[point] = center + new SKPoint(MathF.Cos(angle)*distance,MathF.Sin(angle)*distance*.78f);
            }
            using var outline = new SKPath();
            var start = Middle(points[^1],points[0]);
            outline.MoveTo(start);
            for (var point = 0; point < points.Length; point++)
                outline.QuadTo(points[point], Middle(points[point],points[(point+1)%points.Length]));
            outline.Close();
            using var shader = SKShader.CreateRadialGradient(center, radius,
                [PaperMark(options,18*opacity),PaperMark(options,32*opacity),PaperMark(options,65*opacity),PaperMark(options,0)],
                [0,.6f,.82f,1], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { IsAntialias = true, Shader = shader, BlendMode = SKBlendMode.Multiply };
            canvas.DrawPath(outline,paint);
            paint.Shader = null; paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = .0015f; paint.Color = PaperMark(options,28*opacity);
            canvas.DrawPath(outline,paint);
            // Small uneven deposits follow the water mark rather than a uniform dotted overlay.
            paint.Style = SKPaintStyle.Fill;
            for (var spot = 0; spot < 24; spot++)
            {
                var key = 200 + index*100 + spot*3;
                var angle = PaperNoise(seed,key)*MathF.Tau;
                var distance = radius * MathF.Sqrt(PaperNoise(seed,key+1));
                paint.Color = PaperMark(options,(15+PaperNoise(seed,key+2)*65)*opacity);
                canvas.DrawCircle(center.X+MathF.Cos(angle)*distance,center.Y+MathF.Sin(angle)*distance*.78f,.0006f+PaperNoise(seed,key+2)*.0012f,paint);
            }
        }
    }

    private static void DrawFolds(SKCanvas canvas, PdfToolOptions options, int seed)
    {
        var strength = options.Folds / 100d;
        if (strength <= 0) return;
        var vertical = .48f + (PaperNoise(seed,1001)-.5f)*.06f;
        var horizontal = .51f + (PaperNoise(seed,1002)-.5f)*.08f;
        using var first = new SKPath();
        first.MoveTo(vertical,-.02f); first.CubicTo(vertical+.004f,.33f,vertical-.006f,.67f,vertical+.002f,1.02f);
        using var second = new SKPath();
        second.MoveTo(-.02f,horizontal); second.CubicTo(.33f,horizontal-.004f,.67f,horizontal+.005f,1.02f,horizontal-.002f);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, BlendMode = SKBlendMode.Multiply };
        foreach (var (path, opacity) in new[] { (first,1d),(second,.8d) })
        {
            // Layered soft shadow, narrow crease and offset ridge give the paper depth.
            foreach (var (width, alpha) in new[] { (.026f,5),(.014f,9),(.006f,18),(.0014f,55) })
            { paint.StrokeWidth = width; paint.Color = PaperMark(options,alpha*strength*opacity); canvas.DrawPath(path,paint); }
            canvas.Save(); canvas.Translate(.002f,.0015f);
            paint.BlendMode = SKBlendMode.Screen; paint.Color = new SKColor(255,255,255,(byte)(22*strength*opacity)); paint.StrokeWidth = .0018f;
            canvas.DrawPath(path,paint); canvas.Restore(); paint.BlendMode = SKBlendMode.Multiply;
        }
    }

    public static void Compress(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job); var options = job.Options.Pdf!;
        if (options.Rasterize) { Rasterize(job, progress, ct); KeepSmaller(job, ct); return; }
        using var source = PdfReader.Open(job.Inputs[0], PdfDocumentOpenMode.Modify);
        // Re-encode only independent JPEG images; soft masks and special color spaces remain intact.
        var images = source.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Elements.GetName("/Subtype") == "/Image").ToArray();
        for (var index = 0; index < images.Length; index++)
        {
            ct.ThrowIfCancellationRequested(); var dictionary = images[index];
            if (dictionary.Stream is not null && dictionary.Elements.GetName("/Filter") == "/DCTDecode" && !dictionary.Elements.ContainsKey("/SMask") && !dictionary.Elements.ContainsKey("/Mask") && !dictionary.Elements.ContainsKey("/Decode") && dictionary.Elements.GetName("/ColorSpace") is "/DeviceRGB" or "/DeviceGray")
            {
                using var original = SKBitmap.Decode(dictionary.Stream.Value);
                if (original is not null)
                {
                    var scale = Math.Min(1d, options.LongestEdge / (double)Math.Max(original.Width, original.Height));
                    using var resized = original.Resize(new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale))), SKFilterQuality.High);
                    if (resized is not null)
                    {
                        using var image = SKImage.FromBitmap(resized); using var data = image.Encode(SKEncodedImageFormat.Jpeg, options.Quality);
                        if (data.Size < dictionary.Stream.Value.Length)
                        {
                            dictionary.Stream.Value = data.ToArray(); dictionary.Elements.SetInteger("/Width", resized.Width); dictionary.Elements.SetInteger("/Height", resized.Height);
                            dictionary.Elements.SetInteger("/BitsPerComponent", 8); dictionary.Elements.SetName("/ColorSpace", "/DeviceRGB"); dictionary.Elements.Remove("/DecodeParms");
                        }
                    }
                }
            }
            progress((index + 1) * 80d / Math.Max(1, images.Length));
        }
        source.Options.CompressContentStreams = true;
        SaveNew(source, job.Output, ct);
        KeepSmaller(job, ct);
    }

    private static void KeepSmaller(Job job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (new FileInfo(job.Output).Length >= new FileInfo(job.Inputs[0]).Length)
            File.Copy(job.Inputs[0], job.Output, true);
    }

    internal static void SaveNew(PdfDocument pdf, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) pdf.Save(stream, false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
