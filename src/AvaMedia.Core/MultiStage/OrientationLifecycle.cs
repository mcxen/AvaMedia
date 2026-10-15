using System.Text.Json;

namespace AvaMedia.Core;

public sealed record OrientationOutputChoice(int Rotation, string Format);

/// <summary>Detection, confirmation and export keep the same queue job and durable cursor.</summary>
public static class OrientationLifecycle
{
    public static IReadOnlyList<PageDescriptor> Pages { get; } = [new("process", "方向检测", PageKind.Processing),
        new("choice", "旋转确认", PageKind.Interaction), new("export", "旋转导出", PageKind.Processing), new("done", "完成", PageKind.Result)];

    internal static Job ExportDraft(Job job)
    {
        var choice = job.Lifecycle!.Pages.Single(page => page.Page.Id == "choice").Result?.Deserialize<OrientationOutputChoice>()
            ?? throw new InvalidOperationException("请先确认旋转方向。");
        var result = job.OrientationResult ?? throw new IOException("方向检测结果缺失，请重新检测。");
        return new() { FeatureId = "rotate", Inputs = job.Inputs, Output = job.Output,
            Options = BatchRotate.ResolveOptions(result.Info, choice.Rotation, choice.Format, job.Inputs[0]) };
    }
    internal static async Task ExportAsync(IMediaEngine engine, Job job, TaskLifecycle lifecycle, Action<double> progress, CancellationToken ct)
    {
        lifecycle.StartProcessing("export");
        try
        {
            job.OrientationResult ??= await AiTaskResults.LoadAsync<OrientationTaskResult>(AiTaskResults.PathFor(job, "orientation"), ct)
                ?? throw new IOException("方向检测结果缺失，请重新检测。");
            var source = new FileInfo(job.Inputs[0]); var result = job.OrientationResult;
            if (!source.Exists || source.Length != result.SourceLength || source.LastWriteTimeUtc != result.SourceWriteUtc)
                throw new IOException("源视频已更改，请重新检测方向。");
            var draft = ExportDraft(job);
            draft.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Job.ProgressDetail)) job.ProgressDetail = draft.ProgressDetail; };
            try { await engine.Execute(draft, progress, ct).ConfigureAwait(false); }
            finally { job.AppendLog(await draft.ReadLogAsync().ConfigureAwait(false)); }
            lifecycle.CompleteProcessing("export", BatchRotate.Direction(draft.Options.Rotation));
        }
        catch (Exception error) { lifecycle.Fail(error.Message); throw; }
    }
}
