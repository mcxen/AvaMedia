using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record VideoSummaryChapter(string Title, string Text, double? Seconds);

/// <summary>Model-authored reading structure; navigation only uses timestamps present in the source.</summary>
internal sealed record VideoSummaryOutline(string[] Keywords, string[] Highlights, VideoSummaryChapter[] Chapters)
{
    private sealed record Chapter(string Title, string Text, string Timestamp);
    private sealed record Response(string[] Keywords, string[] Highlights, Chapter[] Chapters);
    internal static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object", "additionalProperties": false,
          "required": ["keywords", "highlights", "chapters"],
          "properties": {
            "keywords": { "type": "array", "maxItems": 8, "items": { "type": "string", "maxLength": 32 } },
            "highlights": { "type": "array", "maxItems": 5, "items": { "type": "string", "maxLength": 160 } },
            "chapters": { "type": "array", "maxItems": 8, "items": {
              "type": "object", "additionalProperties": false, "required": ["title", "text", "timestamp"],
              "properties": {
                "title": { "type": "string", "maxLength": 60 },
                "text": { "type": "string", "maxLength": 180 },
                "timestamp": { "type": "string", "maxLength": 24 }
              }
            } }
          }
        }
        """);

    internal static VideoSummaryOutline Parse(string json, IReadOnlyDictionary<string, double> sourceTimes, bool chapters)
    {
        var result = JsonSerializer.Deserialize<Response>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("视频总结结构无效。");
        var items = chapters ? (result.Chapters ?? []).Take(8).Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new VideoSummaryChapter(item.Title.Trim(), item.Text.Trim(),
                sourceTimes.TryGetValue(item.Timestamp?.Trim() ?? "", out var seconds) ? seconds : null)).ToArray() : [];
        // Unknown times remain readable, but never become invented playback positions.
        if (chapters && items.Length == 0) throw new InvalidDataException("小模型没有返回有效章节，请调整分段字符数后重试。");
        return new(Clean(result.Keywords, 8), Clean(result.Highlights, 5), items);
    }

    private static string[] Clean(string[]? values, int maximum) => (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(maximum).ToArray();

    internal string ChapterMarkdown()
    {
        var text = new StringBuilder();
        foreach (var chapter in Chapters)
        {
            text.Append("### ").Append(chapter.Title.Replace('\n', ' '));
            if (chapter.Seconds is { } time) text.Append(" · ").Append(MediaTime.Format(time));
            text.Append("\n\n").Append(chapter.Text).Append("\n\n");
        }
        return text.ToString().TrimEnd();
    }
}
