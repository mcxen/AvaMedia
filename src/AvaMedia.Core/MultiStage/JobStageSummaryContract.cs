using System.Text.Json.Serialization;

namespace AvaMedia.Core;

// Contract between the main-window task list (frontend, Controls/JobRowView + TaskRowStatus) and the queue-level
// stage summary (backend). Design: /workspace/AvaMedia-sidebar-review/tasklist-stages/design.html.
// The task list renders ONLY from Job.StageSummary; it no longer reads Job.Progress / Status / Activity directly.

/// <summary>One stage as the task list shows it. Fraction applies to the active or paused stage only.</summary>
public sealed record JobStageItem(string Id, string Title, StageState State)
{
    /// <summary>One real line (counts, output summary or reason); empty when nothing real is known.</summary>
    public string Detail { get; init; } = "";
    /// <summary>0..1 for the active / paused stage when determinate; null otherwise.</summary>
    public double? Fraction { get; init; }
    /// <summary>Accumulated stage running time at <see cref="JobStageSummary.SnapshotUtc"/>; null before it started.</summary>
    public TimeSpan? Elapsed { get; init; }
}

/// <summary>
/// Lightweight queue-level stage summary for one job, valid for every job:
/// multi-stage jobs (person-clip, orientation, video summary, media classification, subtitles) derive it from their
/// execution-owned stage state; every other job gets a single-stage fallback (one stage titled with the tool label).
/// Invariants the backend guarantees (and tests cover):
///  - <see cref="State"/> always equals the owning Job.State at publish time; every Job.State transition publishes
///    synchronously (no throttling) before the next PropertyChanged of State is observed by the UI thread.
///  - Completed => every stage Done or Skipped, OverallFraction = 1, CurrentIndex = -1, no Active stage.
///  - Waiting (incl. after restart / retry) => no Active / Failed stage, Error empty, Remaining null, Elapsed zero
///    (stages kept for a retry may stay Done).
///  - Running => exactly one Active stage at CurrentIndex. Paused => that stage Paused. Failed => one Failed stage at
///    CurrentIndex with Error. Cancelled => the interrupted stage Cancelled at CurrentIndex.
///  - Progress-only updates are throttled per job (at most ~4 per second, last value always delivered).
///  - Persisted with the job (queue JSON), so completed / failed / cancelled rows show real stage results after restart;
///    a job that was Running when the app exited is restored consistently with its restored Job.State.
/// </summary>
public sealed record JobStageSummary
{
    /// <summary>Mirror of Job.State at <see cref="SnapshotUtc"/>.</summary>
    public JobState State { get; init; }
    /// <summary>True when the job has more than one planned stage (drives the segmented stage bar).</summary>
    public bool IsMultiStage { get; init; }
    public IReadOnlyList<JobStageItem> Stages { get; init; } = [];
    /// <summary>Index of the active, paused, failed or cancelled stage; -1 when none.</summary>
    public int CurrentIndex { get; init; } = -1;
    /// <summary>Overall 0..1; null when indeterminate (e.g. a single-stage job without a known total).</summary>
    public double? OverallFraction { get; init; }
    /// <summary>0..1 of the current stage; null when indeterminate or no current stage.</summary>
    public double? StageFraction { get; init; }
    /// <summary>Task running time at <see cref="SnapshotUtc"/>.</summary>
    public TimeSpan Elapsed { get; init; }
    /// <summary>True while Running; the UI adds (now - SnapshotUtc) to Elapsed.</summary>
    public bool IsClockRunning { get; init; }
    public DateTime SnapshotUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Real estimate while Running; null when unknown. Never estimated before start.</summary>
    public TimeSpan? Remaining { get; init; }
    /// <summary>Stage id where the job failed; empty otherwise.</summary>
    public string FailedStageId { get; init; } = "";
    /// <summary>Failure reason (Failed) or empty.</summary>
    public string Error { get; init; } = "";
    /// <summary>Stage id accepted by IMultiStageQueueHost.RerunAsync(job, id) now; empty when only a full rerun is possible.</summary>
    public string RetryStageId { get; init; } = "";
    /// <summary>Short live line for the current stage (single-stage fallback: the queue status detail); may be empty.</summary>
    public string ActivityText { get; init; } = "";
    /// <summary>Increments for every publish; lets rows skip identical refreshes.</summary>
    public long Revision { get; init; }

    public static JobStageSummary Empty { get; } = new();
    [JsonIgnore] public JobStageItem? Current => CurrentIndex >= 0 && CurrentIndex < Stages.Count ? Stages[CurrentIndex] : null;
}

public sealed partial class Job
{
    private JobStageSummary? _stageSummary;
    /// <summary>Queue-level stage summary; set only by the backend publisher (any thread) and raised as PropertyChanged.
    /// Null only before the backend attached to a freshly created or loaded job.</summary>
    public JobStageSummary? StageSummary
    {
        get => Volatile.Read(ref _stageSummary);
        set { if (!ReferenceEquals(Volatile.Read(ref _stageSummary), value)) { Volatile.Write(ref _stageSummary, value); Raise(nameof(StageSummary)); } }
    }
}
