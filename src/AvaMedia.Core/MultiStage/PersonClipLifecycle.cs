using System.Text.Json;

namespace AvaMedia.Core;

public sealed record PersonClipMergeChoice(string Preset, bool Fade);

/// <summary>Page plan and queue-side merge using the same export preparation as the segment editor.</summary>
public static class PersonClipLifecycle
{
    public static IReadOnlyList<PageDescriptor> Pages { get; } = [new("process", "分析处理", PageKind.Processing),
        new("adjust", "调整片段", PageKind.Interaction), new("merge", "合并确认", PageKind.Interaction), new("done", "完成", PageKind.Result)];

    public static ClipEditResult Edit(Job job) => job.Lifecycle!.Pages.Single(p => p.Page.Id == "adjust").Result?.Deserialize<ClipEditResult>()
        ?? throw new InvalidOperationException("请先提交调整片段。");

    public static Job PrepareMerge(Job job, PersonClipMergeChoice choice, string folder, IEnumerable<string>? reserved = null)
    {
        var edit = Edit(job);
        if (choice.Fade)
        {
            edit = edit with { Segments = edit.OutputSegments.Select(segment =>
            { var option = segment.Clone(); option.FadeIn = option.FadeOut = Math.Min(.25, (option.End - option.Start) / 4); return option; }).ToArray() };
        }
        var preset = choice.Fade && choice.Preset == "Fast Copy" ? SourceOutput.Original : choice.Preset;
        return QuickClipWorkflow.PrepareJoinedJobs([edit], preset, new(), folder, false, "保留片段", reserved).Single();
    }

    internal static async Task MergeAsync(IMediaEngine engine, Job job, TaskLifecycle lifecycle, Action<double> progress, CancellationToken ct)
    {
        lifecycle.StartProcessing("merge");
        try
        {
            var choice = lifecycle.CurrentPage.Result!.Value.Deserialize<PersonClipMergeChoice>()!;
            var detected = job.PersonDetectionResult ?? await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(AiTaskResults.PathFor(job, "people"), ct);
            var source = new FileInfo(job.Inputs[0]);
            if (detected is null || !source.Exists || source.Length != detected.SourceLength || source.LastWriteTimeUtc != detected.SourceWriteUtc)
                throw new IOException("源视频已更改，请重新检测。");
            var draft = PrepareMerge(job, choice, Path.GetDirectoryName(job.Output)!);
            draft.Output = job.Output;
            var summary = await PersonClipRetry.LoadAsync<PersonClipStageSummary>(AiTaskResults.PathFor(job, "people-stages"), ct);
            var stages = new PersonClipTaskState(job); job.PersonClipStages = stages;
            if (summary is not null) foreach (var stage in PersonClipStageIds.All.Take(4)) stages.Reuse(stage.Id, summary);
            stages.Start(PersonClipStageIds.Export);
            try
            {
                await engine.Execute(draft, value =>
                {
                    stages.Update(value, 100, "%", $"合并片段 {value:0}%"); job.ProgressDetail = $"合并片段 {value:0}%";
                    progress(value);
                }, ct).ConfigureAwait(false);
                stages.Complete(PersonClipStageIds.Export, "合并完成"); stages.Finish(JobState.Completed);
                lifecycle.CompleteProcessing("merge", choice.Fade ? "淡入淡出 · 合并完成" : "无转场 · 合并完成");
            }
            finally { job.AppendLog(await draft.ReadLogAsync(ct).ConfigureAwait(false)); }
        }
        catch (Exception error) { lifecycle.Fail(error.Message); throw; }
    }
}
