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
    internal static JsonElement Schema(bool includeChapters = true) => JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, required = new[] { "keywords", "highlights", "chapters" },
        properties = new
        {
            keywords = new { type = "array", maxItems = 8, items = VideoSummaryGrounding.ClaimShape(60) },
            highlights = new { type = "array", maxItems = 5, items = VideoSummaryGrounding.ClaimSchema },
            chapters = new { type = "array", minItems = includeChapters ? 1 : 0, maxItems = includeChapters ? 8 : 0, items = new
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

    internal static async Task<VideoSummaryOutline> GenerateAsync(ISummaryModel model, string system, string prompt,
        IReadOnlyList<VideoSummaryClaim> facts, VideoSummaryGrounding grounding, bool includeChapters, CancellationToken ct,
        Action<string>? onStructuredRepair = null)
    {
        grounding.CurrentStage = "outline";
        var start = grounding.Diagnostics.Count;
        var structure = await Complete("outline", prompt).ConfigureAwait(false);
        var outline = await ParseAsync(structure, grounding, includeChapters, ct).ConfigureAwait(false);
        if (!includeChapters || outline.Chapters.Length > 0) return outline;

        var reasons = grounding.Diagnostics.Skip(start).Where(item => item.Outcome == "rejected" && item.Stage.EndsWith("/chapter-body", StringComparison.Ordinal))
            .Select(item => item.ClaimText + "：" + item.Reason).Distinct(StringComparer.Ordinal).ToArray();
        grounding.Report("semantic-repair", "全部章节正文未通过审核；仅执行一次有界语义修复。" +
            (reasons.Length == 0 ? "" : "拒绝原因：" + string.Join("；", reasons)));
        // These notes retain their original citations. Reuse the original-evidence review;
        // neither a diagnostic reason nor a rejected title becomes a new source fact.
        var verifiedFacts = await grounding.ReviewAsync(facts, ct).ConfigureAwait(false);
        if (verifiedFacts.Count == 0)
        {
            const string reason = "章节全部为空，且没有通过原始证据审核的事实可用于修复。";
            grounding.Report("failed", reason);
            throw new InvalidDataException(reason);
        }
        var repairPrompt = "上一轮章节正文未能通过原始证据审核。只基于下方已审核事实，重新生成完整总结结构 JSON。" +
            "chapters 至少 1 章、至多 8 章；每章用 1–2 条完整但保守的 claims，优先保持已审核表述的含义、否定与范围。" +
            "每个 text 与其 evidenceIds 必须由同一事实的原始引用支持，不补充数量、原因、连续动作、目的或结局。" +
            "title 使用简单的章节序号，引用本章正文的资料，不额外声称事实。keywords 至多 8 个、highlights 至多 5 条；无可靠依据可以为空。" +
            "所有表述均为 {text,evidenceIds}；使用下方当前笔记 id。拒绝原因只是纠错信息，不是视频事实，也不能引用。" +
            "只输出 JSON，不输出思考过程。\n具体正文拒绝原因（最多 600 字符；完整记录另存诊断）：\n" + Bounded(string.Join("\n", reasons), 600) +
            "\n已审核事实（保留原始证据链）：\n" + grounding.SummaryContext(verifiedFacts);
        grounding.CurrentStage = "outline/semantic-repair";
        structure = await Complete("outline/semantic-repair", repairPrompt).ConfigureAwait(false);
        var repairStart = grounding.Diagnostics.Count;
        outline = await ParseAsync(structure, grounding, includeChapters, ct).ConfigureAwait(false);
        if (outline.Chapters.Length > 0)
        {
            grounding.Report("recovered", "一次语义修复后获得有原始证据支持的章节。", stage: "outline/semantic-repair");
            return outline;
        }
        var repairReasons = grounding.Diagnostics.Skip(repairStart).Where(item => item.Outcome == "rejected" && item.Stage.EndsWith("/chapter-body", StringComparison.Ordinal))
            .Select(item => item.Reason).Distinct(StringComparer.Ordinal).ToArray();
        var failure = "总结章节在一次语义修复后仍全部为空：" +
            (repairReasons.Length == 0 ? "模型没有提供可审核的有效章节正文。" : Bounded(string.Join("；", repairReasons), 240)) + "完整候选和拒绝原因见诊断记录。";
        grounding.Report("failed", failure, stage: "outline/semantic-repair");
        throw new InvalidDataException(failure);

        Task<JsonElement> Complete(string stage, string content) => SummaryStructuredResponse.CompleteAsync(model, system,
            content, Schema(includeChapters), ct, tokens: 3072, operation: "总结结构", onRepair: onStructuredRepair,
            onCandidate: candidate => grounding.Report("structured-candidate", "模型生成的章节结构，尚未核验。", candidate, stage: stage),
            onInvalidCandidate: reason => grounding.Report("rejected-structure", reason, stage: stage));
    }

    private static string Bounded(string text, int budget) => text.Length <= budget ? text : text[..(budget - 1)] + "…";

    internal static async Task<VideoSummaryOutline> ParseAsync(JsonElement root, VideoSummaryGrounding grounding, bool includeChapters, CancellationToken ct)
    {
        var stage = grounding.CurrentStage;
        var keywords = root.GetProperty("keywords").EnumerateArray()
            .Select(item => grounding.ReadClaim(item, 60, stage + "/keyword")).OfType<VideoSummaryClaim>().Take(8).ToArray();
        var highlights = root.GetProperty("highlights").EnumerateArray().Select(item => grounding.ReadClaim(item, stage: stage + "/highlight"))
            .OfType<VideoSummaryClaim>().DistinctBy(VideoSummaryGrounding.Key).Take(5).ToArray();
        var chapters = new List<(VideoSummaryClaim? Title, VideoSummaryClaim[] Claims, int Number)>();
        if (includeChapters)
        {
            var number = 0;
            foreach (var item in root.GetProperty("chapters").EnumerateArray().Take(8))
            {
                number++;
                var title = grounding.ReadClaim(item.GetProperty("title"), 60, stage + "/chapter-title");
                var claims = item.GetProperty("claims").EnumerateArray().Take(2)
                    .Select(claim => grounding.ReadClaim(claim, stage: stage + "/chapter-body")).OfType<VideoSummaryClaim>()
                    .DistinctBy(VideoSummaryGrounding.Key).ToArray();
                if (claims.Length > 0) chapters.Add((title, claims, number));
            }
        }
        // Review titles independently. A rejected title cannot erase a supported chapter body.
        var candidates = keywords.Concat(highlights).Concat(chapters.SelectMany(item => item.Claims))
            .Concat(chapters.Select(item => item.Title).OfType<VideoSummaryClaim>());
        var accepted = (await grounding.ReviewAsync(candidates, ct).ConfigureAwait(false))
            .Select(VideoSummaryGrounding.Key).ToHashSet(StringComparer.Ordinal);
        bool Keep(VideoSummaryClaim claim) => accepted.Contains(VideoSummaryGrounding.Key(claim));
        var result = new List<VideoSummaryChapter>();
        foreach (var item in chapters)
        {
            var claims = item.Claims.Where(Keep).ToArray();
            if (claims.Length == 0) continue;
            var title = item.Title is { } candidate && Keep(candidate) ? candidate : null;
            var ids = claims.SelectMany(claim => claim.EvidenceIds).Concat(title?.EvidenceIds ?? [])
                .Distinct(StringComparer.Ordinal).ToArray();
            var heading = title?.Text ?? $"§ {item.Number}";
            if (title is null)
                grounding.Report("neutral-title", "章节正文已通过审核；未通过的标题替换为不陈述事实的章节序号。",
                    claim: new VideoSummaryClaim(heading, claims.SelectMany(claim => claim.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray()),
                    stage: stage + "/chapter-title");
            result.Add(new VideoSummaryChapter(heading, string.Join("\n\n", claims.Select(claim => claim.Text)),
                ids.Select(id => grounding.Sources[id].Start).Min()) { EvidenceIds = ids });
        }
        grounding.Report("outline-result", $"保留 {result.Count} 个已审核章节、{keywords.Count(Keep)} 个关键词、{highlights.Count(Keep)} 个要点。", stage: stage);
        return new(keywords.Where(Keep).DistinctBy(claim => claim.Text).ToArray(), highlights.Where(Keep).ToArray(),
            result.OrderBy(chapter => chapter.Seconds).ToArray());
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
