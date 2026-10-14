using System.Text.Json;
using AvaMedia.Core;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private sealed partial class DesktopMcpWorkspace
    {
        private McpTaskOptions OptionsView(Job job)
        {
            if (job.Options.VlRename is not null && !owner._settings.ExclusiveToolUnlocked)
                throw new InvalidOperationException("内部工具未激活。");
            return new(job.Id, job.FeatureId, job.Options.Clone(), job.InputOptions?.Select(o => o.Clone()).ToArray(), job.Output)
            { UserOutput = job.UserOutput, InternalOutput = job.HasInternalOutput };
        }
        public Task<McpTaskOptions> TaskOptionsAsync(Guid id, CancellationToken ct) => Ui(() => OptionsView(Find(id)), ct);

        public async Task<McpTaskOptions> UpdateTaskOptionsAsync(Guid id, JsonElement patch, string? outputFolder, CancellationToken ct)
        {
            var result = await Ui(() =>
            {
                var job = Find(id);
                if (!owner.CanStartTask(job) || job.Progress != 0 || job.Activity is not null || job.InputOptions is { Count: > 0 }
                    || Directory.Exists(Path.GetDirectoryName(AiTaskResults.PathFor(job, "tags")))
                    || job.Options.FolderClassification is not null && Directory.Exists(FolderClassificationTaskStore.Folder(job)))
                    throw new InvalidOperationException("只能编辑尚未开始、未排入调度且无独立文件草稿的等待任务。");
                if (job.FeatureId is "batch-rename" or "folder-organize" or "model-download")
                    throw new ArgumentException("此任务须通过专用计划接口重新创建。");
                var options = McpOptionMerge.Apply(job.Options, patch);
                if (options.VlRename is not null || options.Rename is not null || options.FolderOrganization is not null || options.DownloadModelId is not null
                    || options.ImportModelPaths is not null || options.DownloadModelSource is not null)
                    throw new ArgumentException("不能通过编辑任务注入文件操作或模型安装计划。");
                var feature = Catalog.Find(job.FeatureId);
                options = SourceOutput.Resolve(feature, options, job.Inputs.FirstOrDefault());
                var draft = new Job { Id = job.Id, FeatureId = job.FeatureId, Inputs = job.Inputs.ToArray(), Options = options, Output = job.Output };
                if (draft.Options.FolderClassification is { } classification && feature.Operation == Operation.FolderClassify)
                {
                    if (outputFolder is not null) classification.OutputFolder = new McpFiles().Check(outputFolder);
                    draft.Output = FolderClassificationTaskStore.Folder(draft);
                }
                else if (draft.HasInternalOutput)
                {
                    if (outputFolder is not null) throw new ArgumentException("分析结果使用内部目录，不能重定向输出。");
                    draft.Output = AiTaskResults.InternalOutputFor(draft);
                }
                else if (outputFolder is not null || options.Format != job.Options.Format || job.HasInternalOutput)
                {
                    var folder = new McpFiles().Check(outputFolder ?? (job.HasInternalOutput ? owner._settings.OutputFolder : Path.GetDirectoryName(job.Output)!));
                    draft.Output = MediaEngine.UniqueOutput(folder, Path.GetFileNameWithoutExtension(job.HasInternalOutput ? job.Inputs[0] : job.Output), options.Format,
                        owner._jobs.Where(other => other != job).Select(other => other.Output).Concat(job.Inputs), Catalog.DirectoryOutput(draft));
                }
                McpTools.ValidatePaths(draft, new()); MediaEngine.Validate(draft);
                job.Options = draft.Options; job.Output = draft.Output;
                owner.Save(); owner.Refresh(); return OptionsView(job);
            }, ct).ConfigureAwait(false);
            await owner.PersistenceReady.ConfigureAwait(false);
            return result;
        }

        public async Task<McpTaskView> MoveTaskAsync(Guid id, string position, CancellationToken ct)
        {
            var result = await Ui(() =>
            {
                var job = Find(id);
                if (!owner.CanMoveTask(job)) throw new InvalidOperationException("当前任务不能调整顺序。");
                var index = owner._jobs.IndexOf(job);
                var target = position switch
                {
                    "top" => 0, "up" => Math.Max(0, index - 1),
                    "down" => Math.Min(owner._jobs.Count - 1, index + 1), "bottom" => owner._jobs.Count - 1,
                    _ => throw new ArgumentException("position 须为 top/up/down/bottom。")
                };
                owner._jobs.Move(index, target); owner._queue.ReorderPending(owner._jobs); owner.Save(); owner.Refresh(); return View(job);
            }, ct).ConfigureAwait(false);
            await owner.PersistenceReady.ConfigureAwait(false); return result;
        }
    }
}
