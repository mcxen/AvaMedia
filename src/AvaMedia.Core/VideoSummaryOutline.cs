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
    internal static JsonElement Schema() => JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, required = new[] { "keywords", "highlights", "chapters" },
        properties = new
        {
            keywords = new { type = "array", maxItems = 8, items = VideoSummaryGrounding.ClaimShape(60) },
            highlights = new { type = "array", maxItems = 5, items = VideoSummaryGrounding.ClaimSchema },
            chapters = new { type = "array", maxItems = 8, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "title", "claims" },
                properties = new
                {
                    title = VideoSummaryGrounding.ClaimShape(60),
                    claims = new { type = "array", minItems = 1, maxItems = 2, items = VideoSummaryGrounding.ClaimSchema }
                }
            } }
        }
    });

    internal static async Task<VideoSummaryOutline> ParseAsync(string json, VideoSummaryGrounding grounding, bool includeChapters, CancellationToken ct)
    {
        using var document = VideoSummaryGrounding.ParseResponse(json); var root = document.RootElement;
        var keywords = root.GetProperty("keywords").EnumerateArray()
            .Select(item => grounding.ReadClaim(item, 60)).OfType<VideoSummaryClaim>().Take(8).ToArray();
        var highlights = root.GetProperty("highlights").EnumerateArray().Select(item => grounding.ReadClaim(item)).OfType<VideoSummaryClaim>()
            .DistinctBy(VideoSummaryGrounding.Key).Take(5).ToArray();
        var chapters = new List<(VideoSummaryClaim Title, VideoSummaryClaim[] Claims)>();
        if (includeChapters)
            foreach (var item in root.GetProperty("chapters").EnumerateArray().Take(8))
            {
                var title = grounding.ReadClaim(item.GetProperty("title"), 60);
                var claims = item.GetProperty("claims").EnumerateArray().Take(2)
                    .Select(claim => grounding.ReadClaim(claim)).OfType<VideoSummaryClaim>().DistinctBy(VideoSummaryGrounding.Key).ToArray();
                if (claims.Length == 0 || title is null) continue;
                chapters.Add((title, claims));
            }
        // Titles, points and chapter bodies are synthesized text. Each must pass review using
        // its own original citations, including negations and the difference between advice and events.
        var accepted = (await grounding.ReviewAsync(keywords.Concat(highlights)
            .Concat(chapters.SelectMany(item => item.Claims.Prepend(item.Title))), ct).ConfigureAwait(false))
            .Select(VideoSummaryGrounding.Key).ToHashSet(StringComparer.Ordinal);
        bool Keep(VideoSummaryClaim claim) => accepted.Contains(VideoSummaryGrounding.Key(claim));
        return new(keywords.Where(Keep).DistinctBy(claim => claim.Text).ToArray(), highlights.Where(Keep).ToArray(),
            chapters.Where(item => Keep(item.Title)).Select(item => (item.Title, Claims: item.Claims.Where(Keep).ToArray()))
            .Where(item => item.Claims.Length > 0).Select(item =>
            {
                var ids = item.Claims.SelectMany(claim => claim.EvidenceIds).Concat(item.Title.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
                return new VideoSummaryChapter(item.Title.Text, string.Join("\n\n", item.Claims.Select(claim => claim.Text)),
                    ids.Select(id => grounding.Sources[id].Start).Min()) { EvidenceIds = ids };
            })
            .OrderBy(chapter => chapter.Seconds).ToArray());
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
