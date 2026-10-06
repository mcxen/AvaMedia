using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public static class SourceVideoExport
{
    public const string Original = "原格式 / 原属性";
    public const string FastRotation = "Fast Copy（方向标记）";
    public const string OriginalHint = "沿用原容器、视频编码、帧率、像素格式和色彩标记，音轨和字幕直接复制，保留章节；画面需重新编码，码率和文件大小会变化。";
    public const string FastHint = "MOV / MP4 / M4V：只改播放方向标记，音视频和字幕直接复制，保留章节，不损失画质；播放器须支持方向标记。";
    public static string Format(string selection, string? path)
    {
        if (selection is not (Original or FastRotation)) return selection;
        var format = Path.GetExtension(path ?? "").TrimStart('.').ToLowerInvariant();
        if (!QuickClipBatch.VideoExtensions.Contains(format)) throw new ArgumentException("无法确定原视频容器，请选择输出格式。");
        if (selection == FastRotation && format is not ("mov" or "mp4" or "m4v")) throw new ArgumentException("方向标记 Fast Copy 支持 MOV、MP4、M4V；此文件请选择原属性画面旋转。");
        return format;
    }
    public static void ValidateOptions(ConversionOptions options)
    {
        if (options.LosslessRotation is { } angle)
        {
            _ = BatchRotate.Direction(angle);
            if (!options.CopyStreams || options.PreserveSourceAttributes || MediaEngine.HasFilters(options) || options.Mute || options.Start != 0 || options.End != 0 || !options.KeepMetadata || SubtitleOptions.Mode(options) != SubtitleMode.None)
                throw new ArgumentException("方向标记 Fast Copy 不能同时改动画面、音频、截取区间或字幕。");
            if (options.Format is not ("mp4" or "mov" or "m4v")) throw new ArgumentException("此容器不支持方向标记 Fast Copy。");
        }
        if (!options.PreserveSourceAttributes) return;
        if (options.CopyStreams || options.VideoCodec == "copy") throw new ArgumentException("保留原属性的画面处理需要重新编码视频。");
        if (options.Fps > 0) throw new ArgumentException("原属性模式保留原始帧率，请切换到指定格式后修改帧率。");
        if (MediaEngine.HasAudioFilters(options) || options.SampleRate > 0 || options.AudioChannels > 0 || options.Mute || !options.KeepMetadata || SubtitleOptions.Mode(options) != SubtitleMode.None)
            throw new ArgumentException("原属性模式保留全部原始音轨和字幕，请切换到指定格式后修改音频或字幕设置。");
    }
    public static void ValidateJob(Job job)
    {
        var options = job.Options;
        if (!options.PreserveSourceAttributes && options.LosslessRotation is null) return;
        if (job.Inputs.Length != 1 || job.InputOptions is not null || job.FeatureId is not ("rotate" or "crop"))
            throw new ArgumentException("原属性导出当前支持单个视频的批量旋转或裁剪任务。");
        var expected = Format(options.LosslessRotation is null ? Original : FastRotation, job.Inputs[0]);
        if (options.Format != expected || !Path.GetExtension(job.Output).Equals("." + expected, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("原属性导出必须使用源文件的容器和扩展名。");
        if (options.LosslessRotation is not null && job.FeatureId != "rotate") throw new ArgumentException("只有旋转可使用方向标记 Fast Copy，裁剪必须重新编码画面。");
    }
    public static string Encoder(MediaInfo media, string listing)
    {
        var available = Regex.Matches(listing, @"(?m)^\s*V[A-Z\.]{5}\s+(\S+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var candidates = media.VideoCodec switch
        {
            "h264" => new[] { "libx264", "libopenh264" }, "hevc" => ["libx265", "libkvazaar"],
            "vp8" => ["libvpx"], "vp9" => ["libvpx-vp9"], "av1" => ["libaom-av1", "libsvtav1"],
            "prores" => ["prores_ks", "prores"], "msmpeg4v3" => ["msmpeg4"],
            _ => [media.VideoCodec]
        };
        return candidates.FirstOrDefault(available.Contains) ?? throw new ArgumentException($"当前 FFmpeg 缺少 {media.VideoCodec} 视频编码器。请安装包含原编码器的 FFmpeg，或使用 MOV / MP4 的方向标记 Fast Copy。");
    }
    public static List<string> BuildArguments(Job job, IReadOnlyList<MediaInfo> infos)
    {
        MediaEngine.Validate(job);
        if (infos.Count != 1 || !infos[0].HasVideo) throw new ArgumentException("原属性导出需要完整的视频信息。");
        var media = infos[0]; var options = job.Options;
        using var json = JsonDocument.Parse(media.RawJson);
        var video = json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "video").ElementAt(options.VideoStreamIndex);
        var pixels = video.TryGetProperty("pix_fmt", out var pixelValue) ? pixelValue.GetString() : null;
        // yuvj* has the same layout as yuv* plus a full-range color tag.
        // Encoders such as libkvazaar accept the canonical layout, not its yuvj alias.
        var fullRangeAlias = pixels is "yuvj420p" or "yuvj422p" or "yuvj444p" or "yuvj440p" or "yuvj411p";
        var outputPixels = fullRangeAlias ? "yuv" + pixels![4..] : pixels;
        var asymmetricChroma = pixels is not null && Regex.IsMatch(pixels, @"^yuva?j?4(22|11|40)p");
        List<string> arguments = ["-hide_banner", "-nostdin", "-n", "-progress", "pipe:1", "-nostats"];
        if (options.LosslessRotation is { } clockwise)
        {
            var rotation = Rotation(video) - clockwise;
            arguments.AddRange([$"-display_rotation:v:{options.VideoStreamIndex}", MediaEngine.Number(rotation), "-noautorotate"]);
        }
        else if (options.Start > 0) arguments.AddRange(["-ss", MediaEngine.Number(options.Start)]);
        if (options.PreserveSourceAttributes && asymmetricChroma)
            arguments.AddRange([$"-display_rotation:v:{options.VideoStreamIndex}", "0", "-noautorotate"]);
        if (options.Threads > 0) arguments.AddRange(["-threads", options.Threads.ToString(), "-filter_threads", options.Threads.ToString()]);
        arguments.AddRange(["-i", job.Inputs[0], "-map", "0", "-copy_unknown", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy"]);
        // MOV chapter text is exposed as bin_data, which its muxer cannot stream-copy.
        // map_chapters recreates that track from the original chapter metadata.
        foreach (var track in json.RootElement.GetProperty("streams").EnumerateArray())
            if (track.TryGetProperty("codec_name", out var codec) && codec.GetString() == "bin_data" &&
                json.RootElement.TryGetProperty("chapters", out var chapters) && chapters.GetArrayLength() > 0 &&
                track.TryGetProperty("codec_tag_string", out var tag) && tag.GetString() == "text")
                arguments.AddRange(["-map", "-0:" + track.GetProperty("index").GetInt32()]);
        if (options.End > 0) arguments.AddRange(["-t", MediaEngine.Number(options.End - options.Start)]);
        if (options.PreserveSourceAttributes)
        {
            if (options.VideoCodec == "自动") throw new ArgumentException("尚未选择原视频的编码器。");
            var stream = ":v:" + options.VideoStreamIndex;
            arguments.AddRange(["-c" + stream, options.VideoCodec, "-fps_mode" + stream, "passthrough"]);
            var filters = MediaFilters.Video(options, job.Duration, source: job.Inputs[0]);
            if (asymmetricChroma)
            {
                var stored = Rotation(video);
                if (Math.Abs(stored % 90) > .01) throw new ArgumentException("原视频含非直角方向标记，请选择指定格式重新编码。");
                var upright = ((int)-stored % 360 + 360) % 360;
                filters.InsertRange(0, MediaFilters.Video(new() { Rotation = upright }, job.Duration));
                if (upright is 90 or 270 || options.Rotation is 90 or 270)
                {
                    // Transpose cannot accept 4:2:2 / 4:1:1 / 4:4:0 directly.
                    // Explicit 4:4:4 intermediates retain bit depth and alpha while
                    // the output remains in the original chroma sampling format.
                    var fullChroma = Regex.Replace(pixels!, @"4(22|11|40)", "444");
                    filters.InsertRange(0, ["scale=iw:ih", "format=" + fullChroma]);
                    filters.AddRange(["scale=iw:ih", "format=" + pixels]);
                }
            }
            // Strict pixel-format selection disables automatic filter conversions,
            // so normalize explicitly without resizing or compressing the sample range.
            if (fullRangeAlias) filters.AddRange(["scale=iw:ih:in_range=full:out_range=full", "format=" + outputPixels]);
            if (options.VideoCodec == "libkvazaar")
            {
                var (width, height) = OutputSize(media, options);
                if ((width | height) % 2 != 0) throw new ArgumentException("当前 HEVC 编码器要求画面宽高为偶数，请调整输出尺寸。");
                var right = (8 - width % 8) % 8;
                var bottom = (8 - height % 8) % 8;
                if (right != 0 || bottom != 0)
                {
                    // Kvazaar requires 8-pixel alignment. Repeat edge pixels only
                    // outside the requested image, then exclude them in the HEVC SPS.
                    filters.AddRange([$"pad={width + right}:{height + bottom}:0:0", $"fillborders=right={right}:bottom={bottom}:mode=smear"]);
                    arguments.AddRange(["-bsf" + stream, $"hevc_metadata=width={width}:height={height}"]);
                }
            }
            if (filters.Count > 0) arguments.AddRange(["-filter" + stream, string.Join(",", filters)]);
            if (outputPixels is not null) arguments.AddRange(["-pix_fmt" + stream, "+" + outputPixels]);
            if (video.TryGetProperty("bit_rate", out var rate) && long.TryParse(rate.GetString(), out var bitrate) && bitrate > 0)
                arguments.AddRange(["-b" + stream, bitrate.ToString(CultureInfo.InvariantCulture)]);
            if (fullRangeAlias) arguments.AddRange(["-color_range" + stream, "pc"]);
            foreach (var field in new[] { "color_range", "color_space", "color_transfer", "color_primaries", "chroma_location" })
            {
                if (fullRangeAlias && field == "color_range") continue;
                if (video.TryGetProperty(field, out var value) && value.GetString() is { } text && text is not ("unknown" or "unspecified"))
                    arguments.AddRange(["-" + (field == "color_space" ? "colorspace" : field == "chroma_location" ? "chroma_sample_location" : field == "color_transfer" ? "color_trc" : field) + stream, text]);
            }
            if (media.VideoCodec == "prores" && video.TryGetProperty("profile", out var profile))
            {
                var number = profile.GetString() switch { "Proxy" => 0, "LT" => 1, "Standard" => 2, "HQ" => 3, "4444" => 4, "XQ" => 5, _ => -1 };
                if (number >= 0) arguments.AddRange(["-profile" + stream, number.ToString()]);
            }
            // Pixel rotation consumes the old display matrix; avoid rotating twice on playback.
            arguments.AddRange(["-metadata:s:v:" + options.VideoStreamIndex, "rotate=0"]);
        }
        if (options.Format == "m4v") arguments.AddRange(["-f", "mp4"]);
        arguments.Add(job.Output); return arguments;
    }
    private static (int Width, int Height) OutputSize(MediaInfo media, ConversionOptions options)
    {
        var width = options.CropWidth > 0 ? options.CropWidth : media.Width;
        var height = options.CropHeight > 0 ? options.CropHeight : media.Height;
        if (width <= 0 || height <= 0) throw new ArgumentException("无法读取原视频画面尺寸。");
        if (options.Width > 0 || options.Height > 0)
        {
            // Match scale's -2 dimension: nearest even size, with ties rounded up.
            var scaledWidth = options.Width > 0 ? options.Width : checked((int)Math.Round(options.Height * (double)width / height / 2, MidpointRounding.AwayFromZero) * 2);
            var scaledHeight = options.Height > 0 ? options.Height : checked((int)Math.Round(options.Width * (double)height / width / 2, MidpointRounding.AwayFromZero) * 2);
            width = scaledWidth; height = scaledHeight;
        }
        if (width <= 0 || height <= 0) throw new ArgumentException("缩放后的画面尺寸必须大于零。");
        return options.Rotation is 90 or 270 ? (height, width) : (width, height);
    }
    private static double Rotation(JsonElement video)
    {
        if (!video.TryGetProperty("side_data_list", out var sideData)) return 0;
        foreach (var side in sideData.EnumerateArray())
        {
            if (!side.TryGetProperty("rotation", out var rotation)) continue;
            if (side.TryGetProperty("displaymatrix", out var matrix))
            {
                var rows = (matrix.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row[(row.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => double.Parse(n, CultureInfo.InvariantCulture)).ToArray()).ToArray();
                if (rows.Length >= 2 && rows[0].Length >= 2 && rows[1].Length >= 2 && rows[0][0] * rows[1][1] - rows[0][1] * rows[1][0] < 0)
                    throw new ArgumentException("视频含镜像方向标记，请选择指定格式重新编码，避免改变镜像信息。");
            }
            return rotation.GetDouble();
        }
        return 0;
    }
}
