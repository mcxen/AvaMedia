using System.Text.Json;
using AvaMedia.Core;
using Xunit;

namespace AvaMedia.PersonClipTests;

public sealed class TaskLifecycleTests
{
    private static readonly PageDescriptor[] Plan = [new("process", "分析", PageKind.Processing),
        new("adjust", "调整", PageKind.Interaction), new("generate", "生成", PageKind.Processing), new("done", "完成", PageKind.Result)];

    [Fact]
    public void AdvancesInOrderAndWaitsForSubmission()
    {
        using var lifecycle = new TaskLifecycle(new Job(), Plan);
        var notifications = 0; lifecycle.Changed += (_, _) => notifications++;
        Assert.Throws<InvalidOperationException>(() => lifecycle.SubmitInteraction("adjust", new { Keep = 3 }));
        lifecycle.StartProcessing("process"); lifecycle.CompleteProcessing("process");
        Assert.Equal(PageState.AwaitingUser, lifecycle.PageState);
        Assert.Equal(PageState.Pending, lifecycle.Pages[2].State);
        lifecycle.SubmitInteraction("adjust", new { Keep = 3 });
        Assert.Equal(3, lifecycle.Pages[1].Result!.Value.GetProperty("Keep").GetInt32());
        lifecycle.StartProcessing("generate"); lifecycle.CompleteProcessing("generate");
        Assert.Equal("done", lifecycle.CurrentPage.Page.Id);
        Assert.All(lifecycle.Pages, page => Assert.Equal(PageState.Completed, page.State));
        Assert.Equal(5, notifications);
    }

    [Fact]
    public void RevisionRetainsEarlierPagesAndClearsEveryLaterPage()
    {
        using var lifecycle = new TaskLifecycle(new Job(), Plan);
        lifecycle.StartProcessing("process"); lifecycle.CompleteProcessing("process");
        lifecycle.SubmitInteraction("adjust", new { Keep = 3 });
        lifecycle.StartProcessing("generate");
        Assert.Throws<InvalidOperationException>(() => lifecycle.Revise("adjust"));
        lifecycle.CompleteProcessing("generate"); lifecycle.Revise("adjust");
        Assert.Equal(PageState.Completed, lifecycle.Pages[0].State);
        Assert.Equal(PageState.AwaitingUser, lifecycle.Pages[1].State);
        Assert.All(lifecycle.Pages.Skip(2), page => { Assert.Equal(PageState.Pending, page.State); Assert.Null(page.Result); });
        lifecycle.SubmitInteraction("adjust", new { Keep = 1 });
        Assert.Equal("generate", lifecycle.CurrentPage.Page.Id);
        Assert.Equal(1, lifecycle.Pages[1].Result!.Value.GetProperty("Keep").GetInt32());
    }

    [Fact]
    public void RestoresCursorAndSubmittedResultsFromQueueJson()
    {
        var job = new Job();
        using (var lifecycle = new TaskLifecycle(job, Plan))
        {
            lifecycle.StartProcessing("process"); lifecycle.CompleteProcessing("process");
            lifecycle.SubmitInteraction("adjust", new { Keep = 2 });
        }
        var restored = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        using var resumed = new TaskLifecycle(restored, Plan);
        Assert.Equal("generate", resumed.CurrentPage.Page.Id);
        Assert.Equal(2, resumed.Pages[1].Result!.Value.GetProperty("Keep").GetInt32());
        resumed.Revise("adjust");
        var waiting = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(restored))!;
        using var reopened = new TaskLifecycle(waiting, Plan);
        Assert.Equal(PageState.AwaitingUser, reopened.PageState);
        Assert.Equal("adjust", reopened.CurrentPage.Page.Id);
    }

    [Fact]
    public async Task AwaitingInteractionReleasesQueueExecution()
    {
        var first = new Job(); var second = new Job();
        var queue = new QueueService(new WaitingExecutor(first));
        await queue.Run([first, second]);
        Assert.Equal(JobState.AwaitingUser, first.State);
        Assert.False(queue.IsExecuting(first));
        Assert.Equal(JobState.Completed, second.State);
    }

    private sealed class WaitingExecutor(Job first) : IJobExecutor
    {
        public Task Execute(Job job, Action<double> progress, CancellationToken ct)
        {
            if (job == first)
            {
                using var lifecycle = new TaskLifecycle(job, Plan);
                lifecycle.StartProcessing("process"); lifecycle.CompleteProcessing("process");
            }
            return Task.CompletedTask;
        }
    }
}
