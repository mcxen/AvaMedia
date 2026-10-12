using System.Text.Json;

namespace AvaMedia.Core;

public sealed record SummaryMediaTrack(int Index, string Language, string Title, bool Default, string Codec);
public static class LongSummaryMediaPolicy
{
    public static IReadOnlyList<SummaryMediaTrack> Tracks(string rawJson, string type)
    {
        using var json = JsonDocument.Parse(rawJson);
        return json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == type).Select((s, index) =>
        {
            var tags = s.TryGetProperty("tags", out var t) ? t : default;
            string Tag(string name) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            return new SummaryMediaTrack(index, Tag("language"), Tag("title"), s.TryGetProperty("disposition", out var d) && d.TryGetProperty("default", out var def) && def.GetInt32() == 1,
                s.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "");
        }).ToArray();
    }
    public static string NormalizeLanguage(string language) => language.ToLowerInvariant() switch { "chi" or "zho" or "zh-cn" => "zh", "eng" => "en", "jpn" => "ja", _ => language.ToLowerInvariant() };
    public static int SelectTrack(IReadOnlyList<SummaryMediaTrack> tracks, string preferred) => tracks.FirstOrDefault(t => NormalizeLanguage(t.Language) == NormalizeLanguage(preferred))?.Index
        ?? tracks.FirstOrDefault(t => t.Default)?.Index ?? tracks.FirstOrDefault()?.Index ?? -1;
    public static bool VisualOnly(SummarySignals signals) => !signals.HasAudio || signals.Cues.Count == 0 || signals.VoiceIntervals.Count > 0
        && signals.VoiceIntervals.Sum(v => Math.Max(0, v.EndSeconds - v.StartSeconds)) / signals.Duration < .03;
    public static IReadOnlyList<SummaryCue> MergeTracks(IEnumerable<IEnumerable<SummaryCue>> tracks) => tracks.SelectMany(t => t).OrderBy(c => c.Start).ThenBy(c => c.Track).ToArray();
    public static async Task<IReadOnlyList<SummaryCue>> RepairLanguagesAsync(IReadOnlyList<SummaryCue> cues,
        Func<SummaryCue, string, CancellationToken, Task<SummaryCue>> recognize, CancellationToken ct)
    {
        var repaired = cues.ToArray();
        for (int i = 0; i < cues.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); if (cues[i].LanguageConfidence >= .5) continue;
            var neighbors = cues.Skip(Math.Max(0, i - 2)).Take(Math.Min(cues.Count - Math.Max(0, i - 2), 5))
                .Where(c => c.LanguageConfidence >= .5 && c.Language != "auto").GroupBy(c => c.Language).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (neighbors is not null) repaired[i] = await recognize(cues[i], neighbors.Key, ct);
        }
        return repaired;
    }
}
