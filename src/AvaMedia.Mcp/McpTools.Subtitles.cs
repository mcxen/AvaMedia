using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvaMedia.Core;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpSubtitleCue(double Start, double End, string Text);
public sealed record McpSubtitleStyle(int FontSize = 48, string Color = "#FFFFFF", int Alignment = 2, int Margin = 36,
    string Font = "", double? PositionX = null, double? PositionY = null);
public sealed record McpSubtitleReview(Guid TaskId, string Path, string SourceVersion, string Revision, int VideoStreamIndex, double Duration, double SourceStart,
    double Speed, bool HasVideo, bool Partial, McpSubtitleCue[] Cues, McpSubtitleStyle Style);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_review_subtitles", Title = "读取字幕校对草稿", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a finished or stopped subtitle task for visual review. Includes output-relative cue times, source version, duration and style. Partial results remain explicitly marked. Does not run recognition again.")]
    public async Task<McpSubtitleReview> ReviewSubtitles(Guid taskId, CancellationToken ct = default)
    {
        var (task, result, options) = await SubtitleSource(taskId, ct).ConfigureAwait(false);
        var path = _files.Check(task.Inputs.Single(), true);
        var version = SubtitleVersion(result);
        CheckSubtitleSource(path, version);
        var info = await workspace.Engine.Probe(path, ct, options.VideoStreamIndex, options.AudioStreamIndex).ConfigureAwait(false);
        CheckSubtitleSource(path, version);
        var duration = (Math.Min(options.End > 0 ? options.End : info.Duration, info.Duration) - options.Start) / options.Speed;
        return new(taskId, path, version, SubtitleRevision(result, options), options.VideoStreamIndex, duration, options.Start, options.Speed, info.HasVideo,
            result.IsPartial || task.State != "Completed", result.Cues.Select(cue => new McpSubtitleCue(cue.Start.TotalSeconds, cue.End.TotalSeconds, cue.Text)).ToArray(),
            new(options.SubtitleFontSize, options.SubtitleColor, options.SubtitleAlignment, options.SubtitleMargin,
                options.SubtitleFont, options.SubtitlePositionX, options.SubtitlePositionY));
    }

    [McpServerTool(Name = "avamedia_export_subtitles", Title = "导出校对字幕", Destructive = false, UseStructuredContent = true)]
    [Description("Export reviewed subtitle cues through the same conversion queue as desktop without repeating recognition. Formats: srt, ass, mp4, mkv. Cue times are relative to the recognized output interval. Source changes reject export. Partial analysis requires allowPartial=true; reuse requestId for recovery.")]
    public Task<McpSubmission> ExportSubtitles(string requestId, Guid taskId, string sourceVersion, string revision, McpSubtitleCue[] cues,
        McpSubtitleStyle style, string format = "srt", string? outputFolder = null, bool allowPartial = false,
        bool startImmediately = true, CancellationToken ct = default)
    {
        if (format is not ("srt" or "ass" or SourceOutput.Original) && !VideoFormats.OriginalOutputExtensions.Contains(format)) throw new ArgumentException("请选择原格式视频、支持的视频容器、SRT 或 ASS。");
        if (cues.Length is < 1 or > 50000) throw new ArgumentException("请选择 1–50000 条字幕。");
        var folder = _files.Check(outputFolder ?? workspace.Engine.Settings.OutputFolder);
        string path = ""; ConversionOptions? reviewed = null;
        return Submit(requestId, new { taskId, sourceVersion, revision, cues, style, format, outputFolder, allowPartial, startImmediately }, reserved =>
        {
            CheckSubtitleSource(path, sourceVersion);
            var jobs = ConversionBatch.CreateJobs(Catalog.Find("auto-subtitle"), [path], folder, reviewed!, reserved: reserved);
            foreach (var job in jobs) { ValidatePaths(job, _files); MediaEngine.Validate(job); }
            return jobs.ToArray();
        }, startImmediately, ct, async () =>
        {
            var (task, result, options) = await SubtitleSource(taskId, ct).ConfigureAwait(false);
            if ((result.IsPartial || task.State != "Completed") && !allowPartial) throw new ArgumentException("这是部分识别结果，请先确认仅导出已校对内容。");
            if (revision != SubtitleRevision(result, options) || sourceVersion != SubtitleVersion(result)) throw new IOException("识别草稿已变化，请重新打开校对。");
            path = _files.Check(task.Inputs.Single(), true); CheckSubtitleSource(path, sourceVersion);
            var info = await workspace.Engine.Probe(path, ct, options.VideoStreamIndex, options.AudioStreamIndex).ConfigureAwait(false);
            var duration = (Math.Min(options.End > 0 ? options.End : info.Duration, info.Duration) - options.Start) / options.Speed;
            if (cues.Any(cue => !double.IsFinite(cue.Start) || !double.IsFinite(cue.End) || cue.Start < 0 || cue.End <= cue.Start
                || cue.End > duration + .1 || string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Length > 20000))
                throw new ArgumentException("字幕须有文字，时间区间须位于媒体范围内。");
            reviewed = options.Clone(); reviewed.Format = format; ApplySubtitleStyle(reviewed, style);
            reviewed.Transcription ??= new(); reviewed.Transcription.RecognitionOnly = false;
            reviewed.Transcription.ReviewedCues = cues.OrderBy(cue => cue.Start).Select(cue => new SubtitleCue(TimeSpan.FromSeconds(cue.Start), TimeSpan.FromSeconds(cue.End), cue.Text.Trim())).ToArray();
            reviewed.Transcription.ReviewedSourceLength = result.SourceLength;
            reviewed.Transcription.ReviewedSourceWriteUtc = result.SourceWriteUtc;
            reviewed.Transcription.Validate();
        });
    }

    private async Task<(McpTaskView Task, SubtitleTaskResult Result, ConversionOptions Options)> SubtitleSource(Guid id, CancellationToken ct)
    {
        var task = await workspace.GetAsync(id, true, 0, 200, ct).ConfigureAwait(false);
        if (task.Task.FeatureId != "auto-subtitle" || task.Task.State is not ("Completed" or "Failed" or "Cancelled")
            || task.Task.Inputs.Length != 1 || task.Result is not SubtitleTaskResult result)
            throw new ArgumentException("请等待字幕识别结束，再打开校对结果。");
        return (task.Task, result, await workspace.OptionsAsync(id, ct).ConfigureAwait(false));
    }
    internal static void ApplySubtitleStyle(ConversionOptions options, McpSubtitleStyle style)
    {
        options.SubtitleFontSize = style.FontSize; options.SubtitleColor = style.Color;
        options.SubtitleAlignment = style.Alignment; options.SubtitleMargin = style.Margin; options.SubtitleFont = style.Font;
        options.SubtitlePositionX = style.PositionX; options.SubtitlePositionY = style.PositionY;
        var check = options.Clone(); check.SubtitleMode = SubtitleMode.None; check.Subtitle = ""; SubtitleOptions.Validate(check);
    }
    private static string SubtitleRevision(SubtitleTaskResult result, ConversionOptions options) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { result, options }))));
    private static string SubtitleVersion(SubtitleTaskResult result) => $"{result.SourceLength}:{result.SourceWriteUtc.Ticks}";
    internal static void CheckSubtitleSource(string path, string version)
    { if (McpMediaFile.Version(path) != version) throw new IOException("源文件已变化，请重新识别字幕。"); }
}
