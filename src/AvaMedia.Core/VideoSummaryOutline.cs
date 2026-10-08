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
    internal static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, required = new[] { "keywords", "highlights", "chapters" },
        properties = new
        {
            keywords = new { type = "array", maxItems = 8, items = VideoSummaryGrounding.ClaimSchema },
            highlights = new { type = "array", maxItems = 5, items = VideoSummaryGrounding.ClaimSchema },
            chapters = new { type = "array", maxItems = 8, items = new
            {
                type = "object", additionalProperties = false, required = new[] { "title", "text", "evidenceIds" },
                properties = new
                {
                    title = new { type = "string", maxLength = 60 }, text = new { type = "string", maxLength = 240 },
                    evidenceIds = new { type = "array", minItems = 1, maxItems = 3, items = new { type = "string", maxLength = 24 } }
                }
            } }
        }
    });

    internal static async Task<VideoSummaryOutline> ParseAsync(string json, VideoSummaryGrounding grounding, bool includeChapters, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var keywords = root.GetProperty("keywords").EnumerateArray().Select(grounding.ReadClaim).OfType<VideoSummaryClaim>().Take(8).ToArray();
        var highlights = root.GetProperty("highlights").EnumerateArray().Select(grounding.ReadClaim).OfType<VideoSummaryClaim>().Take(5).ToArray();
        var chapters = new List<(VideoSummaryClaim Title, VideoSummaryClaim Body)>();
        if (includeChapters)
            foreach (var item in root.GetProperty("chapters").EnumerateArray().Take(8))
            {
                var body = grounding.ReadClaim(item); var title = item.GetProperty("title").GetString()?.Trim();
                if (body is null || string.IsNullOrWhiteSpace(title)) continue;
                chapters.Add((body with { Text = title }, body));
            }
        var accepted = (await grounding.ReviewAsync(keywords.Concat(highlights).Concat(chapters.SelectMany(item => new[] { item.Title, item.Body })), ct).ConfigureAwait(false))
            .Select(VideoSummaryGrounding.Key).ToHashSet(StringComparer.Ordinal);
        bool Keep(VideoSummaryClaim claim) => accepted.Contains(VideoSummaryGrounding.Key(claim));
        return new(keywords.Where(Keep).ToArray(), highlights.Where(Keep).ToArray(), chapters.Where(item => Keep(item.Title) && Keep(item.Body))
            .Select(item => new VideoSummaryChapter(item.Title.Text, item.Body.Text,
                item.Body.EvidenceIds.Select(id => grounding.Sources[id].Start).Min()) { EvidenceIds = item.Body.EvidenceIds })
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
