using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpVlRenamePreview(RenameItem[] Plan, VlMediaRenameSuggestion[] Suggestions, string Model, string Backend);

/// <summary>Registered only in workspaces unlocked through the desktop's private activation flow.</summary>
[McpServerToolType]
public sealed class McpExclusiveTools(IMcpWorkspace workspace, McpFiles files)
{
    private void RequireUnlocked()
    {
        if (!workspace.Engine.Settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
    }

    [McpServerTool(Name = "avamedia_preview_vl_rename", Title = "内部 VL 命名预览", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Analyze local media using the internal desktop renamer's folder/filename rules and local Qwen3.5 visual model. mode is local or rules. Requires an already unlocked workspace. Local inference uses three video frames or one image; no media is uploaded. Install required models with download_model first. Returns seven-segment names, evidence and a versioned collision-checked plan; review/edit targets then pass plan to avamedia_apply_rename. This preview may take several minutes and changes no source files.")]
    public async Task<McpVlRenamePreview> Preview(string[] paths, bool recursive = true, string mode = "local", CancellationToken ct = default)
    {
        RequireUnlocked();
        if (mode is not ("local" or "rules")) throw new ArgumentException("分析模式须为 local 或 rules。");
        var inputs = VlMediaRenamer.CollectInputs(paths.Select(path => files.Check(path, true)), recursive, ct);
        if (inputs.Length == 0) throw new ArgumentException("没有可分析的图片或视频。");
        await using var model = mode == "local"
            ? await SharedSummaryModelLease.AcquireAsync(new ModelStore(), ModelCatalog.SummaryQwen35Id, ct).ConfigureAwait(false) : null;
        var renamer = new VlMediaRenamer(workspace.Engine);
        var suggestions = new List<VlMediaRenameSuggestion>();
        foreach (var input in inputs)
        {
            RequireUnlocked();
            suggestions.Add(await renamer.SuggestAsync(input.Path, model, ct, input.FolderContext).ConfigureAwait(false));
        }
        foreach (var group in suggestions.GroupBy(item => Path.GetDirectoryName(item.Item.Source)!, BatchRename.PathComparer))
        {
            var reserved = Directory.EnumerateFileSystemEntries(group.Key).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var suggestion in group)
            {
                var name = Path.GetFileName(suggestion.Item.Target); var initial = name; var number = 2;
                if (!StringComparer.OrdinalIgnoreCase.Equals(name, Path.GetFileName(suggestion.Item.Source)))
                    while (reserved.Contains(name)) name = VlMediaNaming.WithConflictNumber(initial, suggestion.Identifier, number++);
                reserved.Add(name);
                suggestions[suggestions.IndexOf(suggestion)] = suggestion with { Item = suggestion.Item with { Target = Path.Combine(group.Key, name) } };
            }
        }
        var plan = suggestions.Select(suggestion => suggestion.Item).ToArray();
        BatchRename.ValidateRenamePlan(plan);
        return new(plan, suggestions.ToArray(), model?.ModelId ?? "rules", model?.Backend ?? "filename/folder rules");
    }

    [McpServerTool(Name = "avamedia_undo_vl_rename", Title = "撤销内部重命名", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Undo a completed avamedia_apply_rename task through its transactional journal. Requires an unlocked workspace. Rejects changed files, collisions and files used by running tasks; updates shared desktop paths. Retry the same taskId after a lost response: alreadyUndone=true means no further mutation. Never supply an arbitrary journal path.")]
    public Task<McpRenameUndo> Undo(Guid taskId, CancellationToken ct = default)
    {
        RequireUnlocked();
        return workspace.UndoRenameAsync(taskId, ct);
    }
}
