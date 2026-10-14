using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpFeature(string Id, string Name, string Category, string Format, bool CanCreateTask, McpTaskForm? Form);
public sealed record McpCapabilities(string Protocol, string Transport, McpFeature[] Features,
    string[] TaskActions, FolderClassificationRule[] ClassificationPresets)
{
    public string FeatureOptionsTool => "avamedia_describe_feature";
    public string SettingsTool => "avamedia_get_settings";
    public string[] ModelTools => ["avamedia_list_models", "avamedia_inspect_model", "avamedia_download_model", "avamedia_import_model",
        "avamedia_release_models", "avamedia_delete_model", "avamedia_provider_presets", "avamedia_provider_models", "avamedia_check_provider"];
}
public sealed record McpFilePage(string[] Files, int Total, int Offset, int? NextOffset);
public sealed record McpRenamePreview(RenameItem[] Plan);

[McpServerToolType]
public sealed partial class McpTools(IMcpWorkspace workspace, McpFiles files)
{
    private readonly McpFiles _files = files;
    public const string ProtocolVersion = "2026-07-28";
    public static bool CanCreate(Feature feature) => feature.Operation is not (Operation.Player or Operation.ImageView or Operation.Info)
        && feature.Id is not ("batch-rename" or "folder-organize" or "model-download");
    private bool PrivateEnabled => workspace.Engine.Settings.EnableNsfwContent;

