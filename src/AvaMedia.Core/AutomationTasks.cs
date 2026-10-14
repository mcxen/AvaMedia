namespace AvaMedia.Core;

public sealed record RenameTaskOptions(RenameItem[] Plan);
public sealed record FolderOrganizationTaskOptions(string OutputFolder, FolderOrganizationItem[] Plan);

public sealed partial class ConversionOptions
{
    public RenameTaskOptions? Rename { get; set; }
    public ContactSheetOptions? ContactSheet { get; set; }
    public FolderOrganizationTaskOptions? FolderOrganization { get; set; }
    public string? DownloadModelId { get; set; }
}

public partial class Job
{
    [System.Text.Json.Serialization.JsonIgnore]
    public RenameItem[]? CommittedFileChanges { get; set; }
}

/// <summary>Queue execution for tools which previously ran only inside their own windows.</summary>
public static class AutomationTasks
{
    public static bool Supports(Job job) => job.FeatureId is "batch-rename" or "contact-sheet" or "folder-organize" or "model-download";
    public static void Validate(Job job)
    {
        if (job.Options.VlRename is not null) VlRenameTasks.Validate(job);
        else if (job.FeatureId == "folder-organize")
        {
            var spec = job.Options.FolderOrganization ?? throw new ArgumentException("缺少分类整理计划。");
            if (!Path.IsPathFullyQualified(spec.OutputFolder) || spec.Plan.Length == 0
                || !job.Inputs.SequenceEqual(spec.Plan.Select(item => item.File.Media.Path), BatchRename.PathComparer))
                throw new ArgumentException("分类整理任务的文件和计划不一致。");
            foreach (var item in spec.Plan)
            {
                MediaTagService.ValidateSource(item.File.Media);
                if (!Path.IsPathFullyQualified(item.Target) || !FolderClassification.IsWithin(item.Target, spec.OutputFolder)
                    || item.ReportPath is not null && item.ReportPath != item.Target + ".tags.txt")
                    throw new ArgumentException("分类整理目标无效。");
            }
            if (!BatchRename.PathComparer.Equals(job.Output, AiTaskResults.PathFor(job, "organization-journal")))
                throw new ArgumentException("分类整理日志路径无效。");
        }
        else if (job.FeatureId == "model-download")
        {
            var model = ModelCatalog.Find(job.Options.DownloadModelId ?? throw new ArgumentException("缺少模型 ID。"));
            if (!model.Supported) throw new ArgumentException("当前平台不支持此模型。");
            if (!BatchRename.PathComparer.Equals(job.Output, AiTaskResults.PathFor(job, "model-download")))
                throw new ArgumentException("模型下载结果路径无效。");
        }
        else if (job.FeatureId == "batch-rename")
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
            BatchVideoTools.ValidateContactSheet(spec);
        }
    }

    public static async Task ExecuteAsync(IMediaEngine engine, Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job); ct.ThrowIfCancellationRequested(); progress(0);
        if (job.Options.VlRename is not null)
            await new VlRenameTasks(engine).ExecuteAsync(job, progress, ct).ConfigureAwait(false);
        else if (job.FeatureId == "folder-organize")
        {
            var result = await FolderOrganization.ExecuteAsync(job.Options.FolderOrganization!.Plan, false, job.Output,
                new OrganizationProgress(value =>
                {
                    job.ProgressDetail = $"复制 {value.Completed} / {value.Total} · {Path.GetFileName(value.Source)}";
                    progress(100d * value.Completed / Math.Max(1, value.Total));
                }), ct).ConfigureAwait(false);
            job.OutputIsPartial = result.Cancelled || ct.IsCancellationRequested || result.Errors.Length > 0;
            await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "organization-result"), result, CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.Errors.Length > 0) throw new IOException(string.Join(Environment.NewLine, result.Errors));
            job.ProgressDetail = $"已复制 {result.Completed} 个文件";
        }
        else if (job.FeatureId == "model-download")
        {
            var id = job.Options.DownloadModelId!;
            var store = new ModelStore();
            if (!await store.IsInstalledAsync(id, ct: ct).ConfigureAwait(false))
                await store.DownloadAsync(id, new DownloadProgress(value =>
                {
                    job.ProgressDetail = value.Stage;
                    job.Activity = new(value.Stage, ModelCatalog.Find(id).Name, DateTime.UtcNow, DateTime.UtcNow)
                    { Current = value.Received, Total = value.Total, Unit = "字节", Detail = value.SourceName };
                    progress(value.Percent);
                }), ct).ConfigureAwait(false);
            await AiTaskResults.SaveAsync(job.Output, new { ModelId = id, Directory = store.DirectoryFor(id), Installed = true }, ct).ConfigureAwait(false);
            job.ProgressDetail = "模型已安装";
        }
        else if (job.FeatureId == "batch-rename")
        {
            // ApplyRename is a transactional operation; cancellation is observed before admission.
            // Do not interrupt between its staging and commit/rollback phases.
            var changed = await Task.Run(() => BatchRename.ApplyRename(job.Options.Rename!.Plan, job.Output), CancellationToken.None).ConfigureAwait(false);
            job.FileChangesCommitted = true;
            job.CommittedFileChanges = changed;
            job.ProgressDetail = $"重命名 {changed.Length} 个文件";
        }
        else
        {
            var spec = job.Options.ContactSheet!;
            if (File.Exists(job.Output) || Directory.Exists(job.Output)) throw new IOException("输出已存在，请重试生成新的名称。");
            var parent = Path.GetDirectoryName(Path.GetFullPath(job.Output))!;
            Directory.CreateDirectory(parent);
            // Stage on the destination filesystem so a multi-sheet result is published atomically.
            var folder = Path.Combine(parent, ".AvaMedia-sheet-" + Guid.NewGuid().ToString("N"));
            try
            {
                var outputs = await BatchVideoTools.GenerateContactSheets(engine, job.Inputs[0], folder,
                    spec, new SheetProgress(value => { job.ProgressDetail = value.Message; progress(value.Percent); }), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (spec.SheetsPerVideo == 1) File.Move(outputs.Single(), job.Output, false);
                else Directory.Move(folder, job.Output);
                job.ProgressDetail = $"已生成 {outputs.Length} 张拼图";
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
        progress(100);
    }
    private sealed class SheetProgress(Action<ContactSheetProgress> report) : IProgress<ContactSheetProgress>
    { public void Report(ContactSheetProgress value) => report(value); }
    private sealed class OrganizationProgress(Action<FolderOrganizationProgress> report) : IProgress<FolderOrganizationProgress>
    { public void Report(FolderOrganizationProgress value) => report(value); }
    private sealed class DownloadProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => report(value); }
}
