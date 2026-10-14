using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpModelCheck(string ModelId, bool Installed, bool HashesVerified, string Directory, IReadOnlyList<ModelArtifact> Files);
public sealed record McpModelRelease(bool AllIdleReleased, IReadOnlyList<ModelRuntimeStatus> Runtime);
public sealed record McpModelDeletion(string ModelId, bool HasLocalData);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_inspect_model", Title = "本地模型清单与校验", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a local model's native artifact manifest (paths, sizes, SHA-256, sources) and installation directory. verifyHashes=true explicitly hashes model files and may take time; default checks installation metadata only. Does not download or load inference sessions.")]
    public async Task<McpModelCheck> InspectModel(string modelId, bool verifyHashes = false, CancellationToken ct = default)
    {
        var model = ModelCatalog.Find(modelId); var store = new ModelStore();
        var installed = await store.IsInstalledAsync(modelId, verifyHashes, ct).ConfigureAwait(false);
        return new(modelId, installed, verifyHashes && installed, store.DirectoryFor(modelId), model.Files);
    }

    [McpServerTool(Name = "avamedia_import_model", Title = "后台导入本地模型", Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Queue native ModelStore import of server files/directories for a catalog model. Matches exact sizes/SHA-256, stages and publishes atomically; source files remain untouched. Durable shared queue supports stop/retry and requestId recovery. Does not download or run inference. inspect_model lists required artifacts.")]
    public Task<McpSubmission> ImportModel(string requestId, string modelId, string[] paths, bool startImmediately = true, CancellationToken ct = default)
    {
        _ = ModelCatalog.Find(modelId);
        if (paths.Length == 0) throw new ArgumentException("请选择模型文件或目录。");
        var inputs = paths.Select(path => _files.Check(path, true)).Distinct(BatchRename.PathComparer).ToArray();
        return Submit(requestId, new { modelId, paths, startImmediately }, _ =>
        {
            var job = new Job { FeatureId = "model-download", Inputs = inputs,
                DownloadTitle = "导入 · " + ModelCatalog.Find(modelId).Name,
                Options = new() { Format = "json", DownloadModelId = modelId, ImportModelPaths = inputs.ToArray() } };
            job.Output = AiTaskResults.PathFor(job, "model-download"); ValidatePaths(job, _files); MediaEngine.Validate(job); return [job];
        }, startImmediately, ct);
    }

    [McpServerTool(Name = "avamedia_release_models", Title = "释放空闲模型", Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Release idle resident tag/semantic/VL/summary model sessions using the desktop lifecycle service. Keeps installed model files. Active inference remains in use; allIdleReleased=false means some sessions could not be released. Does not stop tasks or disable future model loading; set prewarmLocalModels=false separately if desired.")]
    public async Task<McpModelRelease> ReleaseModels(CancellationToken ct = default)
    {
        var store = new ModelStore();
        var released = await MediaTagRuntime.ReleaseAsync(store.Root, ct).ConfigureAwait(false);
        return new(released, MediaTagRuntime.Snapshot(store.Root));
    }

    [McpServerTool(Name = "avamedia_delete_model", Title = "卸载本地模型", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete installed and partial-download data for one catalog model through native ModelStore. Releases idle cached sessions; the native file gate protects in-use/downloaded models. This deletes model files, not source media or online configuration. Disable automatic repair-model download first when removing that model to prevent later automatic installation.")]
    public async Task<McpModelDeletion> DeleteModel(string modelId, CancellationToken ct = default)
    {
        _ = ModelCatalog.Find(modelId); var store = new ModelStore();
        await store.DeleteAsync(modelId, ct).ConfigureAwait(false);
        return new(modelId, store.HasLocalData(modelId));
    }
}
