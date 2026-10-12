using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public static class SummaryProgressStore
{
    private static readonly ConcurrentDictionary<string, SummarySegmentProgress[]> Entries = new(StringComparer.Ordinal);
    public static void Set(string id, IEnumerable<SummarySegmentProgress> segments) => Entries[id] = segments.ToArray();
    public static SummarySegmentProgress[]? Get(string id) => Entries.TryGetValue(id, out var snapshot) ? snapshot.ToArray() : null;
}
public partial class Job
{
    private LongSummaryReport? _longSummaryReport;
    private SegmentOutput[] _summarySegments = [];
    [JsonIgnore] public LongSummaryReport? LongSummaryReport { get => Volatile.Read(ref _longSummaryReport); set { Volatile.Write(ref _longSummaryReport, value); Raise(); } }
    [JsonIgnore] public SegmentOutput[] SummarySegments { get => Volatile.Read(ref _summarySegments).ToArray(); set { Volatile.Write(ref _summarySegments, value.ToArray()); Raise(); } }
}
public sealed record LongSummaryTaskSnapshot(LongSummaryReport Report, SegmentOutput[] Segments, int TotalSegments);
