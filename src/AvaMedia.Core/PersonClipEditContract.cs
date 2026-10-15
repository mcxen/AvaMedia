namespace AvaMedia.Core;

// ---------------------------------------------------------------------------------------------
// 保留有人片段 · 调整片段 — contract between the editing backend (AvaMedia.Core) and the
// adjust-segments window (AvaMedia.Desktop/PersonClipAdjustWindow*). Design: 
// /workspace/AvaMedia-sidebar-review/adjust-segments (variant A, notes.md).
//
// Backend owns: per-sample scores persisted in PersonClipResult, the edit session (commands,
// undo/redo, modified count), snapping, span classification, save into the task result and the
// ClipEditResult handed to the existing export path (MainWindow.SubmitPersonClipsAsync).
// The UI never computes these itself; it renders what the session reports.
// ---------------------------------------------------------------------------------------------

/// <summary>Detector outcome flags of one sampled frame.</summary>
[Flags]
public enum PersonClipSampleFlags : byte
{
    None = 0,
    /// <summary>Kept by the detector decision (score ≥ threshold, votes, hands…).</summary>
    Person = 1,
    /// <summary>Near the threshold (decision Uncertain).</summary>
    Uncertain = 2,
    /// <summary>Skipped as a dark frame (not analysed).</summary>
    Dark = 4,
    /// <summary>Skipped as a blank frame (not analysed).</summary>
    Blank = 8,
    /// <summary>Inside a user exclusion range (not analysed).</summary>
    Excluded = 16,
    /// <summary>Extra sample taken while refining a segment boundary.</summary>
    Boundary = 32,
    /// <summary>Score reused from a similar frame instead of running the detector.</summary>
    Reused = 64
}

/// <summary>One sampled frame. Score is the best person score (0..1); 0 for skipped frames.</summary>
public readonly record struct PersonClipSample(double Seconds, float Score, PersonClipSampleFlags Flags)
{
    public bool Analysed => (Flags & (PersonClipSampleFlags.Dark | PersonClipSampleFlags.Blank | PersonClipSampleFlags.Excluded)) == 0;
}

/// <summary>Timeline classification of the whole source (sorted, non-overlapping, covering 0..Duration).</summary>
public enum PersonClipSpanKind
{
    /// <summary>有人 · 保留 (also manual keeps).</summary>
    Kept,
    /// <summary>已手动移除: a segment the user switched off.</summary>
    RemovedByUser,
    /// <summary>无人 · 移除.</summary>
    NoPerson,
    /// <summary>未检测: see <see cref="PersonClipUnscannedReason"/>.</summary>
    Unscanned
}

public enum PersonClipUnscannedReason { None, Dark, Blank, Excluded, NotAnalysed }

public sealed record PersonClipSpan(double Start, double End, PersonClipSpanKind Kind, PersonClipUnscannedReason Reason = PersonClipUnscannedReason.None);

public enum PersonClipSegmentOrigin { Detected, Manual }

/// <summary>
/// One editable segment. Options carries Start/End plus the per-segment effects already supported by
/// the quick-clip export (Speed, FadeIn/FadeOut, crop, Rotation, Flip, delogo, stream indexes).
/// Adjusted: bounds differ from the detection result. LowConfidence: average score of its analysed samples is
/// below threshold + 0.1 (false when no scores are stored).
/// </summary>
public sealed record PersonClipEditSegment(int Id, ConversionOptions Options, bool Keep, PersonClipSegmentOrigin Origin, bool Adjusted)
{
    public double Start => Options.Start;
    public double End => Options.End;
    public double Duration => Math.Max(0, End - Start);
    /// <summary>Average score of analysed samples inside the segment; null when no scores are stored or none fall inside.</summary>
    public double? Confidence { get; init; }
    public bool LowConfidence { get; init; }
}

/// <summary>Evidence for the inspector. Around = samples from Start-2s to End+2s (for the score bars).</summary>
public sealed record PersonClipSegmentEvidence(int Samples, int PersonSamples, int UncertainSamples, double Average, double Peak,
    int BoundarySamples, IReadOnlyList<PersonClipSample> Around);

public enum PersonClipSnapKind
{
    /// <summary>First / last person frame of a detected run (from stored scores).</summary>
    FirstPersonFrame,
    LastPersonFrame,
    /// <summary>Fallback when no scores are stored: original detection segment start / end.</summary>
    DetectionSegment,
    /// <summary>Another segment's edge.</summary>
    SegmentEdge,
    Exclusion
}

