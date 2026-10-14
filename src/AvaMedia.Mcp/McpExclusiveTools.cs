using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

/// <summary>Registered only in workspaces unlocked through the desktop's private activation flow.</summary>
[McpServerToolType]
public sealed class McpExclusiveTools(IMcpWorkspace workspace, McpFiles files)
{
    private void RequireUnlocked()
    {
        if (!workspace.Engine.Settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
    }

    [McpServerTool(Name = "avamedia_preview_vl_rename", Title = "内部 VL 后台命名分析", OpenWorld = false, UseStructuredContent = true)]
    [Description("Queue internal VL naming analysis and return a durable task ID after scanning, without waiting for inference. Requires an unlocked workspace and a fresh UUID requestId; reuse the same ID and arguments after a lost response. mode: local (Qwen3.5), online (sends filenames/folder names and sampled frames to a configured vision provider), or rules. providerId optionally selects the online provider; omitted uses the default. The selected model is saved in the task; credentials stay in user settings. Poll/wait with get_task or wait_task, includeResult=true, and read every nextOffset page for partial/final Files and Plan. pause/resume/stop/retry use control_task; retry retains successful files. Review/edit the plan before apply_rename. Desktop: Video sidebar > VL media renamer > analysis method and provider > background analysis. Closing the tool or disconnecting MCP does not cancel accepted work. Analysis never renames source files.")]
    public Task<McpSubmission> Preview(string requestId, string[] paths, bool recursive = true, string mode = "local",
        string? providerId = null, bool startImmediately = true, string? model = null, CancellationToken ct = default)
    {
        RequireUnlocked();
        Job? job = null;
        return new McpTools(workspace, files).Submit(requestId, new { paths, recursive, mode, providerId, model, startImmediately }, _ =>
        {
            RequireUnlocked();
            var prepared = job ?? throw new InvalidOperationException("VL 任务尚未准备完成。");
            McpTools.ValidatePaths(prepared, files); MediaEngine.Validate(prepared); return [prepared];
        }, startImmediately, ct, async () =>
        {
            job = await Task.Run(() => VlRenameTasks.CreateAnalysis(workspace.Engine.Settings,
                VlMediaRenamer.CollectInputs(paths.Select(path => files.Check(path, true)), recursive, ct), mode, providerId, model), ct).ConfigureAwait(false);
        });
    }

    [McpServerTool(Name = "avamedia_undo_vl_rename", Title = "撤销内部重命名", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Undo a completed avamedia_apply_rename task through its transactional journal. Requires an unlocked workspace. Rejects changed files, collisions and files used by running tasks; updates shared desktop paths. Retry the same taskId after a lost response: alreadyUndone=true means no further mutation. Never supply an arbitrary journal path.")]
    public Task<McpRenameUndo> Undo(Guid taskId, CancellationToken ct = default)
    {
        RequireUnlocked();
        return workspace.UndoRenameAsync(taskId, ct);
    }
}
