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
        app.MapGet("/web/preview/frame", (string path, double seconds, int? width, int? videoStreamIndex, int? audioStreamIndex,
            IMcpWorkspace workspace, HttpContext context) => Run(context, async ct =>
        {
            path = new McpFiles().Check(path, true);
            if (!VideoFormats.IsVideo(path) && !ImageFormats.Supports(path))
                throw new ArgumentException("预览请选择视频或图片。");
            if (!double.IsFinite(seconds) || seconds < 0)
                throw new ArgumentException("预览时间无效。");
            // Two cache-friendly sizes; clients cannot request full-resolution decoding output.
            var frameWidth = width > 640 ? 960 : 640;
            var sourceVersion = McpMediaFile.Version(path);
            var info = await workspace.Engine.Probe(path, ct, videoStreamIndex ?? 0, audioStreamIndex ?? 0);
            if (info.Duration > 0) seconds = Math.Min(seconds, Math.Max(0, info.Duration - .05));
            var frame = await workspace.Engine.Thumbnail(path, seconds, frameWidth, frameWidth * 9 / 16, ct, pad: false, videoStreamIndex: info.VideoStreamIndex);
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
