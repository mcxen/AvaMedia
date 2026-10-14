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
    [Description("Preview names with the internal VL renamer. mode: local (Qwen3.5, local only; install via download_model), online (configured OpenAI-compatible vision provider; sends folder/file names and sampled frames to that provider), or rules (folder/filename only). Desktop path: Video sidebar > VL media renamer > Analysis method = Online VL > select provider; Model Settings opens Settings > Online AI. For MCP online mode, providerId optionally selects an enabled provider; omitted uses the default. Requires an already unlocked workspace. Uses three video frames or one image. Returns seven-segment names, evidence and a collision-checked plan; review/edit targets then pass plan to avamedia_apply_rename. Preview changes no source files.")]
    public async Task<McpVlRenamePreview> Preview(string[] paths, bool recursive = true, string mode = "local", string? providerId = null, CancellationToken ct = default)
    {
        RequireUnlocked();
        if (mode is not ("local" or "online" or "rules")) throw new ArgumentException("分析模式须为 local、online 或 rules。");
        var inputs = VlMediaRenamer.CollectInputs(paths.Select(path => files.Check(path, true)), recursive, ct);
        if (inputs.Length == 0) throw new ArgumentException("没有可分析的图片或视频。");
        ISummaryModel? model = mode switch
        {
            "local" => await SharedSummaryModelLease.AcquireAsync(new ModelStore(), ModelCatalog.SummaryQwen35Id, ct).ConfigureAwait(false),
            "online" => OpenOnlineModel(providerId),
            _ => null
        };
        try
        {
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
        finally { if (model is not null) await model.DisposeAsync().ConfigureAwait(false); }
    }

    private ISummaryModel OpenOnlineModel(string? providerId)
    {
        var provider = workspace.Engine.Settings.OnlineAi.Resolve(providerId);
        if (string.IsNullOrWhiteSpace(provider.EffectiveVisionModel))
            throw new ArgumentException("线上 VL 路径：视频侧栏 → VL 媒体智能重命名 → 模型设置… → 在线 AI；返回后将分析方式切为“线上 VL”。");
        return new OnlineSummaryModel(provider, vision: true);
    }

    [McpServerTool(Name = "avamedia_undo_vl_rename", Title = "撤销内部重命名", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Undo a completed avamedia_apply_rename task through its transactional journal. Requires an unlocked workspace. Rejects changed files, collisions and files used by running tasks; updates shared desktop paths. Retry the same taskId after a lost response: alreadyUndone=true means no further mutation. Never supply an arbitrary journal path.")]
    public Task<McpRenameUndo> Undo(Guid taskId, CancellationToken ct = default)
    {
        RequireUnlocked();
        return workspace.UndoRenameAsync(taskId, ct);
    }
}
