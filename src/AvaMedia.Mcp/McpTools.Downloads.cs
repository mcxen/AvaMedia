using System.ComponentModel;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_inspect_download", Title = "解析下载链接", ReadOnly = true, UseStructuredContent = true)]
    [Description("Resolve a URL/playlist through the native desktop VideoDownloadService without downloading media. Returns titles, source URLs and selectable videos. DownloadOptions includes maxHeight, playlist expansion, proxy, subtitles, cookies file or existing browser/CDP context. This may contact the website and use the explicitly selected login state. Does not open a browser or acquire login interactively.")]
    public async Task<DownloadInspection> InspectDownload(string url, DownloadOptions? options = null, CancellationToken ct = default)
    {
        var settings = await workspace.SettingsAsync(null, null, ct).ConfigureAwait(false);
        var spec = options ?? new(); spec.Validate();
        if (spec.CookieFile.Length > 0) _files.Check(spec.CookieFile, true);
        return await new VideoDownloadService(settings).InspectAsync(DownloadLinks.Normalize(url), spec, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "avamedia_download_videos", Title = "下载已解析视频", Destructive = false, UseStructuredContent = true)]
    [Description("Submit selected videos from inspect_download through native DownloadBatch and the durable shared queue. Preserves title/source metadata and one job per selected video. Supports format mp4/mkv/mp3/m4a, all DownloadOptions and optional single-video outputName. Use the same requestId/arguments for response recovery. Existing browser/CDP contexts are reused without opening UI; no interactive login is performed.")]
    public Task<McpSubmission> DownloadVideos(string requestId, DownloadVideo[] videos, string? outputFolder = null,
        string format = "mp4", DownloadOptions? options = null, string outputName = "", bool startImmediately = true, CancellationToken ct = default)
    {
        var spec = options ?? new();
        var folder = _files.Check(outputFolder ?? workspace.Engine.Settings.OutputFolder);
        return Submit(requestId, new { videos, outputFolder, format, options = spec, outputName, startImmediately }, reserved =>
        {
            var jobs = DownloadBatch.CreateJobs(new(videos, folder, format, spec, outputName), reserved).ToArray();
            foreach (var job in jobs) { ValidatePaths(job, _files); MediaEngine.Validate(job); }
            return jobs;
        }, startImmediately, ct);
    }
}
