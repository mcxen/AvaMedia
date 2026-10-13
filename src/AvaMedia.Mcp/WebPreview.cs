using System.Text.Json;
using AvaMedia.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AvaMedia.Mcp;

internal sealed record WebContactSheetPreview(string Path, ContactSheetOptions Options);

internal static class WebPreview
{
    public static void Map(WebApplication app)
    {
        // Bound both decoders and waiting/slow responses. This budget is separate from queued jobs.
        var admission = new SemaphoreSlim(3, 3);
        var decoder = new SemaphoreSlim(1, 1);
        FrameStripCache? stripCache = null;
        app.MapGet("/web/preview/frame", (string path, double seconds, int? width, int? videoStreamIndex, int? audioStreamIndex,
            IMcpWorkspace workspace, HttpContext context) => Run(context, async ct =>
        {
            path = new McpFiles().Check(path, true);
            if (!VideoFormats.IsVideo(path) && !ImageFormats.Supports(path))
                throw new ArgumentException("预览请选择视频或图片。");
            if (!double.IsFinite(seconds) || seconds < 0)
                throw new ArgumentException("预览时间无效。");
            // Cache-friendly display budgets, including the small covers used by file rows.
            var frameWidth = VideoPreviewSizes.RemoteWidth(width);
            var sourceVersion = McpMediaFile.Version(path);
            var info = await workspace.Engine.Probe(path, ct, videoStreamIndex ?? 0, audioStreamIndex ?? 0);
            if (info.Duration > 0) seconds = Math.Min(seconds, Math.Max(0, info.Duration - .05));
            var size = VideoPreviewSizes.Bound(info, new(frameWidth, frameWidth * 3 / 4));
            var frame = await workspace.Engine.Thumbnail(path, seconds, size.Width, size.Height, ct, pad: false, videoStreamIndex: info.VideoStreamIndex);
            if (sourceVersion != McpMediaFile.Version(path)) throw new IOException("读取期间源文件发生变化，请重试。");
            var metadata = JsonSerializer.Serialize(new { seconds, info = new
            {
                width = info.Width, height = info.Height, duration = info.Duration,
                videoCodec = info.VideoCodec, audioCodec = info.AudioCodec, sourceVersion
            } });
            return (frame, metadata);
        }));
        app.MapPost("/web/preview/contact-sheet", (WebContactSheetPreview request, IMcpWorkspace workspace, HttpContext context) => Run(context, async ct =>
        {
            var path = new McpFiles().Check(request.Path, true);
            var version = McpMediaFile.Version(path);
            var info = await workspace.Engine.Probe(path, ct);
            var frame = await BatchVideoTools.PreviewContactSheetAsync(workspace.Engine, path, request.Options, ct);
            if (version != McpMediaFile.Version(path)) throw new IOException("读取期间源文件发生变化。");
            var metadata = JsonSerializer.Serialize(new { seconds = request.Options.StartSeconds, info = new
            { width = info.Width, height = info.Height, duration = info.Duration, videoCodec = info.VideoCodec, audioCodec = info.AudioCodec, sourceVersion = version } });
            return (frame, metadata);
        }));
        app.MapGet("/web/preview/strip", async (string path, int? count, int? videoStreamIndex, IMcpWorkspace workspace, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!admission.Wait(0))
            {
                context.Response.Headers.RetryAfter = "1";
                return Results.Problem("预览服务繁忙，请稍后重试。", statusCode: 429);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                path = new McpFiles().Check(path, true);
                if (!VideoFormats.IsVideo(path)) throw new ArgumentException("胶片条请选择视频。");
                var version = McpMediaFile.Version(path);
                var info = await workspace.Engine.Probe(path, timeout.Token, videoStreamIndex ?? 0);
                await decoder.WaitAsync(timeout.Token);
                async Task<IReadOnlyList<FrameStripFrame>> ReadStrip()
                {
                    try
                    {
                        stripCache ??= new FrameStripCache(workspace.Engine);
                        return await stripCache.GetAsync(path, info.Duration, count ?? 10, videoStreamIndex: info.VideoStreamIndex);
                    }
                    finally { decoder.Release(); }
                }
                // Keep the decoder lease until shared work finishes, even if this response is cancelled.
                var strip = await ReadStrip().WaitAsync(timeout.Token);
                var frames = strip.Select(frame => new { seconds = frame.Seconds, image = Convert.ToBase64String(frame.Image) });
                if (version != McpMediaFile.Version(path)) throw new IOException("读取期间源文件发生变化，请重试。");
                return Results.Json(new { duration = info.Duration, width = info.Width, height = info.Height, sourceVersion = version, frames });
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { return Results.Problem("胶片条生成超时。", statusCode: 408); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { return Results.Problem(error.Message, statusCode: 400); }
            finally { admission.Release(); }
        });
        // Segment boundary thumbnails (起点/终点) and frame-accurate nudging for clip editors.
        app.MapGet("/web/preview/boundaries", async (string path, double start, double end, int? width, int? videoStreamIndex, IMcpWorkspace workspace, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!admission.Wait(0)) { context.Response.Headers.RetryAfter = "1"; return Results.Problem("预览服务繁忙，请稍后重试。", statusCode: 429); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                path = new McpFiles().Check(path, true);
                if (!VideoFormats.IsVideo(path)) throw new ArgumentException("请选择视频。");
                var version = McpMediaFile.Version(path);
                var frameWidth = width > 240 ? 320 : 240;
                await decoder.WaitAsync(timeout.Token);
                SegmentBoundaryFrame[] frames;
                try { frames = await SegmentBoundaryFrames.ReadAsync(workspace.Engine, path, start, end, frameWidth, frameWidth * 9 / 16, videoStreamIndex ?? 0, timeout.Token); }
                finally { decoder.Release(); }
                if (version != McpMediaFile.Version(path)) throw new IOException("读取期间源文件发生变化，请重试。");
                return Results.Json(new { sourceVersion = version, frames = frames.Select(frame => new { edge = frame.Edge, seconds = frame.Seconds, image = Convert.ToBase64String(frame.Image) }) });
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Results.Problem("预览超时。", statusCode: 408); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException) { return Results.Problem(error.Message, statusCode: 400); }
            finally { admission.Release(); }
        });
        app.MapGet("/web/preview/nudge", async (string path, double seconds, int frames, int? videoStreamIndex, IMcpWorkspace workspace, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                path = new McpFiles().Check(path, true);
                var result = await SegmentBoundaryFrames.NudgeAsync(workspace.Engine, path, seconds, frames, videoStreamIndex ?? 0, timeout.Token);
                return Results.Json(new { seconds = result });
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Results.Problem("定位超时。", statusCode: 408); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException) { return Results.Problem(error.Message, statusCode: 400); }
        });
        async Task Run(HttpContext context, Func<CancellationToken, Task<(byte[] Frame, string Metadata)>> render)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!admission.Wait(0))
            {
                context.Response.Headers.RetryAfter = "1";
                await Results.Problem("预览服务繁忙，请稍后重试。", statusCode: 429).ExecuteAsync(context);
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var ct = timeout.Token;
            try
            {
                await decoder.WaitAsync(ct);
                byte[] frame; string metadata;
                try { (frame, metadata) = await render(ct); }
                finally { decoder.Release(); }
                ct.ThrowIfCancellationRequested();
                context.Response.Headers["X-AvaMedia-Preview"] = metadata;
                context.Response.ContentType = "image/png";
                context.Response.ContentLength = frame.Length;
                await context.Response.Body.WriteAsync(frame, ct);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                if (context.Response.HasStarted) context.Abort();
                else await Problem(context, "预览超时，请重新选择时间点。", 408);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            {
                if (context.Response.HasStarted) context.Abort();
                else await Problem(context, error.Message, 400);
            }
            finally { admission.Release(); }
        }
    }

    private static Task Problem(HttpContext context, string message, int status)
    {
        // A cancelled first write may have set PNG headers without starting the response.
        context.Response.ContentLength = null;
        context.Response.Headers.Remove("X-AvaMedia-Preview");
        return Results.Problem(message, statusCode: status).ExecuteAsync(context);
    }
}
