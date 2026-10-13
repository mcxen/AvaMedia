using System.Globalization;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed partial class VideoSlimming
{
    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        ValidateJob(job);
        var options = job.Options.VideoSlimming ?? new();
        var analysis = options.Analysis;
        if (analysis is null || !analysis.Matches(job.Inputs[0], options, Threads()))
            analysis = await AnalyzeAsync(job.Inputs[0], options, (value, detail) =>
            { job.ProgressDetail = detail; progress(value * .35); }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!analysis.Matches(job.Inputs[0], options, Threads()))
            throw new IOException("分析后源视频发生变化，请重新分析。");
        job.Duration = analysis.Duration;
        job.Options.VideoSlimming = options with { Analysis = analysis };
        var estimated = analysis.EstimatedBytes is { } estimate ? $"预计 {estimate} B" : "未找到合格的瘦身方案";
        job.AppendLog($"视频瘦身：{analysis.SourceBytes} B；{estimated}；{analysis.Diagnosis}");
        if (analysis.Quality is { } quality && analysis.MeanSsim is { } mean && analysis.WorstSsim is { } worst)
            job.AppendLog($"{analysis.Encoder} medium CRF {quality}；{analysis.Samples} 段 / {analysis.SampleSeconds:0.##} 秒；SSIM 平均 {mean:0.######}，最低 {worst:0.######}；XPSNR {analysis.WorstXpsnr?.ToString("0.##", CultureInfo.InvariantCulture) ?? "不可用"}。");
        if (!analysis.Worthwhile)
        {
            string reason;
            if (analysis.Disposition == VideoSlimmingDisposition.QualityLimit)
                reason = "无需瘦身：未找到满足当前画质要求的缩小方案，已保留原视频。";
            else if (analysis.ActualBytes is { } measuredBytes)
            {
                job.AppendLog($"已复用相同源视频与编码参数的实际结果：{measuredBytes} B；原采样预估 {analysis.EstimatedBytes} B。");
                reason = analysis.ActualSaving is { } measuredSaving && measuredSaving > 0
                    ? $"无需瘦身：实际仅节省 {measuredSaving:0.##}%，不足 5%；已保留原视频。"
                    : "无需瘦身：实际编码结果未缩小，已保留原视频。";
            }
            else
                reason = analysis.EstimatedSaving is { } estimatedSaving && estimatedSaving > 0
                    ? $"无需瘦身：在当前画质要求下，预计仅可节省 {estimatedSaving:0.##}%，不足 5%；已保留原视频。"
                    : "无需瘦身：在当前画质要求下，预计编码后体积不会缩小，已保留原视频。";
            CompleteUnchanged(reason);
            return;
        }
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!,
            ".AvaMedia-slim-" + Guid.NewGuid().ToString("N") + "." + options.Format);
        try
        {
            job.ProgressDetail = "正在瘦身"; progress(35);
            await Checked(EncodingArguments(job.Inputs[0], temporary, analysis.Encoder, analysis.PixelFormat,
                analysis.Quality!.Value, analysis.Threads, options.Format, includeSubtitles: true,
                new(analysis.OutputWidth, analysis.OutputHeight, analysis.SizeOptimized, analysis.OutputPixelAspectRatio)), ct, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    double.TryParse(line[12..], NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                    progress(35 + Math.Clamp(time / 1000000 / analysis.Duration, 0, 1) * 63);
            }).ConfigureAwait(false);
            var output = await engine.Probe(temporary, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(output.RawJson);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            if (!output.HasVideo || !double.IsFinite(output.Duration) || output.Duration <= 0 ||
                Math.Abs(output.Duration - analysis.Duration) > Math.Max(.25, 3 / Math.Max(1, analysis.FrameRate)) ||
                output.Width != analysis.OutputWidth || output.Height != analysis.OutputHeight ||
                Math.Abs(MediaStreams.PixelAspectRatio(output) / analysis.OutputPixelAspectRatio - 1) > .001 ||
                streams.Count(stream => Text(stream, "codec_type") == "audio") != analysis.AudioTracks ||
                streams.Count(stream => Text(stream, "codec_type") == "subtitle") != analysis.SubtitleTracks)
                throw new InvalidDataException("瘦身输出的时长、尺寸或轨道不完整，已保留原视频。");
            var bytes = new FileInfo(temporary).Length;
            if (bytes <= 0) throw new InvalidDataException("瘦身输出为空，已保留原视频。");
            if (!analysis.Matches(job.Inputs[0], options, Threads()))
                throw new IOException("瘦身期间源视频发生变化，请重新分析。");
            ct.ThrowIfCancellationRequested();
            var actualSaving = (1 - bytes / (double)analysis.SourceBytes) * 100;
            if (bytes >= analysis.SourceBytes * .95)
            {
                File.Delete(temporary);
                var reason = actualSaving > 0
                    ? $"无需瘦身：实际仅节省 {actualSaving:0.##}%，不足 5%；已跳过输出并保留原视频。"
                    : "无需瘦身：实际编码结果未缩小，已跳过输出并保留原视频。";
                job.Options.VideoSlimming = options with
                { Analysis = analysis with { ActualBytes = bytes, Disposition = VideoSlimmingDisposition.InsufficientSavings, Diagnosis = reason } };
                job.AppendLog($"实际编码结果 {bytes} B，节省 {actualSaving:0.##}%；收益不足 5%，已删除临时输出。");
                CompleteUnchanged(reason);
                return;
            }
            File.Move(temporary, job.Output);
            job.Options.VideoSlimming = options with { Analysis = analysis with { ActualBytes = bytes } };
            job.ProgressDetail = $"节省 {actualSaving:0.##}%";
            job.CompletionMessage = $"视频瘦身完成，节省 {actualSaving:0.##}%；原视频已保留。";
            var dimensions = analysis.SizeOptimized ? $"尺寸 {analysis.Width} × {analysis.Height} → {analysis.OutputWidth} × {analysis.OutputHeight}；" : "保持分辨率；";
            job.AppendLog($"实际输出 {bytes} B，节省 {actualSaving:0.##}%；{dimensions}保持显示比例、帧率和全部音轨 / 字幕轨。画质评分来自采样，不等于无损。");
            progress(100);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        void CompleteUnchanged(string message)
        {
            ct.ThrowIfCancellationRequested();
            job.AppendLog(message);
            job.CompletionKind = JobCompletionKind.Unchanged;
            job.CompletionMessage = message;
            job.ProgressDetail = "无需瘦身 · 保留原视频";
            progress(100);
        }
    }
}
