namespace AvaMedia.Core;

public sealed record RenameTaskOptions(RenameItem[] Plan);
public sealed record ContactSheetTaskOptions(int Columns = 4, int Rows = 4, int CellWidth = 320);

public sealed partial class ConversionOptions
{
    public RenameTaskOptions? Rename { get; set; }
    public ContactSheetTaskOptions? ContactSheet { get; set; }
}

/// <summary>Queue execution for tools which previously ran only inside their own windows.</summary>
public static class AutomationTasks
{
    public static bool Supports(Job job) => job.FeatureId is "batch-rename" or "contact-sheet";
    public static void Validate(Job job)
    {
        if (job.FeatureId == "batch-rename")
        {
            var plan = job.Options.Rename?.Plan ?? throw new ArgumentException("缺少重命名计划。");
            if (plan.Length == 0 || !job.Inputs.SequenceEqual(plan.Select(item => item.Source), BatchRename.PathComparer))
                throw new ArgumentException("重命名任务的文件和计划不一致。");
            BatchRename.ValidateRenamePlan(plan);
        }
        else
        {
            var spec = job.Options.ContactSheet ?? throw new ArgumentException("缺少多宫格参数。");
            if (job.Inputs.Length != 1 || !VideoFormats.IsVideo(job.Inputs[0]) || !File.Exists(job.Inputs[0]))
                throw new ArgumentException("多宫格任务须包含一个视频。");
            if (spec.Columns is < 1 or > 12 || spec.Rows is < 1 or > 12 || spec.CellWidth is < 80 or > 1920)
                throw new ArgumentException("多宫格参数超出范围。");
        }
    }

    public static async Task ExecuteAsync(IMediaEngine engine, Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job); ct.ThrowIfCancellationRequested(); progress(0);
        if (job.FeatureId == "batch-rename")
        {
            // ApplyRename is a transactional operation; cancellation is observed before admission.
            // Do not interrupt between its staging and commit/rollback phases.
            var changed = await Task.Run(() => BatchRename.ApplyRename(job.Options.Rename!.Plan, job.Output), CancellationToken.None).ConfigureAwait(false);
            job.FileChangesCommitted = true;
            job.ProgressDetail = $"重命名 {changed.Length} 个文件";
        }
        else
        {
            var spec = job.Options.ContactSheet!;
            var folder = Path.Combine(Path.GetTempPath(), "AvaMedia-sheet-" + job.Id.ToString("N"));
            try
            {
                var outputs = await BatchVideoTools.GenerateContactSheets(engine, job.Inputs[0], folder,
                    new(spec.Columns, spec.Rows, spec.CellWidth, Math.Min(spec.CellWidth, 180), Format: "png"),
                    new SheetProgress(value => progress(value.Percent)), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.GetDirectoryName(job.Output)!);
                File.Move(outputs.Single(), job.Output, false);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        progress(100);
    }
    private sealed class SheetProgress(Action<ContactSheetProgress> report) : IProgress<ContactSheetProgress>
    { public void Report(ContactSheetProgress value) => report(value); }
}
