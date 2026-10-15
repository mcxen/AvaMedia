namespace AvaMedia.Core;

// Contract between the multi-stage task window (frontend) and stage-aware task sessions (backend).
// Models here are immutable snapshots; do not change signatures without updating
// /workspace/AvaMedia-sidebar-review/multistage-shell/impl-plan.md.

/// <summary>State of one planned stage.</summary>
public enum StageState { Pending, Active, Paused, Done, Failed, Skipped, Cancelled }

/// <summary>Task-level state shown by the window. Configuring is a queued task that has not started.</summary>
public enum MultiStageTaskState { Configuring, Running, Paused, Stopping, Failed, Completed, Cancelled }

/// <summary>Static stage declaration. Description is the generic plan text; real parameters go in <see cref="TaskStageStatus.Detail"/>.</summary>
public sealed record TaskStageDescriptor(string Id, string Title, string Description, bool CanRetryFrom = false);

/// <summary>Observed state of one stage.</summary>
public sealed record TaskStageStatus(TaskStageDescriptor Stage, StageState State)
{
    /// <summary>One real line: plan with actual parameters (Pending), counts (Active/Paused), output summary (Done),
    /// reason (Failed/Skipped/Cancelled). Never estimated or placeholder data.</summary>
    public string Detail { get; init; } = "";
    public double? Current { get; init; }
    public double? Total { get; init; }
    public string Unit { get; init; } = "";
    /// <summary>0..1 for determinate progress of the active/paused stage; null means indeterminate or not applicable.</summary>
    public double? Fraction { get; init; }
    /// <summary>Accumulated running time up to <see cref="MultiStageTaskSnapshot.SnapshotUtc"/>; null before the stage started.</summary>
    public TimeSpan? Elapsed { get; init; }
    /// <summary>True while the stage clock advances; the window adds (now - SnapshotUtc) to Elapsed.</summary>
    public bool IsClockRunning { get; init; }
}

/// <summary>Commands currently accepted; the window only shows lifecycle actions that are allowed.</summary>
public sealed record TaskCommands
{
    public bool CanStart { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanStop { get; init; }
    public bool CanRerun { get; init; }
    /// <summary>Stage id that <see cref="IMultiStageTask.RetryFromStageAsync"/> accepts now; empty when only a full rerun is possible.</summary>
    public string RetryStageId { get; init; } = "";
    public bool CanChangeOutput { get; init; }
}

/// <summary>Actual inference environment. Backend is empty before the model stage reports it.</summary>
public sealed record TaskRuntimeInfo(string Model, string Backend = "", string Note = "");

public enum TaskLogLevel { Info, Warning, Error, Marker }

/// <summary>Structured log line. StageId may be empty for task-level entries; Marker highlights cancel / interruption points.</summary>
public sealed record TaskLogEntry(DateTime TimeUtc, string StageId, string Title, string Message, TaskLogLevel Level = TaskLogLevel.Info);

/// <summary>Immutable view of a multi-stage task at <see cref="SnapshotUtc"/>.</summary>
public sealed record MultiStageTaskSnapshot
{
    public MultiStageTaskState State { get; init; }
    public IReadOnlyList<TaskStageStatus> Stages { get; init; } = [];
    /// <summary>Index of the active, paused, failed or cancelled stage; -1 when none.</summary>
    public int CurrentStageIndex { get; init; } = -1;
    /// <summary>Overall percentage 0..100.</summary>
    public double Progress { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool IsClockRunning { get; init; }
    public DateTime SnapshotUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Real estimate from the queue; null when unknown. Never estimated before start.</summary>
    public TimeSpan? Remaining { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    /// <summary>Failure reason (Failed) or empty.</summary>
    public string Error { get; init; } = "";
    /// <summary>Stage where the task failed or was cancelled; empty otherwise.</summary>
    public string InterruptedStageId { get; init; } = "";
    /// <summary>Stages whose outputs are retained for a retry (e.g. read-video, prepare-model).</summary>
    public IReadOnlyList<string> RetainedStageIds { get; init; } = [];
    public TaskCommands Commands { get; init; } = new();
    public TaskRuntimeInfo Runtime { get; init; } = new("");
    /// <summary>Increments for every published change; lets the window skip identical refreshes.</summary>
    public long Revision { get; init; }
}

/// <summary>Stage-aware session over one queued task. Created per window, disposed when the window closes;
/// disposing never stops the task.</summary>
public interface IMultiStageTask : IDisposable
{
    string FeatureId { get; }
    IReadOnlyList<TaskStageDescriptor> Stages { get; }
    MultiStageTaskSnapshot Snapshot { get; }
    /// <summary>Raised on any thread after Snapshot (or tool-specific views) changed. Handlers must marshal to the UI thread.</summary>
    event EventHandler? Changed;
    Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default);
    Task StartAsync();
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
    /// <summary>Run every stage again from the beginning.</summary>
    Task RerunAsync();
    /// <summary>Re-run from <paramref name="stageId"/>, reusing earlier stage outputs. Only valid for Commands.RetryStageId.</summary>
    Task RetryFromStageAsync(string stageId);
    /// <summary>Change the output folder while the task has not started (Commands.CanChangeOutput).</summary>
    Task ChangeOutputFolderAsync(string folder);
}

/// <summary>Queue operations provided by the desktop host; the backend session delegates lifecycle commands here.</summary>
public interface IMultiStageQueueHost
{
    bool CanStart(Job job);
    Task StartAsync(Job job);
    bool CanPause(Job job);
    void Pause(Job job);
    bool CanResume(Job job);
    Task ResumeAsync(Job job);
    bool CanStop(Job job);
    void Stop(Job job);
    bool CanRerun(Job job);
    /// <summary>Requeue the job; a non-null stage id asks the executor to resume from that stage.</summary>
    Task RerunAsync(Job job, string? fromStageId);
    bool CanChangeOutput(Job job);
    Task ChangeOutputFolderAsync(Job job, string folder);
}
