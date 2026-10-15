namespace AvaMedia.Core;

/// <summary>
/// Saved editing draft, separate from Result.Segments (the export projection). DetectionSegments and
/// DetectionExclusions never change. Entries retain removed segments and provenance for restoration.
/// Undo history is session-local; NextId persists so saved ids are never recycled.
/// </summary>
public sealed record PersonClipEditState(IReadOnlyList<ConversionOptions> DetectionSegments,
    IReadOnlyList<PersonClipRange> DetectionExclusions, IReadOnlyList<PersonClipEditEntry> Entries,
    IReadOnlyList<PersonClipRange> Exclusions, int NextId);

/// <summary>DetectionIds are 1-based indexes into the original detection, including split/merge ancestry.</summary>
public sealed record PersonClipEditEntry(PersonClipEditSegment Segment, int[] DetectionIds, bool Restructured = false);
