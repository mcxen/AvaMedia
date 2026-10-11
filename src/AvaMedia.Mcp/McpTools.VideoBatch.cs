using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpVideoBatchSpec(string FeatureId, string[] Paths, ConversionOptions Options,
    string? ReferencePath = null, string? ReferenceVersion = null, BatchCropMode CropMode = BatchCropMode.Pixels,
    Dictionary<string, int>? Rotations = null);
public sealed record McpVideoBatchEntry(string Path, string SourceVersion, MediaInfo? Info, CropArea? Crop,
    int? Rotation, string Format, string Error);
public sealed record McpVideoBatchPreview(McpVideoBatchSpec Spec, McpVideoBatchEntry[] Entries);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_preview_video_batch", Title = "批量裁剪与旋转计划", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Validate every selected video against the desktop batch crop/rotation planner. Returns per-file errors and source versions. Crop supports pixels or relative coordinates using a reviewed reference frame; rotation supports per-file directions and original/fast rotation formats.")]
    public async Task<McpVideoBatchPreview> PreviewVideoBatch(McpVideoBatchSpec spec, CancellationToken ct = default)
    {
        if (spec.FeatureId is not ("crop" or "rotate") || spec.Paths.Length is < 1 or > 200)
            throw new ArgumentException("批量裁剪或旋转须选择 1–200 个视频。");
        var paths = spec.Paths.Select(path => _files.Check(path, true)).ToArray();
        if (paths.Distinct(BatchRename.PathComparer).Count() != paths.Length) throw new ArgumentException("文件列表包含重复路径。");
        var area = new CropArea(spec.Options.CropX, spec.Options.CropY, spec.Options.CropWidth, spec.Options.CropHeight);
        MediaInfo? reference = null;
        if (spec.FeatureId == "crop")
        {
            var path = _files.Check(spec.ReferencePath ?? "", true);
            if (McpMediaFile.Version(path) != spec.ReferenceVersion) throw new IOException("裁剪参考视频已变化，请重新读取预览。");
            reference = await workspace.Engine.Probe(path, ct, spec.Options.VideoStreamIndex, spec.Options.AudioStreamIndex).ConfigureAwait(false);
            if (McpMediaFile.Version(path) != spec.ReferenceVersion) throw new IOException("读取期间参考视频发生变化。");
        }
        var entries = new List<McpVideoBatchEntry>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!VideoFormats.IsVideo(path)) throw new ArgumentException("请选择视频文件。");
                var version = McpMediaFile.Version(path);
                var info = await workspace.Engine.Probe(path, ct, spec.Options.VideoStreamIndex, spec.Options.AudioStreamIndex).ConfigureAwait(false);
                if (version != McpMediaFile.Version(path)) throw new IOException("读取期间源视频发生变化。");
                var rotation = spec.Rotations?.GetValueOrDefault(path, spec.Options.Rotation) ?? spec.Options.Rotation;
                var options = spec.FeatureId == "crop"
                    ? BatchCrop.ResolveOptions(area, reference!, info, spec.CropMode, spec.Options, path)
                    : BatchRotate.ResolveOptions(info, rotation, spec.Options.Format, path);
                MediaEngine.ValidateEdits(new() { FeatureId = spec.FeatureId, Inputs = [path], Options = options,
                    Output = Path.Combine(Path.GetTempPath(), "AvaMedia-batch-preview." + options.Format) }, [info]);
                entries.Add(new(path, version, info with { RawJson = "" }, spec.FeatureId == "crop"
                    ? new(options.CropX, options.CropY, options.CropWidth, options.CropHeight) : null,
                    spec.FeatureId == "rotate" ? rotation : null, options.Format, ""));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { entries.Add(new(path, "", null, null, null, "", error.Message)); }
        }
        return new(spec, entries.ToArray());
    }

    [McpServerTool(Name = "avamedia_export_video_batch", Title = "导出批量视频计划", Destructive = false, UseStructuredContent = true)]
    [Description("Submit a reviewed preview_video_batch plan to the shared queue. Rechecks all source versions and recomputes options using desktop planners. Invalid entries reject the whole batch; zero rotation entries are skipped. Reuse requestId unchanged for recovery.")]
    public Task<McpSubmission> ExportVideoBatch(string requestId, McpVideoBatchPreview plan, string? outputFolder = null,
        bool startImmediately = true, CancellationToken ct = default)
    {
        var folder = _files.Check(outputFolder ?? workspace.Engine.Settings.OutputFolder);
        McpVideoBatchPreview? verified = null;
        MediaInfo? reference = null;
        var media = new Dictionary<string, MediaInfo>(BatchRename.PathComparer);
        return Submit(requestId, new { plan, outputFolder, startImmediately }, reserved =>
        {
            foreach (var entry in verified!.Entries)
                if (entry.SourceVersion != McpMediaFile.Version(entry.Path)) throw new IOException("源视频已变化，请重新检查批量计划。");
            IReadOnlyList<Job> jobs;
            if (plan.Spec.FeatureId == "crop")
            {
                var o = plan.Spec.Options;
                if (McpMediaFile.Version(plan.Spec.ReferencePath!) != plan.Spec.ReferenceVersion) throw new IOException("裁剪参考视频已变化。");
                jobs = BatchCrop.CreateJobs(new(verified.Entries.Select(entry => new BatchCropInput(entry.Path, media[entry.Path])).ToArray(),
                    new(o.CropX, o.CropY, o.CropWidth, o.CropHeight), reference!, plan.Spec.CropMode, o, folder), reserved);
            }
            else jobs = BatchRotate.CreateJobs(new(verified.Entries.Select(entry => new BatchRotateInput(entry.Path, media[entry.Path], entry.Rotation)).ToArray(),
                plan.Spec.Options.Rotation, plan.Spec.Options.Format, folder), reserved);
            if (jobs.Count == 0) throw new ArgumentException("所选视频均无需旋转。");
            foreach (var job in jobs) { ValidatePaths(job, _files); MediaEngine.Validate(job); }
            return jobs.ToArray();
        }, startImmediately, ct, async () =>
        {
            verified = await PreviewVideoBatch(plan.Spec, ct).ConfigureAwait(false);
            if (plan.Entries.Length != verified.Entries.Length) throw new ArgumentException("计划文件数不一致，请重新检查。");
            for (var i = 0; i < verified.Entries.Length; i++)
            {
                var entry = verified.Entries[i]; var previous = plan.Entries[i];
                if (entry.Error.Length > 0) throw new ArgumentException(Path.GetFileName(entry.Path) + "：" + entry.Error);
                if (entry.Path != previous.Path || entry.SourceVersion != previous.SourceVersion) throw new IOException("源视频已变化，请重新检查批量计划。");
                media[entry.Path] = await workspace.Engine.Probe(entry.Path, ct, plan.Spec.Options.VideoStreamIndex,
                    plan.Spec.Options.AudioStreamIndex).ConfigureAwait(false);
            }
            if (plan.Spec.FeatureId == "crop") reference = await workspace.Engine.Probe(plan.Spec.ReferencePath!, ct,
                plan.Spec.Options.VideoStreamIndex, plan.Spec.Options.AudioStreamIndex).ConfigureAwait(false);
        });
    }
}
