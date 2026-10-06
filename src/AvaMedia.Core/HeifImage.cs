using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Selects the primary HEIF image, including a complete derived tile grid.</summary>
internal sealed record HeifImage(string Specifier, int VideoIndex, int Width, int Height, int BitDepth,
    string PixelFormat, bool HasOrientation, string PreFilter = "")
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif";
    public string Map(int input = 0) => Map(Specifier, input);
    public static string Map(string specifier, int input = 0) => specifier.StartsWith("g:", StringComparison.Ordinal) ?
        $"[{input}:{specifier}]" : $"{input}:{specifier}";
    public static void AppendVideo(List<string> args, string map, string filters = "null")
    {
        // A tiled image is a filtergraph output. Filtering it must stay in a complex graph.
        if (map.StartsWith('[')) args.AddRange(["-filter_complex", $"{map}{filters}[heifout]", "-map", "[heifout]"]);
        else args.AddRange(["-map", map, "-vf", filters]);
    }
    public static HeifImage Parse(string rawJson)
    { using var json = JsonDocument.Parse(rawJson); return Read(json.RootElement); }
    public static HeifImage Read(JsonElement root)
    {
        var videos = root.GetProperty("streams").EnumerateArray().Where(s =>
            s.TryGetProperty("codec_type", out var type) && type.GetString() == "video").ToArray();
        if (videos.Length == 0) throw new InvalidDataException("HEIC / HEIF 文件没有可解码的主图。");
        var groups = root.TryGetProperty("stream_groups", out var allGroups) ? allGroups.EnumerateArray()
            .Where(g => g.TryGetProperty("type", out var type) && type.GetString() == "Tile Grid")
            .OrderByDescending(Default).ToArray() : [];
        var videoIndex = Array.FindIndex(videos, stream => Default(stream) == 1);
        if (groups.Length > 0 && (Default(groups[0]) == 1 || videoIndex < 0))
        {
            var group = groups[0]; var component = group.GetProperty("components")[0];
            var first = group.GetProperty("streams")[0].GetProperty("index").GetInt32();
            videoIndex = Array.FindIndex(videos, stream => stream.GetProperty("index").GetInt32() == first);
            var single = component.GetProperty("nb_tiles").GetInt32() == 1;
            var primary = Describe(single ? "v:" + Math.Max(0, videoIndex) : "g:" + group.GetProperty("index").GetInt32(), Math.Max(0, videoIndex),
                component.GetProperty("width").GetInt32(), component.GetProperty("height").GetInt32(),
                videos[Math.Max(0, videoIndex)], component);
            // FFmpeg builds its internal grid graph only when there is more than one tile.
            return single ? primary with { PreFilter = SingleTileFilters(component) } : primary;
        }
        if (videoIndex < 0) videoIndex = 0;
        var video = videos[videoIndex];
        return Describe("v:" + videoIndex, videoIndex, video.GetProperty("width").GetInt32(),
            video.GetProperty("height").GetInt32(), video, video);
    }
    private static HeifImage Describe(string specifier, int index, int width, int height, JsonElement video, JsonElement display)
    {
        var codec = video.GetProperty("codec_name").GetString();
        if (codec is not ("hevc" or "av1")) throw new InvalidDataException("此 HEIF 文件的主图编码不受支持。");
        if (video.TryGetProperty("nb_frames", out var frames) && long.TryParse(frames.GetString(), out var count) && count > 1)
            throw new InvalidDataException("当前支持 HEIF 静态主图，不支持动画序列。");
        var pixel = video.TryGetProperty("pix_fmt", out var pixels) ? pixels.GetString() ?? "rgba" : "rgba";
        var depth = 0;
        if (video.TryGetProperty("bits_per_raw_sample", out var bits)) int.TryParse(bits.GetString(), out depth);
        if (depth < 8) depth = pixel.Contains("16") ? 16 : pixel.Contains("12") ? 12 : pixel.Contains("10") ? 10 : 8;
        var oriented = false;
        if (display.TryGetProperty("side_data_list", out var sideData))
            foreach (var side in sideData.EnumerateArray())
                if (side.TryGetProperty("rotation", out var rotation))
                {
                    oriented = true;
                    if (Math.Abs(rotation.GetDouble()) % 180 == 90) (width, height) = (height, width);
                }
        return new(specifier, index, width, height, depth, pixel, oriented);
    }
    private static int Default(JsonElement element) => element.TryGetProperty("disposition", out var disposition) &&
        disposition.TryGetProperty("default", out var value) ? value.GetInt32() : 0;
    private static string SingleTileFilters(JsonElement component)
    {
        List<string> filters = [$"crop={component.GetProperty("width").GetInt32()}:{component.GetProperty("height").GetInt32()}:" +
            $"{component.GetProperty("horizontal_offset").GetInt32()}:{component.GetProperty("vertical_offset").GetInt32()}"];
        if (component.TryGetProperty("side_data_list", out var sides))
            foreach (var side in sides.EnumerateArray())
                if (side.TryGetProperty("rotation", out var rotation))
                {
                    var angle = ((-rotation.GetDouble() % 360) + 360) % 360;
                    var matrix = side.TryGetProperty("displaymatrix", out var display) ? display.GetString()!
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(row => row[(row.IndexOf(':') + 1)..]
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(long.Parse).ToArray() : [];
                    if (angle == 90) filters.Add(matrix.Length > 4 && matrix[3] > 0 ? "transpose=cclock_flip" : "transpose=clock");
                    else if (angle == 270) filters.Add(matrix.Length > 4 && matrix[3] < 0 ? "transpose=clock_flip" : "transpose=cclock");
                    else if (angle == 180)
                    {
                        if (matrix.Length < 5 || matrix[0] < 0) filters.Add("hflip");
                        if (matrix.Length < 5 || matrix[4] < 0) filters.Add("vflip");
                    }
                    else if (matrix.Length > 4 && matrix[4] < 0) filters.Add("vflip");
                }
        return string.Join(',', filters);
    }
}
