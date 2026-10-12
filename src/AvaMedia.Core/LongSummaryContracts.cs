using System.Text.Json;

namespace AvaMedia.Core;

public static class LongSummaryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
public sealed record SummaryCue(string Id, double Start, double End, string Text, bool ContextOnly = false,
    string Language = "auto", int Track = 0, double LanguageConfidence = 1);
public sealed record SummaryFrame(string Id, double T, string Shot, string Caption, string Ocr = "");
public sealed record SegmentRange(string Id, double Start, double End, double BoundaryScore = 0);
public sealed record SegmentPlan(double Duration, IReadOnlyList<SegmentRange> Segments, string PlanHash);
public sealed record RollingContext(string PrevSummary, IReadOnlyList<string> OpenThreads, IReadOnlyList<string> Entities)
{
    public static RollingContext Empty { get; } = new("", [], []);
}
public sealed record SegmentJob(string SegmentId, double Start, double End, string Language,
    RollingContext Context, IReadOnlyList<SummaryCue> Transcript, IReadOnlyList<SummaryFrame> Frames, string Focus,
    bool VisualOnly = false, string OutputLanguage = "简体中文");
public sealed record SummaryEvent(double Start, double End, string Text, string[] Evidence);
public sealed record SummaryClaim(string Id, string Text, string[] Evidence, string Confidence = "low");
public sealed record SummaryEntity(string Name, string Type, string[] Aliases);
public sealed record SegmentResult(string Summary, SummaryEvent[] Events, SummaryClaim[] Claims,
    SummaryEntity[] Entities, string[] OpenQuestions, string[] ThreadsOut, string TopicLabel)
{
    public string Status { get; init; } = "done";
}
public sealed record SegmentOutput(SegmentRange Range, SegmentResult Result);
public sealed record SummaryKeyPoint(string Text, string[] Citations);
public sealed record ChapterNode(string Id, double Start, double End, string Title, string Summary,
    SummaryKeyPoint[] KeyPoints, string[] Citations, string Status = "degraded");
public sealed record CoverageGap(double Start, double End);
public sealed record SummaryCoverage(CoverageGap[] Gaps, string[] DegradedSegments);
public sealed record LongSummaryReport(double Duration, string Language, string Summary, SummaryKeyPoint[] KeyPoints,
    ChapterNode[] Chapters, SummaryCoverage Coverage, string EvidenceIndex = "evidence/index.json")
{
    public int Version { get; init; } = 2;
    public bool IsPartial { get; init; }
    public string[] Limitations { get; init; } = [];
    // Segment results live in work/map; the report stays bounded independently of transcript size.
    public string SegmentIndex { get; init; } = "work/segments.json";
}
