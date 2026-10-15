using System.Globalization;
using AvaMedia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Display model of the task-list status column, computed only from the queue-level <see cref="JobStageSummary"/>
/// (UISPEC 7.4.1). The job is consulted for the state when the summary is missing or behind, never for progress.</summary>
internal sealed record TaskRowStatus
{
    public JobState State { get; init; }
    /// <summary>StatusPill state: waiting, running, paused, failed, completed, cancelled.</summary>
    public string Pill { get; init; } = "waiting";
    public string PillText { get; init; } = "";
    /// <summary>Emphasised lead: current stage title (multi-stage) or percentage (single-stage running).</summary>
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Right-aligned overall percentage and remaining time (running / paused only).</summary>
    public string Meta { get; init; } = "";
    public bool IsError { get; init; }
    public string Tip { get; init; } = "";
    /// <summary>Single-stage 4px bar: null hides it, NaN is indeterminate, otherwise 0..100.</summary>
    public double? Progress { get; init; }
    public IReadOnlyList<StageBarSegment>? Segments { get; init; }
    public string RetryStageId { get; init; } = "";
    public string RetryStageTitle { get; init; } = "";

    public static TaskRowStatus From(Job job, JobStageSummary? summary, string completedDetail)
    {
        var state = job.State;
        // A summary published for another state is stale; keep its stage plan but drop its live values.
        var current = summary is not null && summary.State == state ? summary : null;
        var stages = (current ?? summary)?.Stages ?? [];
        var multi = (current ?? summary)?.IsMultiStage == true && stages.Count > 1;
        var unchanged = state == JobState.Completed && job.CompletionKind == JobCompletionKind.Unchanged;
        var status = new TaskRowStatus
        {
            State = state,
            Pill = unchanged ? "waiting" : state switch
            {
                JobState.Running or JobState.AwaitingUser => "running", JobState.Paused => "paused", JobState.Failed => "failed",
                JobState.Completed => "completed", JobState.Cancelled => "cancelled", _ => "waiting"
            },
            PillText = Localization.Text(unchanged ? "无需瘦身" : state switch
            {
                JobState.AwaitingUser => "等待你操作", JobState.Running => "进行中", JobState.Paused => "已暂停", JobState.Failed => "失败",
                JobState.Completed => "已完成", JobState.Cancelled => "已取消", JobState.Stopping => "正在停止", _ => "等待"
            })
        };
        var error = (current?.Error is { Length: > 0 } reported ? reported : job.Error).Trim();
        if (current?.Pages.Count > 0) return LifecyclePages(status, current, error, completedDetail);
        return multi ? MultiStage(status, current, summary!, error, completedDetail) : SingleStage(status, job, current, error, completedDetail, unchanged);
    }

    private static TaskRowStatus LifecyclePages(TaskRowStatus status, JobStageSummary summary, string error, string completedDetail)
    {
        var pages = summary.Pages;
        var current = summary.CurrentPage;
        var index = pages.ToList().FindIndex(page => page.Page.Id == summary.CurrentPageId);
        var segments = pages.Select(page => new StageBarSegment(page.State switch
        {
            PageState.Completed => "done", PageState.AwaitingUser when status.State == JobState.AwaitingUser => "awaiting",
            PageState.Failed => "failed", PageState.Running => status.State == JobState.Paused ? "paused" : "active", _ => "pending"
        }, page.State == PageState.Running ? summary.OverallFraction : null)).ToArray();
        var detail = status.State switch
        {
            JobState.AwaitingUser => "",
            JobState.Failed => error,
            JobState.Completed => completedDetail,
            _ => Localization.Text(summary.ActivityText)
        };
        return status with
        {
            Segments = segments,
            Title = status.State == JobState.Completed ? "" : Localization.Text(current?.Page.Title ?? ""),
            Text = Join(status.State == JobState.Completed ? "" : Localization.Format($"{index + 1}/{pages.Count}"), detail),
            Meta = status.State == JobState.Running ? Join(summary.OverallFraction is { } f ? Percent(f * 100, 0) : "", Remaining(summary)) : "",
            IsError = status.State == JobState.Failed, RetryStageId = summary.RetryStageId,
            RetryStageTitle = summary.Stages.FirstOrDefault(stage => stage.Id == summary.RetryStageId)?.Title ?? ""
        };
    }

