using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

/// <summary>Consumes FFmpeg-normalized SRT, preserving source timestamps for analysis and export.</summary>
public static class SubtitleTranscript
{
    private static readonly Regex Timestamp = new(@"^(\d{1,4}):(\d{2}):(\d{2})[,.](\d{3})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Tags = new(@"<[^>]*>|\{[^}]*\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<SubtitleCue> Parse(string srt)
    {
        var lines = srt.Replace("\r", "").TrimStart('\uFEFF').Split('\n');
        var cues = new List<SubtitleCue>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(" --> ", StringComparison.Ordinal)) continue;
            var times = lines[index].Split(" --> ", StringSplitOptions.None);
            if (times.Length != 2 || !TryTime(times[0].Trim(), out var start) || !TryTime(times[1].Trim(), out var end) || end <= start)
                throw new InvalidDataException("字幕时间戳无效。");
            var text = new List<string>();
            while (++index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) text.Add(lines[index]);
            var clean = WebUtility.HtmlDecode(Tags.Replace(string.Join("\n", text), "")).Trim();
            if (clean.Length > 0) cues.Add(new(start, end, clean));
        }
        return cues.OrderBy(cue => cue.Start).ToArray();
    }

    private static bool TryTime(string text, out TimeSpan time)
    {
        time = default; var match = Timestamp.Match(text);
        if (!match.Success) return false;
        var parts = match.Groups.Cast<Group>().Skip(1).Select(group => int.Parse(group.Value, CultureInfo.InvariantCulture)).ToArray();
        if (parts[1] > 59 || parts[2] > 59) return false;
        time = TimeSpan.FromHours(parts[0]) + TimeSpan.FromMinutes(parts[1]) + TimeSpan.FromSeconds(parts[2]) + TimeSpan.FromMilliseconds(parts[3]);
        return true;
    }

    public static string Timeline(IEnumerable<SubtitleCue> cues) => string.Join("\n", cues.Select(cue =>
        $"[{MediaTime.Format(cue.Start.TotalSeconds)} – {MediaTime.Format(cue.End.TotalSeconds)}] {cue.Text}"));
}
