using System.Text.Json;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Player;

internal sealed record PlaybackVideoProfile(VideoCompressionColor Color, bool RequiresNative, string PixelFormat)
{
    public static bool IsHdr(MediaInfo info)
    { var profile = Inspect(info); return profile.Color.ToneMap || profile.Color.DolbyVision; }
    public static PlaybackVideoProfile Inspect(MediaInfo info)
    {
        if (!info.HasVideo || string.IsNullOrWhiteSpace(info.RawJson)) return new(new("", "", "", "", false), false, "");
        using var document = JsonDocument.Parse(info.RawJson);
        var video = document.RootElement.GetProperty("streams").EnumerateArray().Where(s => Text(s, "codec_type") == "video").ElementAt(info.VideoStreamIndex);
        var transfer = Text(video, "color_transfer"); var dolby = false; var requiresNative = false;
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var data in sideData.EnumerateArray())
                if (Text(data, "side_data_type") == "DOVI configuration record")
                {
                    dolby = true;
                    var profile = data.TryGetProperty("dv_profile", out var p) ? p.GetInt32() : 0;
                    requiresNative = profile == 5;
                    if (transfer is "" or "unknown" or "unspecified")
                    {
                        var compatible = data.TryGetProperty("dv_bl_signal_compatibility_id", out var compatibility) ? compatibility.GetInt32() : 0;
                        if (compatible == 4) transfer = "arib-std-b67";
                        else if (compatible == 1) transfer = "smpte2084";
                        else requiresNative = true;
                    }
                }
        return new(new(transfer, Text(video, "color_primaries"), Text(video, "color_space"), Text(video, "color_range"), dolby), requiresNative, Text(video, "pix_fmt"));
    }
    public string Filters(int width, int height, double fps)
    {
        if (RequiresNative) throw new InvalidOperationException("此 Dolby Vision 视频需要使用原生 GPU / HDR 播放。");
        var filters = new List<string> { "fps=" + MediaEngine.Number(fps) + ":start_time=0" };
        if (Color.ToneMap)
        {
            var colorFilters = Color.Filters().ToArray(); colorFilters[0] += $":w={width}:h={height}";
            filters.AddRange(colorFilters);
        }
        else filters.Add($"scale={width}:{height}:flags=fast_bilinear");
        return string.Join(',', filters);
    }
    private static string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
}