/// <summary>Snap target; Label is zh-CN text shown in the drag bubble (e.g. "首个有人帧").</summary>
public sealed record PersonClipSnap(double Seconds, PersonClipSnapKind Kind, string Label);

/// <summary>
/// Editing session over one completed person-clip task. Every command is one undo step, raises Changed, and
/// throws ArgumentException with a zh-CN message for invalid input (overlap, outside 0..Duration, shorter than
/// MinimumSeconds, last kept segment etc.). Segments are always returned in output order; new detected/manual
/// segments are inserted in time order. Ids are stable for the session (also across undo/redo).
/// </summary>
public interface IPersonClipEditSession
{
    Job Job { get; }
    string SourcePath { get; }
    MediaInfo Info { get; }
    double Duration { get; }
    double Threshold { get; }
    /// <summary>Smallest allowed segment length (seconds).</summary>
    double MinimumSeconds { get; }
    IReadOnlyList<PersonClipEditSegment> Segments { get; }
    /// <summary>Stored per-sample scores, sorted by time; empty for results produced before scores were persisted.</summary>
    IReadOnlyList<PersonClipSample> Samples { get; }
    bool HasScores { get; }
    IReadOnlyList<PersonClipRange> Exclusions { get; }
    IReadOnlyList<PersonClipSpan> Spans { get; }
    /// <summary>Distinct edits relative to the detection result (bounds, keep flags, manual adds, merges, splits, effects, exclusions).</summary>
    int ModifiedCount { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    double KeptSeconds { get; }
    int KeptCount { get; }
    /// <summary>Total length of the original detection segments.</summary>
    double DetectedSeconds { get; }
    /// <summary>The source file changed since detection (length / write time); editing and saving stay allowed, export is not.</summary>
    bool SourceChanged { get; }
    event EventHandler? Changed;

    void SetKeep(int id, bool keep);
    void SetBounds(int id, double start, double end);
    /// <summary>Split at a time strictly inside the segment; returns the id of the right part.</summary>
    int Split(int id, double at);
    /// <summary>Replace a segment by parts from ClipSplitWindow (batch split).</summary>
    void SplitMany(int id, IReadOnlyList<ConversionOptions> parts);
    void MergeWithNext(int id);
    void MergeWithPrevious(int id);
    /// <summary>Manual segment; returns its id.</summary>
    int Add(double start, double end);
    int Duplicate(int id);
    /// <summary>Change output order by ±1.</summary>
    void Move(int id, int delta);
    /// <summary>Copy effect fields (everything except Start/End) from options onto the segment.</summary>
    void SetEffects(int id, ConversionOptions options);
    /// <summary>Back to its detection bounds and kept; manual segments are removed.</summary>
    void RestoreSegment(int id);
    void RestoreDetection();
    void InvertKeep();
    /// <summary>Exclusions act immediately as a removal mask (segments are trimmed/split) and are saved for the next detection.</summary>
    void AddExclusion(double start, double end);
    void RemoveExclusion(int index);
    void Undo();
    void Redo();

    PersonClipSegmentEvidence? Evidence(int id);
    /// <summary>Nearest snap target within tolerance seconds; ignoreId excludes the dragged segment's own edges.</summary>
    PersonClipSnap? Snap(double seconds, double tolerance, int? ignoreId = null);
    /// <summary>Kept segments in output order for QuickClipWorkflow.PrepareJoinedJobs; throws when nothing is kept or the source changed.</summary>
    ClipEditResult ToClipEditResult();
    /// <summary>Detection options for 重新检测: current options with the session's exclusions.</summary>
    PersonClipOptions RedetectOptions();
    /// <summary>Persist the edit state into the task result (Result.Segments = kept segments in output order) and update Job.PersonDetectionResult.</summary>
    Task SaveAsync(CancellationToken ct = default);
}

public static partial class PersonClipEditing
{
    /// <summary>
    /// Open the editing session of a completed person-clip job (loads Job.PersonDetectionResult or its saved file and any
    /// saved edit state). Throws InvalidOperationException with a zh-CN message when the job has no complete result.
    /// </summary>
    public static Task<IPersonClipEditSession> OpenAsync(Job job, CancellationToken ct = default) => OpenCoreAsync(job, ct);

    // Implemented by the backend in PersonClipEditSession.cs.
    private static partial Task<IPersonClipEditSession> OpenCoreAsync(Job job, CancellationToken ct);
}
