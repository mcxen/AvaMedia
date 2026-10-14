using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AvaMedia.Core;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpTaskOptions(Guid TaskId, string FeatureId, ConversionOptions Options, ConversionOptions[]? InputOptions, string Output)
{
    public string UserOutput { get; init; } = "";
    public bool InternalOutput { get; init; }
}
public sealed record McpFeatureOptions(McpFeature Feature, ConversionOptions Defaults, JsonElement Schema, bool GroupedInputs, string[] Tools, string Notes);

/// <summary>Merge explicit wire fields onto native defaults, including nested partial objects.</summary>
public static class McpOptionMerge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static ConversionOptions Apply(ConversionOptions defaults, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("options 须为对象。");
        var target = JsonSerializer.SerializeToNode(defaults, Json)!.AsObject();
        Merge(target, patch);
        var result = target.Deserialize<ConversionOptions>(Json) ?? throw new ArgumentException("任务参数无效。");
        return result;
    }
    private static void Merge(JsonObject target, JsonElement patch)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in patch.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("重复参数：" + property.Name);
            var key = target.Select(pair => pair.Key).FirstOrDefault(key => key.Equals(property.Name, StringComparison.OrdinalIgnoreCase)) ?? property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object && target[key] is JsonObject child) Merge(child, property.Value);
            else target[key] = JsonNode.Parse(property.Value.GetRawText());
        }
    }
}

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_describe_feature", Title = "完整功能参数", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Describe any Catalog feature, including audio/image/PDF/archive/AI tools. Returns native ConversionOptions JSON Schema, feature defaults, grouped-input behavior and dedicated tool routes. Schema is generated from the actual Core types; enum names/values follow tools/list. capabilities.form is only the compact video form, not the complete option contract. PDF operations require explicit pdf.pages in input/page order; use document_pages to discover pages. Private tools stay gated.")]
    public McpFeatureOptions DescribeFeature(string featureId)
    {
        var settings = workspace.Engine.Settings;
        var feature = Catalog.All.FirstOrDefault(feature => feature.Id == featureId) ?? throw new ArgumentException("未知功能。");
        if (Catalog.IsBeta(feature) && !settings.EnableBetaFeatures) throw new InvalidOperationException("Beta 功能已关闭。");
        string[] tools = feature.Id switch
        {
            "batch-rename" => ["avamedia_preview_rename", "avamedia_apply_rename"],
            "folder-organize" => ["avamedia_preview_organization", "avamedia_apply_organization"],
            "model-download" => ["avamedia_list_models", "avamedia_download_model", "avamedia_import_model"],
            "player" or "image-viewer" or "info" => ["avamedia_probe", "avamedia_editor_preview"],
            "folder-classification" => ["avamedia_classify_media", "avamedia_create_task", "avamedia_classification_live", "avamedia_resolve_classification"],
            "download" => ["avamedia_inspect_download", "avamedia_download_videos", "avamedia_create_task"],
            _ => ["avamedia_create_task"]
        };
        var notes = PdfTools.Supports(feature.Operation) ? "pdf.pages must explicitly select source pages; no automatic page selection."
            : !CanCreate(feature) ? "Use the dedicated tools; interactive playback/viewer windows are desktop-only."
            : "Omitted fields retain feature defaults. paths and outputFolder are absolute server paths. Nested options use Core validation.";
        return new(new(feature.Id, feature.Label, feature.Category, feature.Format, CanCreate(feature), McpVideoForms.For(feature, settings)),
            Defaults(feature), AIJsonUtilities.CreateJsonSchema(typeof(ConversionOptions)), ConversionBatch.IsGrouped(feature), tools, notes);
    }

    [McpServerTool(Name = "avamedia_get_task_options", Title = "读取任务全部参数", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read the exact persisted native options, per-input options and output path for a shared task. Credentials are never included. Private VL task options require activation. Use update_task_options only before a task has started; running/partial/transactional plans require their dedicated review flow.")]
    public Task<McpTaskOptions> GetTaskOptions(Guid taskId, CancellationToken ct = default) => workspace.TaskOptionsAsync(taskId, ct);

    [McpServerTool(Name = "avamedia_update_task_options", Title = "修改未启动任务参数", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Patch native ConversionOptions of an unscheduled Waiting task that has never started. Omitted nested fields retain current values. Validates through the same engine, preserves task ID, saves durably and recomputes output name when format/folder changes. Rejects per-input drafts and rename/organization/model-install plans; recreate those with their dedicated tools. Output folder cannot redirect internal AI analysis results. Does not start the task.")]
    public Task<McpTaskOptions> UpdateTaskOptions(Guid taskId, JsonElement patch, string? outputFolder = null, CancellationToken ct = default) =>
        workspace.UpdateTaskOptionsAsync(taskId, patch, outputFolder, ct);

    [McpServerTool(Name = "avamedia_move_task", Title = "调整任务顺序", Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Move a Waiting/Paused task in the shared desktop queue. position: top, up, down, bottom. Uses the desktop pending scheduler; does not interrupt running jobs or change concurrency.")]
    public Task<McpTaskView> MoveTask(Guid taskId, string position, CancellationToken ct = default) => workspace.MoveTaskAsync(taskId, position, ct);

    [McpServerTool(Name = "avamedia_document_pages", Title = "读取文档页面", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read PDF pages or the single image page with dimensions in points for native PDF task selection. No rasterization/export. Returns page numbers starting at 1; use them with pdf.pages inputIndex/pageNumber/rotation/breakAfter. Paths are on the server.")]
    public async Task<PdfPageInfo[]> DocumentPages(string path, CancellationToken ct = default)
    {
        path = _files.Check(path, true);
        return await Task.Run(() => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? PdfRasterizer.ReadPages(path, ct).ToArray()
            : ImageFormats.Supports(path) ? [PdfTools.ReadImagePage(path)] : throw new ArgumentException("请选择 PDF 或图片。"), ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "avamedia_ai_progress", Title = "AI 分阶段进度", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read actual AI stages, queue summary, elapsed times, recent evidence and model backend through the shared desktop progress projection. Does not start inference. Privacy filtering matches desktop.")]
    public Task<AiTaskProgressDto> AiProgress(Guid taskId, CancellationToken ct = default) => workspace.AiProgressAsync(taskId, ct);

    [McpServerTool(Name = "avamedia_classification_live", Title = "分类实时结果", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read classification rules, current files, review categories and grouping state, including partial/running results. Privacy filtering matches desktop. This full live projection is intended for modest tasks; get_task provides paginated file results for large folders.")]
    public Task<FolderClassificationLive> ClassificationLive(Guid taskId, CancellationToken ct = default) => workspace.ClassificationLiveAsync(taskId, ct);

    [McpServerTool(Name = "avamedia_resolve_classification", Title = "确认分类结果", Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Assign a classified file to a visible rule/category by IDs returned by classification_live. Persists the desktop review override; does not move files or rerun inference. Use a fresh organization preview after review.")]
    public Task<FolderClassificationTaskFile> ResolveClassification(Guid taskId, string path, string ruleId, string categoryId, CancellationToken ct = default) =>
        workspace.ResolveClassificationAsync(taskId, _files.Check(path, true), ruleId, categoryId, ct);

    [McpServerTool(Name = "avamedia_summary_evidence", Title = "总结证据", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read one saved video-summary evidence entry by its evidenceId from the report. Uses the native evidence store and does not rerun models.")]
    public Task<SummaryEvidence?> SummaryEvidence(Guid taskId, string evidenceId, CancellationToken ct = default) => workspace.SummaryEvidenceAsync(taskId, evidenceId, ct);
}
