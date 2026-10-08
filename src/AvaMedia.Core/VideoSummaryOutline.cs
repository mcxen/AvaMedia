using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record VideoSummaryChapter(string Title, string Text, double? Seconds)
{
    public string[] EvidenceIds { get; init; } = [];
}

/// <summary>Navigation derives from cited originals, never from model-authored timestamps.</summary>
internal sealed record VideoSummaryOutline(VideoSummaryClaim[] KeywordClaims, VideoSummaryClaim[] HighlightClaims, VideoSummaryChapter[] Chapters)
{
    internal string[] Keywords => KeywordClaims.Select(claim => claim.Text).ToArray();
    internal string[] Highlights => HighlightClaims.Select(claim => claim.Text).ToArray();
    internal static JsonElement Schema(int factCount) => JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, required = new[] { "keywords", "highlights", "chapters" },
        properties = new
        {
            keywords = new { type = "array", maxItems = 8, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "text", "factIndex" },
                properties = new { text = new { type = "string", maxLength = 60 }, factIndex = new { type = "integer", minimum = 0, maximum = factCount - 1 } }
            } },
            highlights = new { type = "array", maxItems = 5, items = new { type = "integer", minimum = 0, maximum = factCount - 1 } },
            chapters = new { type = "array", maxItems = 8, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "title", "factIndex" },
                properties = new
                {
                    title = new { type = "string", maxLength = 60 }, factIndex = new { type = "integer", minimum = 0, maximum = factCount - 1 }
                }
            } }
        }
    });

    internal static async Task<VideoSummaryOutline> ParseAsync(string json, VideoSummaryGrounding grounding, IReadOnlyList<VideoSummaryClaim> facts, bool includeChapters, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var keywords = root.GetProperty("keywords").EnumerateArray()
            .Select(item => Label(item.GetProperty("text"), Fact(item.GetProperty("factIndex")))).OfType<VideoSummaryClaim>().Take(8).ToArray();
        var highlights = root.GetProperty("highlights").EnumerateArray().Select(Fact).OfType<VideoSummaryClaim>()
            .DistinctBy(VideoSummaryGrounding.Key).Take(5).ToArray();
        var chapters = new List<(VideoSummaryClaim Title, VideoSummaryClaim Body)>();
        if (includeChapters)
            foreach (var item in root.GetProperty("chapters").EnumerateArray().Take(8))
            {
                var body = Fact(item.GetProperty("factIndex")); var title = Label(item.GetProperty("title"), body);
                if (body is null || title is null) continue;
                chapters.Add((title, body));
            }
        // Bodies and highlights select already reviewed originals. Only newly generated labels
        // require review; the model cannot turn an original recommendation into an event here.
        var accepted = (await grounding.ReviewAsync(keywords.Concat(chapters.Select(item => item.Title)), ct).ConfigureAwait(false))
            .Select(VideoSummaryGrounding.Key).ToHashSet(StringComparer.Ordinal);
        bool Keep(VideoSummaryClaim claim) => accepted.Contains(VideoSummaryGrounding.Key(claim));
        return new(keywords.Where(Keep).ToArray(), highlights, chapters.Where(item => Keep(item.Title))
            .Select(item => new VideoSummaryChapter(item.Title.Text, item.Body.Text,
                item.Body.EvidenceIds.Select(id => grounding.Sources[id].Start).Min()) { EvidenceIds = item.Body.EvidenceIds })
            .OrderBy(chapter => chapter.Seconds).ToArray());
        VideoSummaryClaim? Fact(JsonElement item) => item.TryGetInt32(out var index) && index >= 0 && index < facts.Count ? facts[index] : null;
        VideoSummaryClaim? Label(JsonElement item, VideoSummaryClaim? fact) => fact is null ? null : grounding.ReadClaim(
            JsonSerializer.SerializeToElement(new { text = item.GetString(), evidenceIds = fact.EvidenceIds }));
    }

    internal string ChapterMarkdown()
    {
        var text = new StringBuilder();
        foreach (var chapter in Chapters)
        {
            text.Append("### ").Append(chapter.Title.Replace('\n', ' '));
            if (chapter.Seconds is { } time) text.Append(" · ").Append(MediaTime.Format(time));
            text.Append("\n\n").Append(chapter.Text).Append(" 〔").Append(string.Join("、", chapter.EvidenceIds)).Append("〕\n\n");
        }
        return text.ToString().TrimEnd();
    }
}
