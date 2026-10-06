using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

/// <summary>Color handling for compression, including the HLG base layer of iPhone Dolby Vision.</summary>
public sealed record VideoCompressionColor(string Transfer, string Primaries, string Matrix, string Range, bool DolbyVision)
{
    public bool ToneMap => Transfer is "arib-std-b67" or "smpte2084";
    public string SourceLabel => (DolbyVision ? "Dolby Vision / " : "") + (Transfer == "arib-std-b67" ? "HLG HDR" : "PQ HDR");

    public static VideoCompressionColor Inspect(MediaInfo source)
    {
        if (!source.HasVideo || string.IsNullOrWhiteSpace(source.RawJson)) return new("", "", "", "", false);
        using var document = JsonDocument.Parse(source.RawJson);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(item => Text(item, "codec_type") == "video").ElementAt(source.VideoStreamIndex);
        var transfer = Text(stream, "color_transfer");
        var dolbyVision = false;
        if (stream.TryGetProperty("side_data_list", out var sideData))
            foreach (var data in sideData.EnumerateArray())
                if (Text(data, "side_data_type") == "DOVI configuration record")
                {
                    dolbyVision = true;
                    var profile = Integer(data, "dv_profile");
                    if (profile == 5)
                        throw new ArgumentException("此 Dolby Vision Profile 5 视频不能按普通 HDR 压缩；请先转换为 HLG、PQ 或 SDR 视频。");
                    // Apple's iPhone capture is Profile 8, cross-compatibility ID 4: a standard HLG base layer.
                    if (transfer is "" or "unknown" or "unspecified")
                    {
                        if (profile == 8 && Integer(data, "dv_bl_signal_compatibility_id") == 4) transfer = "arib-std-b67";
                        else throw new ArgumentException("Dolby Vision 视频缺少可识别的基础层色彩信息；请先转换为 HLG、PQ 或 SDR 视频。");
                    }
                }
        return new(transfer, Text(stream, "color_primaries"), Text(stream, "color_space"), Text(stream, "color_range"), dolbyVision);
    }

    public IReadOnlyList<string> Filters()
    {
        if (!ToneMap) return DolbyVision ? ["sidedata=mode=delete"] : [];
        var primaries = Primaries is "bt709" or "bt2020" or "smpte170m" or "smpte240m" or "smpte431" or "smpte432" ? Primaries : "bt2020";
        var matrix = Matrix switch { "gbr" => "gbr", "bt709" => "bt709", "bt2020c" => "bt2020c", _ => "bt2020nc" };
        var range = Range == "pc" ? "full" : "limited";
        // Linear floating point processing precedes scaling and the final 8-bit encoder conversion.
        // Remove obsolete HDR / Dolby Vision side data after tone mapping.
        return [$"zscale=transferin={Transfer}:primariesin={primaries}:matrixin={matrix}:rangein={range}:transfer=linear:npl=100",
            "format=gbrpf32le", "zscale=primaries=bt709", "tonemap=tonemap=mobius:desat=2",
            "zscale=transfer=bt709:matrix=bt709:range=limited:dither=error_diffusion", "format=yuv420p", "sidedata=mode=delete"];
    }

    public void ValidateFilters(string listing)
    {
        if (!ToneMap) return;
        var available = Regex.Matches(listing, @"(?m)^\s*[A-Z\.]{2,3}\s+(\S+)")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (new[] { "zscale", "tonemap", "sidedata" }.Any(filter => !available.Contains(filter)))
            throw new InvalidOperationException("HDR 视频压缩需要带 zscale、tonemap、sidedata 的 FFmpeg；请安装完整媒体引擎。");
    }

    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    private static int Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