    private static TaskRowStatus SingleStage(TaskRowStatus status, Job job, JobStageSummary? summary, string error, string completedDetail, bool unchanged)
    {
        var fraction = summary?.OverallFraction;
        var percent = fraction is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) * 100 : (double?)null;
        switch (status.State)
        {
            case JobState.Running:
                return status with
                {
                    Title = percent is { } p ? Percent(p, 1) : "", Text = Localization.Text(summary?.ActivityText ?? ""),
                    Meta = Remaining(summary), Progress = percent ?? double.NaN,
                    Tip = Join(percent is { } tp ? Percent(tp, 1) : "", Localization.Text(summary?.ActivityText ?? ""), Remaining(summary))
                };
            case JobState.Stopping:
                return status with { Progress = percent ?? double.NaN };
            case JobState.Paused when percent > 0:
                return status with { Text = Percent(percent.Value, 1), Progress = percent };
            case JobState.Failed:
                var reason = error.Length > 0 ? error : Localization.Text("查看任务详情");
                return status with { Text = reason, IsError = true, Tip = reason };
            case JobState.Completed when unchanged:
                return status with { Text = Localization.Text(job.CompletionMessage), Tip = Localization.Text(job.CompletionMessage) };
            case JobState.Completed:
                return status with { Text = completedDetail, Tip = completedDetail };
            default:
                return status;
        }
    }

    private static TaskRowStatus MultiStage(TaskRowStatus status, JobStageSummary? current, JobStageSummary plan, string error, string completedDetail)
    {
        var stages = (current ?? plan).Stages;
        var count = stages.Count;
        // Without a current summary only the plan is trustworthy: draw it as pending, or as finished for a completed job.
        var segments = stages.Select(stage => current is null
            ? new StageBarSegment(status.State == JobState.Completed ? stage.State == StageState.Skipped ? "skipped" : "done" : "pending")
            : new StageBarSegment(Segment(stage.State), stage.State is StageState.Active or StageState.Paused ? Fraction(stage.Fraction ?? current.StageFraction) : null)).ToArray();
        status = status with { Segments = segments, RetryStageId = current?.RetryStageId ?? "" };
        if (status.RetryStageId.Length > 0)
            status = status with { RetryStageTitle = stages.FirstOrDefault(stage => stage.Id == status.RetryStageId)?.Title ?? "" };
        var index = current?.CurrentIndex ?? -1;
        var stage = index >= 0 && index < count ? stages[index] : null;
        var position = Localization.Format($"{index + 1}/{count}");
        var overall = current?.OverallFraction is { } o && double.IsFinite(o) ? Percent(Math.Clamp(o, 0, 1) * 100, 0) : "";
        switch (status.State)
        {
            case JobState.Running or JobState.Stopping when stage is not null:
                var detail = stage.Detail.Length > 0 ? Localization.Text(stage.Detail)
                    : Fraction(stage.Fraction ?? current!.StageFraction) is { } f ? Localization.Format($"本阶段 {Percent(f * 100, 0)}") : "";
                return status with
                {
                    Title = Localization.Text(stage.Title), Text = Join(position, detail),
                    Meta = status.State == JobState.Running ? Join(overall, Remaining(current)) : ""
                };
            case JobState.Paused when stage is not null:
                var paused = Fraction(stage.Fraction ?? current!.StageFraction) is { } pf && pf > 0 ? Localization.Format($"本阶段 {Percent(pf * 100, 0)}") : "";
                return status with { Title = Localization.Format($"停在 {Localization.Key(stage.Title)}"), Text = Join(position, paused), Meta = overall };
            case JobState.Failed:
                var failed = current is null ? null : stages.FirstOrDefault(s => s.Id == current.FailedStageId) ?? stage;
                var failedIndex = failed is null ? -1 : IndexOf(stages, failed);
                var reason = error.Length > 0 ? error : Localization.Text("查看任务详情");
                return failed is null
                    ? status with { Text = reason, IsError = true }
                    : status with
                    {
                        Title = Localization.Text(failed.Title), IsError = true,
                        Text = Join(Localization.Format($"{failedIndex + 1}/{count}"), Localization.Format($"失败：{reason}"))
                    };
            case JobState.Cancelled when stage is not null:
                return status with { Title = Localization.Format($"停在 {Localization.Key(stage.Title)}"), Text = position };
            case JobState.Completed:
                var skipped = stages.Count(s => s.State == StageState.Skipped);
                var elapsed = current is null ? "" : Localization.Format($"用时 {Clock(current.Elapsed)}");
                return status with
                {
                    Text = Join(Localization.Format($"{count} 个阶段"), skipped > 0 ? Localization.Format($"跳过 {skipped}") : "", elapsed, completedDetail)
                };
            default:
                return status with { Text = Localization.Format($"{count} 个阶段") };
        }
    }

    private static int IndexOf(IReadOnlyList<JobStageItem> stages, JobStageItem stage)
    { for (var i = 0; i < stages.Count; i++) if (ReferenceEquals(stages[i], stage)) return i; return -1; }
    internal static string Segment(StageState state) => state switch
    {
        StageState.Active => "active", StageState.Paused => "paused", StageState.Done => "done", StageState.Failed => "failed",
        StageState.Skipped => "skipped", StageState.Cancelled => "cancelled", _ => "pending"
    };
    private static double? Fraction(double? value) => value is { } f && double.IsFinite(f) ? Math.Clamp(f, 0, 1) : null;
    internal static string Percent(double value, int digits) => value.ToString(digits == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
    internal static string Remaining(JobStageSummary? summary)
    {
        if (summary is not { State: JobState.Running, Remaining: { } remaining }) return "";
        // The estimate was taken at SnapshotUtc; count it down until the next publish.
        var left = remaining - (DateTime.UtcNow - summary.SnapshotUtc);
        return left > TimeSpan.Zero ? Localization.Format($"剩余 {Clock(left)}") : Localization.Text("正在收尾");
    }
    internal static TimeSpan Elapsed(JobStageSummary summary)
        => summary.Elapsed + (summary.IsClockRunning ? Max(DateTime.UtcNow - summary.SnapshotUtc, TimeSpan.Zero) : TimeSpan.Zero);
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    internal static string Clock(TimeSpan time)
    {
        var seconds = (long)Math.Max(0, Math.Ceiling(time.TotalSeconds));
        return seconds >= 3600 ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }
    internal static string Join(params string[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
