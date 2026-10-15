using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public enum PageKind { Processing, Interaction, Result }
public enum PageState { Pending, Running, AwaitingUser, Completed, Failed }
public sealed record PageDescriptor(string Id, string Title, PageKind Kind);
public sealed record TaskPageStatus(PageDescriptor Page, PageState State = PageState.Pending)
{
    public JsonElement? Result { get; init; }
    public string Detail { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Durable, immutable checkpoint, serialized with the queue. Viewing a page does not change this cursor.</summary>
public sealed record TaskLifecycleSnapshot
{
    public IReadOnlyList<TaskPageStatus> Pages { get; init; } = [];
    public int CurrentIndex { get; init; }
    public DateTime? RunningSinceUtc { get; init; }
    public long Revision { get; init; }
    [JsonIgnore] public TaskPageStatus Current => Pages[CurrentIndex];
    [JsonIgnore] public bool AwaitingUser => Current.State == PageState.AwaitingUser;
}

public interface ITaskLifecycle
{
    IReadOnlyList<TaskPageStatus> Pages { get; }
    TaskPageStatus CurrentPage { get; }
    PageState PageState { get; }
    event EventHandler? Changed;
    void SubmitInteraction(string pageId, object result);
    void Revise(string pageId);
}

public sealed partial class Job
{
    internal readonly object LifecycleGate = new();
    private TaskLifecycleSnapshot? _lifecycle;
    public TaskLifecycleSnapshot? Lifecycle
    {
        get => Volatile.Read(ref _lifecycle);
        set { if (!ReferenceEquals(_lifecycle, value)) { Volatile.Write(ref _lifecycle, value); Raise(nameof(Lifecycle)); } }
    }
}

/// <summary>
/// Queue-owned page cursor. No worker, model lease or timer is held while awaiting interaction.
/// Processing code completes its page and returns; the host schedules the next processing page after submission.
/// All mutations are serialized on the owning job, including observers created by different windows.
/// </summary>
public sealed class TaskLifecycle : ITaskLifecycle, IDisposable
{
    private readonly Job _job;
    private readonly TimeProvider _time;
    public TaskLifecycle(Job job, IEnumerable<PageDescriptor> pages, TimeProvider? time = null)
    {
        _job = job; _time = time ?? TimeProvider.System;
        var plan = pages.ToArray();
        if (plan.Length == 0 || plan.Any(p => string.IsNullOrWhiteSpace(p.Id)) || plan.Select(p => p.Id).Distinct().Count() != plan.Length
            || plan[^1].Kind != PageKind.Result || plan.Take(plan.Length - 1).Any(p => p.Kind == PageKind.Result))
            throw new ArgumentException("任务需要唯一的页标识及最后一个完成页。", nameof(pages));
        lock (job.LifecycleGate)
        {
            if (job.Lifecycle is { } saved)
            {
                if (!saved.Pages.Select(p => p.Page).SequenceEqual(plan) || saved.CurrentIndex < 0 || saved.CurrentIndex >= plan.Length)
                    throw new InvalidOperationException("任务页与已保存的流程不一致。");
            }
            else job.Lifecycle = new() { Pages = Array.AsReadOnly(plan.Select(p => new TaskPageStatus(p)).ToArray()) };
        }
        job.PropertyChanged += JobChanged;
    }
    public TaskLifecycleSnapshot Snapshot => _job.Lifecycle!;
    public IReadOnlyList<TaskPageStatus> Pages => Snapshot.Pages;
    public TaskPageStatus CurrentPage => Snapshot.Current;
    public PageState PageState => CurrentPage.State;
    public event EventHandler? Changed;
    private void JobChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(Job.Lifecycle)) Changed?.Invoke(this, EventArgs.Empty); }

