using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Single-processing tools keep their existing durable queue/stage checkpoints as the source of truth.</summary>
internal sealed class ProcessingTaskPages(IMultiStageTask task) : ITaskLifecycle
{
    private static readonly PageDescriptor Process = new("process", "处理", PageKind.Processing), Done = new("done", "完成", PageKind.Result);
    public IReadOnlyList<TaskPageStatus> Pages
    {
        get
        {
            var snapshot = task.Snapshot;
            return [new(Process, snapshot.State switch { MultiStageTaskState.Completed => PageState.Completed,
                MultiStageTaskState.Configuring => PageState.Pending, MultiStageTaskState.Failed or MultiStageTaskState.Cancelled => PageState.Failed, _ => PageState.Running })
                { Elapsed = snapshot.Elapsed, Detail = snapshot.Error },
                new(Done, snapshot.State == MultiStageTaskState.Completed ? PageState.Completed : PageState.Pending)];
        }
    }
    public TaskPageStatus CurrentPage => Pages[task.Snapshot.State == MultiStageTaskState.Completed ? 1 : 0];
    public PageState PageState => CurrentPage.State;
    public event EventHandler? Changed { add => task.Changed += value; remove => task.Changed -= value; }
    public void SubmitInteraction(string pageId, object result) => throw new InvalidOperationException("该流程没有交互页。");
    public void Revise(string pageId) => throw new InvalidOperationException("该流程没有交互页。");
}