    [McpServerTool(Name = "avamedia_capabilities", Title = "服务能力", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Discover AvaMedia services, local path conventions and classification presets before creating tasks. Task IDs are durable AvaMedia queue IDs, shared with the desktop UI. No media is uploaded by this endpoint.")]
    public McpCapabilities Capabilities() => new(ProtocolVersion, "Streamable HTTP",
        Catalog.All.Where(feature => workspace.Engine.Settings.EnableBetaFeatures || !Catalog.IsBeta(feature))
            .Select(feature => new McpFeature(feature.Id, feature.Label, feature.Category, feature.Format, CanCreate(feature), McpVideoForms.For(feature, workspace.Engine.Settings))).ToArray(),
        ["start", "pause", "resume", "stop", "retry", "rerun", "remove"],
        FolderClassificationRule.Presets.Where(rule => PrivateEnabled || !MediaPrivacy.IsSensitiveRule(rule)).ToArray());

    [McpServerTool(Name = "avamedia_list_files", Title = "目录文件", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List local files. Skips directory symlinks when scanning recursively. Paginate with offset/limit; mediaOnly selects images/videos suitable for AI tagging. Paths refer to the machine running AvaMedia.")]
    public McpFilePage ListFiles(string folder, bool recursive = false, bool mediaOnly = true, int offset = 0, int limit = 100, CancellationToken ct = default)
    {
        Page(offset, limit); var files = _files.Collect([folder], recursive, mediaOnly, ct, allowEmpty: true);
        return new(files.Skip(offset).Take(limit).ToArray(), files.Length, offset, offset + limit < files.Length ? offset + limit : null);
    }

    [McpServerTool(Name = "avamedia_probe", Title = "媒体信息", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read duration, streams, dimensions and codec information for a local media file.")]
    public Task<MediaInfo> Probe(string path, CancellationToken ct = default) => workspace.Engine.Probe(_files.Check(path, true), ct);

    [McpServerTool(Name = "avamedia_editor_preview", Title = "视频编辑预览", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read media information and a square-pixel preview frame from a server video for clipping/crop selection. Coordinates use info.width/info.height, not thumbnail pixels.")]
    public async Task<McpEditorPreview> EditorPreview(string path, double seconds = 0, CancellationToken ct = default)
    {
        path = _files.Check(path, true);
        if (!VideoFormats.IsVideo(path) && !ImageFormats.Supports(path)) throw new ArgumentException("预览请选择视频或图片。");
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentException("预览时间无效。");
        var info = await workspace.Engine.Probe(path, ct).ConfigureAwait(false);
        if (info.Duration > 0) seconds = Math.Min(seconds, Math.Max(0, info.Duration - .05));
        var size = VideoPreviewSizes.Bound(info, VideoPreviewSizes.Preview);
        var frame = await workspace.Engine.Thumbnail(path, seconds, size.Width, size.Height, ct, pad: false, videoStreamIndex: info.VideoStreamIndex).ConfigureAwait(false);
        return new(info, seconds, Convert.ToBase64String(frame));
    }

    [McpServerTool(Name = "avamedia_create_task", Title = "创建任务", Destructive = true, UseStructuredContent = true)]
    [Description("Create shared desktop queue tasks for any capability with canCreateTask=true. Fresh UUID requestId; reuse with identical arguments for response recovery. Returns immediately; poll get_task. Folder paths support recursive expansion; private file operations use dedicated tools. Partial options recursively merge onto native feature defaults; describe_feature provides the full schema/defaults across all categories. inputOptions optionally supplies independent options in explicit file order for Join/Mux/AudioMix only; do not mix with folder inputs. PDF tasks require explicit pdf.pages from document_pages.")]
    public Task<McpSubmission> CreateTask(string requestId, string featureId, string[] paths, string? outputFolder = null,
        ConversionOptions? options = null, bool recursive = false, bool startImmediately = true,
        ConversionOptions[]? inputOptions = null, CancellationToken ct = default,
        RequestContext<CallToolRequestParams>? context = null)
    {
        var feature = Catalog.All.FirstOrDefault(feature => feature.Id == featureId) ?? throw new ArgumentException("未知服务：" + featureId);
        if (!CanCreate(feature)) throw new ArgumentException("该服务请使用专用接口或桌面窗口。");
        if (Catalog.IsBeta(feature) && !workspace.Engine.Settings.EnableBetaFeatures) throw new InvalidOperationException("Beta 功能已关闭。");
        var spec = options?.Clone() ?? Defaults(feature);
        if (context?.Params?.Arguments?.TryGetValue("options", out var rawOptions) == true && rawOptions.ValueKind == JsonValueKind.Object)
            spec = McpOptionMerge.Apply(Defaults(feature), rawOptions);
        if (spec.VlRename is not null) throw new ArgumentException("内部 VL 分析请使用 avamedia_preview_vl_rename。");
        if (spec.Rename is not null || spec.FolderOrganization is not null || spec.DownloadModelId is not null
            || spec.DownloadModelSource is not null || spec.ImportModelPaths is not null)
            throw new ArgumentException("文件操作和模型安装须使用专用接口。");
        if (inputOptions is not null && (feature.Operation is not (Operation.Join or Operation.Mux or Operation.AudioMix)
            || paths.Any(Directory.Exists) || inputOptions.Length != paths.Length))
            throw new ArgumentException("独立输入参数仅用于合并/混流/混音，须按显式文件路径顺序一一提供。");
        var perInput = inputOptions?.Select(o => o.Clone()).ToArray();
        if (context?.Params?.Arguments?.TryGetValue("inputOptions", out var rawInputs) == true && rawInputs.ValueKind == JsonValueKind.Array)
            perInput = rawInputs.EnumerateArray().Select(raw => McpOptionMerge.Apply(Defaults(feature), raw)).ToArray();
        var folder = outputFolder ?? workspace.Engine.Settings.OutputFolder;
        string[] inputs = [];
        return Submit(requestId, new { featureId, paths, outputFolder, options = spec, inputOptions = perInput, recursive, startImmediately }, reserved =>
        {
            var local = new McpFiles();
            Job[] jobs;
            if (feature.Operation == Operation.FolderClassify)
            {
                var job = new Job { FeatureId = feature.Id, Inputs = inputs, Options = spec.Clone() };
                job.Options.FolderClassification ??= new() { Rules = FolderClassificationRule.DefaultRules().Where(rule => PrivateEnabled || !MediaPrivacy.IsSensitiveRule(rule)).ToArray() };
                if (job.Options.FolderClassification.Rules.Length == 0)
                    job.Options.FolderClassification.Rules = FolderClassificationRule.DefaultRules().Where(rule => PrivateEnabled || !MediaPrivacy.IsSensitiveRule(rule)).ToArray();
                job.Options.FolderClassification.OutputFolder = local.Check(folder);
                job.Output = FolderClassificationTaskStore.Folder(job); jobs = [job];
            }
            else if (feature.Operation == Operation.MediaTag)
            {
                spec.MediaTag ??= new(); spec.MediaTag.WriteTextReport = false;
                jobs = inputs.Select(input =>
                {
                    var job = new Job { FeatureId = feature.Id, Inputs = [input], Options = spec.Clone() };
                    job.Output = AiTaskResults.PathFor(job, "tags"); return job;
                }).ToArray();
            }
            else if (feature.Id == "contact-sheet")
            {
                spec.ContactSheet ??= new(); BatchVideoTools.ValidateContactSheet(spec.ContactSheet);
                spec.Format = spec.ContactSheet.Format;
                var used = new HashSet<string>(reserved, BatchRename.PathComparer);
                jobs = inputs.Select(input =>
                {
                    var job = new Job { FeatureId = feature.Id, Inputs = [input], Options = spec.Clone(),
                        Output = MediaEngine.UniqueOutput(local.Check(folder), Path.GetFileNameWithoutExtension(input) + "-grid", spec.Format, used,
                            directory: spec.ContactSheet.SheetsPerVideo > 1) };
                    used.Add(job.Output); return job;
                }).ToArray();
            }
            else if (feature.Operation is Operation.PersonClip or Operation.Download)
            {
                var used = new HashSet<string>(reserved, BatchRename.PathComparer);
                jobs = inputs.Select(input =>
                {
                    var options = spec.Clone();
                    if (feature.Operation == Operation.PersonClip)
                    {
                        options.PersonClip ??= new();
                        if (options.PersonClip.AnalysisOnly) options.Format = "json";
                        else options = QuickClipBatch.ResolveOptions(input, options.PersonClip.ExportPreset, options);
                    }
                    var job = ConversionBatch.CreateJobs(feature, [input], local.Check(folder), options, reserved: used).Single();
                    if (job.Options.PersonClip?.AnalysisOnly == true) job.Output = AiTaskResults.InternalOutputFor(job);
                    used.Add(job.Output); return job;
                }).ToArray();
            }
            else
            {
                jobs = ConversionBatch.CreateJobs(feature, inputs, local.Check(folder), spec, inputOptions: perInput, reserved: reserved).ToArray();
                foreach (var job in jobs)
                    if (job.Options.PersonClip?.AnalysisOnly == true || job.Options.Transcription?.RecognitionOnly == true)
                        job.Output = AiTaskResults.InternalOutputFor(job);
            }
            foreach (var job in jobs) { ValidatePaths(job, local); MediaEngine.Validate(job); }
            return jobs;
        }, startImmediately, ct, async () =>
        {
            inputs = await Task.Run(() => feature.Operation == Operation.Download ? paths.Select(DownloadLinks.Normalize).ToArray()
                : feature.Operation == Operation.IsoCopy ? paths.Select(path => _files.Check(path)).ToArray()
                : _files.Collect(paths, recursive, feature.Operation is Operation.MediaTag or Operation.FolderClassify, ct), ct).ConfigureAwait(false);
        });
    }

    [McpServerTool(Name = "avamedia_tag_media", Title = "AI 媒体标签", Destructive = false, UseStructuredContent = true)]
    [Description("Queue AI label recognition for images/videos, or all media in a folder. Results stay in the task and are available via avamedia_get_task. Models may be downloaded by the existing service. Does not rename/move source files. For AI renaming, use returned labels/captions to choose keywords, then preview/apply a rename plan.")]
    public Task<McpSubmission> TagMedia(string requestId, string[] paths, MediaTagOptions? analysis = null,
        bool recursive = false, bool startImmediately = true, CancellationToken ct = default) =>
        CreateTask(requestId, "media-ai", paths, options: new() { MediaTag = new() { Analysis = analysis ?? new(), WriteTextReport = false } },
            recursive: recursive, startImmediately: startImmediately, ct: ct);

    [McpServerTool(Name = "avamedia_classify_media", Title = "AI 媒体分类", Destructive = false, UseStructuredContent = true)]
    [Description("Queue classification of videos/images/folders using explicit category groups or built-in presets. Analysis returns decisions and uncertain cases; it does not move source files. Fetch presets via capabilities. Semantic categories require the semantic model; allowSemanticDownload opts into downloading it. Empty rules use built-in groups.")]
    public Task<McpSubmission> ClassifyMedia(string requestId, string[] paths, FolderClassificationRule[]? rules = null,
        bool recursive = false, bool allowSemanticDownload = false, bool startImmediately = true,
        MediaTagOptions? analysis = null, double tagThreshold = .5, string? outputFolder = null, CancellationToken ct = default) =>
        CreateTask(requestId, "folder-classification", paths, outputFolder, options: new()
        {
            FolderClassification = new()
            {
                Rules = rules is { Length: > 0 } ? rules : FolderClassificationRule.DefaultRules().Where(rule => PrivateEnabled || !MediaPrivacy.IsSensitiveRule(rule)).ToArray(),
                AllowSemanticDownload = allowSemanticDownload, IncludeNsfw = PrivateEnabled,
                Analysis = analysis ?? new(), TagThreshold = tagThreshold
            }
        }, recursive: recursive, startImmediately: startImmediately, ct: ct);

    [McpServerTool(Name = "avamedia_preview_rename", Title = "预览重命名", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Build a collision-checked rename plan without changing files. Supports AvaMedia templates ({name}, {index}, {keyword}) and replacement/prefix/suffix rules. keywords maps each absolute source path to an AI-selected short label. Return plan includes source length and modification time; apply the exact plan using avamedia_apply_rename.")]
    public McpRenamePreview PreviewRename(string[] paths, RenameRules? rules = null, Dictionary<string, string>? keywords = null, CancellationToken ct = default)
    {
        var files = _files.Collect(paths, false, false, ct);
        return new(BatchRename.PreviewRename(files, rules ?? new(), keywords));
    }

    [McpServerTool(Name = "avamedia_apply_rename", Title = "执行重命名", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Queue an exact plan returned by avamedia_preview_rename. Renames within source directories, rejects changed sources/collisions, and saves the existing transactional rename journal. The task appears in the shared desktop queue. Do not automatically retry a completed rename with a new requestId. Once the rename transaction commits, a concurrent stop request cannot undo those changes.")]
    public Task<McpSubmission> ApplyRename(string requestId, RenameItem[] plan, bool startImmediately = true, CancellationToken ct = default)
    {
        if (plan.Length == 0) throw new ArgumentException("重命名计划不能为空。");
        return Submit(requestId, new { plan, startImmediately }, _ =>
        {
            var job = new Job { FeatureId = "batch-rename", Inputs = plan.Select(item => item.Source).ToArray(),
                Options = new() { Format = "json", Rename = new(plan.ToArray()) } };
            job.Output = AiTaskResults.PathFor(job, "rename-journal");
            ValidatePaths(job, new McpFiles()); MediaEngine.Validate(job); return [job];
        }, startImmediately, ct);
    }

    [McpServerTool(Name = "avamedia_list_tasks", Title = "任务列表", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List desktop and MCP-created tasks from all entry points. State names: Waiting, Running, Completed, Failed, Cancelled, Paused, Stopping. Results are paginated; counts gives unfiltered per-state totals plus All.")]
    public Task<McpTaskPage> ListTasks(int offset = 0, int limit = 50, string? state = null, CancellationToken ct = default)
    { Page(offset, limit); return workspace.ListAsync(offset, limit, state, ct); }

    [McpServerTool(Name = "avamedia_get_task", Title = "任务进度与结果", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a durable queue task by ID. includeResult returns AI labels, classification decisions, person segments, subtitles or video summary. Classification results use offset/limit. Ordinary media tasks return their output path; binary media is not embedded. Query again while Running/Stopping.")]
    public Task<McpTaskResult> GetTask(Guid taskId, bool includeResult = false, int offset = 0, int limit = 50, CancellationToken ct = default)
    { Page(offset, limit); return workspace.GetAsync(taskId, includeResult, offset, limit, ct); }

    [McpServerTool(Name = "avamedia_control_task", Title = "控制任务", Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Control a shared task. start: Waiting; pause: Waiting/Running at a safe checkpoint; resume: Paused; stop: Waiting/Paused/Running; retry: Failed/Cancelled; rerun: Completed ordinary task; remove: inactive queue record/metadata, keeps media outputs. Applying rename/organization again requires a fresh plan; VL analysis retry preserves successful files, but a completed VL analysis requires a new analysis submission. Returns current task state immediately.")]
    public Task<McpTaskView> ControlTask(Guid taskId, string action, CancellationToken ct = default) => workspace.ControlAsync(taskId, action, ct);

    internal async Task<McpSubmission> Submit(string requestId, object arguments, Func<string[], Job[]> create, bool start, CancellationToken ct, Func<Task>? prepare = null)
    {
        if (!Guid.TryParse(requestId, out var id)) throw new ArgumentException("requestId 须为 UUID。");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(arguments)));
        if (prepare is not null)
        {
            var previous = await workspace.FindSubmissionAsync(id.ToString("N"), hash, ct).ConfigureAwait(false);
            if (previous is not null) return previous;
            await prepare().ConfigureAwait(false);
        }
        return await workspace.SubmitAsync(id.ToString("N"), hash, create, start, ct).ConfigureAwait(false);
    }

    private static ConversionOptions Defaults(Feature feature) => McpVideoForms.Defaults(feature);
    public static void Page(int offset, int limit)
    { if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("offset 须非负，limit 须为 1–200。"); }

    public static void ValidatePaths(Job job, McpFiles policy)
    {
        var feature = Catalog.All.FirstOrDefault(feature => feature.Id == job.FeatureId) ?? throw new ArgumentException("未知服务。");
        if (!CanCreate(feature) && feature.Id is not ("batch-rename" or "folder-organize" or "model-download")) throw new ArgumentException("该服务不支持 MCP 任务。");
        foreach (var input in job.Inputs)
            if (feature.Operation == Operation.Download) _ = DownloadLinks.Normalize(input); else policy.Check(input);
        var o = job.Options;
        if (o.Format.Length > 12 || o.Format.Length == 0 || o.Format.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("输出格式须为扩展名。");
        if (o.Subtitle.Length > 0) policy.Check(o.Subtitle, true);
        foreach (var input in job.InputOptions ?? [])
            if (input.Subtitle.Length > 0) policy.Check(input.Subtitle, true);
        if (o.VideoSummary?.SubtitleFile is { Length: > 0 } subtitles) policy.Check(subtitles, true);
        if (o.Transcription?.ReviewOutputFolder is { Length: > 0 } review) policy.Check(review);
        if (o.VideoSummary?.Speech.ReviewOutputFolder is { Length: > 0 } summaryReview) policy.Check(summaryReview);
        if (o.FolderOrganization is { } organization)
        {
            policy.Check(organization.OutputFolder);
            foreach (var item in organization.Plan) { policy.Check(item.File.Media.Path, true); policy.Check(item.Target); }
        }
        else if (job.FeatureId == "model-download") { }
        else if (o.Rename is { } rename)
        {
            foreach (var item in rename.Plan) { policy.Check(item.Source); policy.Check(item.Target); }
            if (!BatchRename.PathComparer.Equals(job.Output, AiTaskResults.PathFor(job, "rename-journal"))) throw new ArgumentException("重命名日志路径无效。");
        }
        else if (feature.Operation == Operation.FolderClassify)
        {
            policy.Check(o.FolderClassification?.OutputFolder ?? throw new ArgumentException("缺少分类参数。"));
            if (!BatchRename.PathComparer.Equals(job.Output, FolderClassificationTaskStore.Folder(job))) throw new ArgumentException("分类结果路径无效。");
        }
        else if (job.HasInternalOutput)
        {
            if (!BatchRename.PathComparer.Equals(job.Output, AiTaskResults.InternalOutputFor(job))) throw new ArgumentException("AI 结果路径无效。");
        }
        else policy.Check(job.Output);
    }
}
