using System.Text.Json;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public static class SubtitleOptions
{
    // Old presets use an external path alone; preserve their burn-in behavior.
    public static SubtitleMode Mode(ConversionOptions o) => o.SubtitleMode == SubtitleMode.Auto
        ? string.IsNullOrWhiteSpace(o.Subtitle) ? SubtitleMode.None : SubtitleMode.BurnIn
        : o.SubtitleMode;

    public static void Validate(ConversionOptions o)
    {
        if (!Enum.IsDefined(o.SubtitleMode) || o.SubtitleStreamIndex < -1 || o.VideoStreamIndex < 0 || o.AudioStreamIndex < 0)
            throw new ArgumentException("轨道索引必须为非负整数；字幕 -1 表示默认/全部。");
        if (o.SubtitleFontSize is < 0 or > 200 || o.SubtitleAlignment is < 1 or > 9 || o.SubtitleMargin is < 0 or > 2000)
            throw new ArgumentException("字幕字体大小、位置或边距超出允许范围。");
        if (!Regex.IsMatch(o.SubtitleColor, "^#[0-9a-fA-F]{6}$") || o.SubtitleFont.Any(c => char.IsControl(c) || ",=;:'\\[]".Contains(c)))
            throw new ArgumentException("字幕颜色需为 #RRGGBB，字体名称不能包含滤镜分隔符。");
        if (o.SubtitleLanguage.Length > 0 && !Regex.IsMatch(o.SubtitleLanguage, "^[a-zA-Z]{3}$"))
            throw new ArgumentException("字幕语言使用三字母代码，例如 zho、eng。");
        var mode = Mode(o);
        if (mode == SubtitleMode.None) return;
        if (mode == SubtitleMode.ExternalTrack && string.IsNullOrWhiteSpace(o.Subtitle)) throw new ArgumentException("请选择要附加的字幕文件。");
        if (mode is SubtitleMode.BurnIn or SubtitleMode.ExternalTrack && !string.IsNullOrWhiteSpace(o.Subtitle) && !File.Exists(o.Subtitle))
            throw new FileNotFoundException("字幕文件不存在", o.Subtitle);
        if (mode is SubtitleMode.Preserve or SubtitleMode.ExternalTrack)
        {
            if (o.Format is not ("mkv" or "mp4" or "mov" or "m4v" or "webm")) throw new ArgumentException("独立字幕轨需要 MKV、MP4、MOV、M4V 或 WebM 容器。");
            if (o.Speed != 1) throw new ArgumentException("独立字幕轨暂不支持变速。请选择烧录字幕，使字幕随画面一起变速。");
        }
    }

    public static void ValidateSource(ConversionOptions o, MediaInfo info)
    {
        var mode = Mode(o);
        if (mode != SubtitleMode.Preserve && !(mode == SubtitleMode.BurnIn && string.IsNullOrWhiteSpace(o.Subtitle))) return;
        using var json = JsonDocument.Parse(info.RawJson);
        var tracks = json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "subtitle").ToArray();
        if (tracks.Length == 0 || o.SubtitleStreamIndex >= tracks.Length) throw new ArgumentException("源文件不包含所选字幕轨。");
        if (mode == SubtitleMode.BurnIn && tracks[Math.Max(0, o.SubtitleStreamIndex)].GetProperty("codec_name").GetString() is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle")
            throw new ArgumentException("此位图字幕轨不能通过文本字幕滤镜烧录，请保留到 MKV 字幕轨。");
        if (o.Format != "mkv" && tracks.Where((_, i) => o.SubtitleStreamIndex < 0 || i == o.SubtitleStreamIndex).Any(s => s.GetProperty("codec_name").GetString() is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle"))
            throw new ArgumentException("位图字幕需保留到 MKV，不能转为此容器的文本字幕轨。");
    }

    internal static string BurnFilter(ConversionOptions o, string? source)
    {
        var path = string.IsNullOrWhiteSpace(o.Subtitle) ? source : o.Subtitle;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("请选择包含字幕的源文件或外部字幕文件", path);
        string Value(string value) => Escape(Escape(value, "\\':"), "\\'[],;");
        var filter = "subtitles=filename=" + Value(Path.GetFullPath(path).Replace("\\", "/"));
        if (string.IsNullOrWhiteSpace(o.Subtitle) || o.SubtitleStreamIndex >= 0) filter += ":si=" + Math.Max(0, o.SubtitleStreamIndex);
        var rgb = o.SubtitleColor[1..];
        var style = $"PrimaryColour=&H00{rgb[4..6]}{rgb[2..4]}{rgb[..2]},Alignment={o.SubtitleAlignment},MarginV={o.SubtitleMargin}";
        if (o.SubtitleFont.Length > 0) style += ",FontName=" + o.SubtitleFont;
        if (o.SubtitleFontSize > 0) style += ",FontSize=" + o.SubtitleFontSize;
        return filter + ":force_style=" + Value(style);
    }

    internal static void Map(List<string> args, ConversionOptions o, int externalInput)
    {
        var mode = Mode(o);
        if (mode is not (SubtitleMode.Preserve or SubtitleMode.ExternalTrack)) { args.Add("-sn"); return; }
        var input = mode == SubtitleMode.ExternalTrack ? externalInput : 0;
        args.AddRange(["-map", o.SubtitleStreamIndex < 0 ? $"{input}:s" : $"{input}:s:{o.SubtitleStreamIndex}"]);
        args.AddRange(["-c:s", o.Format is "mp4" or "mov" or "m4v" ? "mov_text" : o.Format == "webm" ? "webvtt" : "copy"]);
        if (o.SubtitleLanguage.Length > 0) args.AddRange(["-metadata:s:s", "language=" + o.SubtitleLanguage.ToLowerInvariant()]);
    }

    private static string Escape(string value, string special) => string.Concat(value.Select(c => special.Contains(c) ? "\\" + c : c.ToString()));
}
