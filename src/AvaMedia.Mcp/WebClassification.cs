using AvaMedia.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AvaMedia.Mcp;

internal static class WebClassification
{
    private sealed record ResolveRequest(string Path, string RuleId, string CategoryId);

    public static void Map(WebApplication app)
    {
        var thumbnails = new MediaThumbnailCache();
        var routes = app.MapGroup("/web/tasks/{id:guid}/classification");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (NotSupportedException) { return Results.Problem("此工作区不支持实时分类。", statusCode: 501); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(error.Message); }
        });
        routes.MapGet("/progress", async (Guid id, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            var live = await workspace.ClassificationLiveAsync(id, ct);
            var task = (await workspace.GetAsync(id, false, 0, 1, ct)).Task;
            var currentFile = Current(live, task);
            var states = live.Snapshot.Files.Select(file => State(file)).ToArray();
            var groups = FolderClassificationInsights.Groups(live.Snapshot.Files, null).Select(group => group with
            { Added = live.Regroup?.Added.GetValueOrDefault(group.RuleId + "/" + group.CategoryId) ?? 0 }).ToArray();
            return Results.Ok(new { task, stages = FolderClassificationInsights.Stages(live.Snapshot, live.Rules, live.Regroup?.Ordinal ?? 0, live.ActiveStage),
                percent = live.Progress, regroup = live.Regroup, groups,
                rules = live.Rules.Select(rule => new { rule.Id, rule.Name, rule.ByOutfit }), elapsedSeconds = live.Elapsed.TotalSeconds, threshold = live.Threshold,
                current = currentFile is null ? null : FolderClassificationInsights.Visual(currentFile, live.Rules, true),
                counts = new { completed = states.Count(state => state == FolderFileState.Done), pending = states.Count(state => state == FolderFileState.Pending),
                    failed = states.Count(state => state == FolderFileState.Failed), total = states.Length } });
        });
        routes.MapGet("/files", async Task<IResult> (Guid id, int? offset, int? limit, string? state, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            FolderFileState? filter = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<FolderFileState>(state, true, out var parsed) || !Enum.IsDefined(parsed)) return Results.BadRequest("文件状态无效。");
                filter = parsed;
            }
            var live = await workspace.ClassificationLiveAsync(id, ct);
            var task = (await workspace.GetAsync(id, false, 0, 1, ct)).Task;
            var current = Current(live, task);
            var selected = live.Snapshot.Files.Where(file => filter is null || (ReferenceEquals(file, current) && file.Error is null
                ? FolderFileState.Processing : State(file)) == filter).ToArray();
            var start = Math.Max(0, offset ?? 0); var take = Math.Clamp(limit ?? 60, 1, 200);
            var items = selected.Skip(start).Take(take).Select(file => FolderClassificationInsights.Visual(file, live.Rules, ReferenceEquals(file, current))).ToArray();
            return Results.Ok(new { items, total = selected.Length, offset = start, nextOffset = (long)start + items.Length < selected.Length ? (int?)(start + items.Length) : null });
        });
        routes.MapGet("/histogram", async (Guid id, double? threshold, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            var live = await workspace.ClassificationLiveAsync(id, ct);
            return Results.Ok(FolderClassificationInsights.Histogram(live.Snapshot.Files, threshold ?? live.Threshold));
        });
        routes.MapGet("/summary", async (Guid id, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            var live = await workspace.ClassificationLiveAsync(id, ct);
            return Results.Ok(FolderClassificationInsights.BeforeAfter(live.Snapshot.Files, live.SourceFolders));
        });
        routes.MapGet("/thumb", async Task<IResult> (Guid id, int index, int? width, double? seconds, IMcpWorkspace workspace, HttpContext context) =>
        {
            var task = (await workspace.GetAsync(id, false, 0, 1, context.RequestAborted)).Task;
            if (task.FeatureId != "folder-classification" || index < 0 || index >= task.Inputs.Length) return Results.BadRequest("任务文件索引无效。");
            var path = await thumbnails.GetAsync(task.Inputs[index], Math.Clamp(width ?? 160, 48, 640), seconds, context.RequestAborted);
            return Jpeg(context, path);
        });
        routes.MapGet("/mosaic", async Task<IResult> (Guid id, string ruleId, string categoryId, int? width, int? height, IMcpWorkspace workspace, HttpContext context) =>
        {
            var live = await workspace.ClassificationLiveAsync(id, context.RequestAborted);
            var group = FolderClassificationInsights.Groups(live.Snapshot.Files, null).FirstOrDefault(group => group.RuleId == ruleId && group.CategoryId == categoryId);
            if (group is null) return Results.NotFound("分类分组不存在。");
            var path = await thumbnails.MosaicAsync(group.CoverPaths, Math.Clamp(width ?? 320, 48, 640), Math.Clamp(height ?? 240, 48, 640), context.RequestAborted);
            return Jpeg(context, path);
        });
        routes.MapPost("/resolve", async (Guid id, ResolveRequest request, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            var file = await workspace.ResolveClassificationAsync(id, request.Path, request.RuleId, request.CategoryId, ct);
            var live = await workspace.ClassificationLiveAsync(id, ct);
            return Results.Ok(FolderClassificationInsights.Visual(file, live.Rules, false));
        });
    }

    private static IResult Jpeg(HttpContext context, string path)
    {
        context.Response.Headers.CacheControl = "private, max-age=3600";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(path, "image/jpeg");
    }
    private static FolderFileState State(FolderClassificationTaskFile file) => file.Error is not null ? FolderFileState.Failed
        : file.Pending || file.Result is null ? FolderFileState.Queued : file.Result.Decisions.Any(decision => decision.NeedsReview) ? FolderFileState.Pending : FolderFileState.Done;
    private static FolderClassificationTaskFile? Current(FolderClassificationLive live, McpTaskView task) => task.State != "Running" ? null
        : live.Snapshot.Files.FirstOrDefault(file => task.Activity?.Detail is { } detail && (detail == file.Path || detail == Path.GetFileName(file.Path)
            || detail.StartsWith(Path.GetFileName(file.Path) + " · ", StringComparison.Ordinal)))
            ?? live.Snapshot.Files.FirstOrDefault(file => file.Pending && file.Result is not null);
}