    public void StartProcessing(string pageId)
    {
        lock (_job.LifecycleGate)
        {
            var state = Snapshot; RequireCurrent(state, pageId, PageKind.Processing);
            if (state.Current.State == PageState.Completed) throw new InvalidOperationException("此页已经完成。");
            if (state.Current.State == PageState.Running) return;
            Publish(state, state.Pages.Select((p, i) => i == state.CurrentIndex ? p with { State = PageState.Running, Detail = "" } : p).ToArray(), state.CurrentIndex, Now);
        }
    }
    public void CompleteProcessing(string pageId, string detail = "")
    {
        lock (_job.LifecycleGate)
        {
            var state = Snapshot; RequireCurrent(state, pageId, PageKind.Processing);
            if (state.Current.State != PageState.Running) throw new InvalidOperationException("处理页尚未运行。");
            Advance(state, null, detail);
        }
    }
    public void SubmitInteraction(string pageId, object result)
    {
        // Serialize before changing state so an invalid result leaves the checkpoint untouched.
        var json = JsonSerializer.SerializeToElement(result);
        lock (_job.LifecycleGate)
        {
            var state = Snapshot; RequireCurrent(state, pageId, PageKind.Interaction);
            if (!state.AwaitingUser) throw new InvalidOperationException("此页尚未等待提交。");
            Advance(state, json, "已提交");
        }
    }
    public void Revise(string pageId)
    {
        lock (_job.LifecycleGate)
        {
            var state = Snapshot;
            var index = state.Pages.ToList().FindIndex(p => p.Page.Id == pageId);
            if (index < 0 || state.Pages[index].Page.Kind != PageKind.Interaction || state.Pages[index].State != PageState.Completed
                || state.Current.State == PageState.Running)
                throw new InvalidOperationException("运行中不能修改，或此页尚未提交。");
            var pages = state.Pages.Select((p, i) => i < index ? p : i == index
                ? p with { State = PageState.AwaitingUser } : new TaskPageStatus(p.Page)).ToArray();
            Publish(state, pages, index, null);
        }
    }
    public void Fail(string reason)
    {
        lock (_job.LifecycleGate)
        {
            var state = Snapshot;
            Publish(state, state.Pages.Select((p, i) => i == state.CurrentIndex
                ? p with { State = PageState.Failed, Detail = reason, Elapsed = Elapsed(state) } : p).ToArray(), state.CurrentIndex, null);
        }
    }
    public void Reset()
    {
        lock (_job.LifecycleGate)
        {
            var state = Snapshot;
            Publish(state, state.Pages.Select(p => new TaskPageStatus(p.Page)).ToArray(), 0, null);
        }
    }
    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private TimeSpan Elapsed(TaskLifecycleSnapshot state) => state.Current.Elapsed
        + (state.RunningSinceUtc is { } start ? TimeSpan.FromTicks(Math.Max(0, (Now - start).Ticks)) : TimeSpan.Zero);
    private static void RequireCurrent(TaskLifecycleSnapshot state, string id, PageKind kind)
    { if (state.Current.Page.Id != id || state.Current.Page.Kind != kind) throw new InvalidOperationException("请按顺序完成当前页。"); }
    private void Advance(TaskLifecycleSnapshot state, JsonElement? result, string detail)
    {
        var pages = state.Pages.ToArray();
        pages[state.CurrentIndex] = state.Current with { State = PageState.Completed, Result = result, Detail = detail, Elapsed = Elapsed(state) };
        var next = state.CurrentIndex + 1;
        pages[next] = pages[next] with { State = pages[next].Page.Kind switch
        { PageKind.Interaction => PageState.AwaitingUser, PageKind.Result => PageState.Completed, _ => PageState.Pending } };
        Publish(state, pages, next, null);
    }
    private void Publish(TaskLifecycleSnapshot previous, TaskPageStatus[] pages, int index, DateTime? since)
        => _job.Lifecycle = previous with { Pages = Array.AsReadOnly(pages), CurrentIndex = index, RunningSinceUtc = since, Revision = previous.Revision + 1 };
    public void Dispose() { _job.PropertyChanged -= JobChanged; Changed = null; }
}
