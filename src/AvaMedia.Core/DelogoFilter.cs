using System.Globalization;

namespace AvaMedia.Core;

/// <summary>Builds ffmpeg graph fragments for one or more (optionally time-limited) watermark regions.</summary>
public static class DelogoFilter
{
    public const int MaxRegions = 16;

    /// <summary>Legacy single region first, then any additional regions.</summary>
    public static DelogoRegion[] Regions(ConversionOptions o)
    {
        var regions = new List<DelogoRegion>();
        if (o.DelogoWidth > 0 && o.DelogoHeight > 0) regions.Add(new(o.DelogoX, o.DelogoY, o.DelogoWidth, o.DelogoHeight));
        if (o.DelogoRegions is { } extra) regions.AddRange(extra.Where(region => region.Width > 0 && region.Height > 0));
        return regions.ToArray();
    }

    public static void Validate(ConversionOptions o)
    {
        if (o.DelogoRegions is not { } extra) return;
        if (extra.Length + 1 > MaxRegions) throw new ArgumentException($"水印区域最多 {MaxRegions} 个。");
        foreach (var region in extra)
        {
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0) throw new ArgumentException("水印区域坐标与尺寸无效。");
            if (!double.IsFinite(region.Start) || !double.IsFinite(region.End) || region.Start < 0 || region.End < 0 || region.End > 0 && region.End <= region.Start)
                throw new ArgumentException("水印区域的时间范围无效。");
        }
    }

    /// <summary>
    /// Each region repairs a padded copy and overlays only the repaired rectangle, keeping the frame size.
    /// Region times are source times; the graph clock starts at <see cref="ConversionOptions.Start"/>.
    /// </summary>
    public static List<string> Build(ConversionOptions o, string prefix = "")
    {
        var filters = new List<string>(); var regions = Regions(o);
        for (var index = 0; index < regions.Length; index++)
        {
            var region = regions[index];
            // The first region keeps the original labels so single-region graphs are unchanged.
            var tag = index == 0 ? prefix : prefix + "d" + index.ToString(CultureInfo.InvariantCulture);
            var x = (long)region.X + 2; var y = (long)region.Y + 2;
            var enable = Enable(region, o.Start);
            filters.Add($"split[{tag}original][{tag}patch];[{tag}patch]" +
                "pad=iw+4:ih+4:2:2,fillborders=left=2:right=2:top=2:bottom=2:mode=smear," +
                $"delogo=x={x}:y={y}:w={region.Width}:h={region.Height}," +
                $"crop={region.Width}:{region.Height}:{x}:{y}:exact=1[{tag}repaired];" +
                $"[{tag}original][{tag}repaired]overlay={region.X}:{region.Y}" + enable);
        }
        return filters;
    }

    private static string Enable(DelogoRegion region, double origin)
    {
        if (region.Start <= 0 && region.End <= 0) return "";
        var start = Math.Max(0, region.Start - origin);
        var end = region.End > 0 ? region.End - origin : 0;
        var condition = end > 0 ? $"between(t\\,{MediaEngine.Number(start)}\\,{MediaEngine.Number(end)})" : $"gte(t\\,{MediaEngine.Number(start)})";
        return ":enable=" + condition;
    }
}
