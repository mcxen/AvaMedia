using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpClipEdit(string Path, string SourceVersion, ConversionOptions[] Segments, bool RemoveSelected = false);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_export_clips", Title = "导出已编辑片段", Destructive = false, UseStructuredContent = true)]
    [Description("Export reviewed per-file segments with the same quick clipping planner as desktop. sourceVersion comes from the web preview. Supports ordered separate segments or joining retained segments per source, and removing selected intervals. Reuse requestId unchanged to recover a lost submission response.")]
    public Task<McpSubmission> ExportClips(string requestId, McpClipEdit[] edits, string preset = SourceOutput.Original,
        ConversionOptions? exportOptions = null, bool joinSegments = false, string? outputFolder = null,
        bool startImmediately = true, CancellationToken ct = default)
    {
        if (edits.Length is < 1 or > 100 || edits.Any(edit => edit.Segments.Length is < 1 or > ClipSplit.MaximumSegments))
            throw new ArgumentException("每批请选择 1–100 个视频，每个视频保留 1–100 个编辑片段。");
        var folder = _files.Check(outputFolder ?? workspace.Engine.Settings.OutputFolder);
        var output = exportOptions?.Clone() ?? new ConversionOptions();
        var reviewed = new List<ClipEditResult>();
        return Submit(requestId, new { edits, preset, exportOptions = output, joinSegments, outputFolder, startImmediately }, reserved =>
        {
            foreach (var edit in edits)
                if (McpMediaFile.Version(edit.Path) != edit.SourceVersion)
                    throw new IOException("源视频已变化，请重新读取预览后再导出。");
            var jobs = joinSegments
                ? QuickClipWorkflow.PrepareJoinedJobs(reviewed, preset, output, folder, false, "", reserved)
                : QuickClipBatch.CreateJobs(QuickClipWorkflow.PrepareExports(reviewed, preset, output), folder, reserved: reserved);
            foreach (var job in jobs)
            {
                ValidatePaths(job, _files);
                foreach (var segment in job.InputOptions ?? [])
                    if (segment.Subtitle.Length > 0) _files.Check(segment.Subtitle, true);
                MediaEngine.Validate(job);
            }
            return jobs.ToArray();
        }, startImmediately, ct, async () =>
        {
            var unique = new HashSet<string>(BatchRename.PathComparer);
            foreach (var edit in edits)
            {
                var path = _files.Check(edit.Path, true);
                if (!unique.Add(path) || !VideoFormats.IsVideo(path)) throw new ArgumentException("请选择不重复的视频文件。");
                if (McpMediaFile.Version(path) != edit.SourceVersion) throw new IOException("源视频已变化，请重新读取预览后再导出。");
                var info = await workspace.Engine.Probe(path, ct).ConfigureAwait(false);
                if (McpMediaFile.Version(path) != edit.SourceVersion) throw new IOException("读取期间源视频发生变化。");
                reviewed.Add(new(path, info, edit.Segments.Select(segment => segment.Clone()).ToArray(), edit.RemoveSelected));
            }
        });
    }
}

internal static class McpMediaFile
{
    public static string Version(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("源文件不存在。", path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }
}
