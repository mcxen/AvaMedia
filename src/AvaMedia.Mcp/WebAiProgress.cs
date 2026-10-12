using AvaMedia.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SkiaSharp;

namespace AvaMedia.Mcp;

internal static class WebAiProgress
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/web/tasks/{id:guid}/ai");
        routes.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (NotSupportedException) { return Results.StatusCode(StatusCodes.Status501NotImplemented); }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { return Results.BadRequest(error.Message); }
        });
        routes.MapGet("/progress", async (Guid id, IMcpWorkspace workspace, CancellationToken ct) =>
            Results.Ok(await workspace.AiProgressAsync(id, ct)));
        routes.MapGet("/preview", async Task<IResult> (Guid id, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            var bytes = await workspace.AiPreviewAsync(id, ct);
            if (bytes is not { Length: > 0 }) return Results.NotFound();
            // Activity previews may be PNG; the public endpoint always serves JPEG.
            var jpeg = await Task.Run(() =>
            {
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap is null) return null;
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                return encoded?.ToArray();
            }, ct);
            return jpeg is null ? Results.NotFound() : Results.Bytes(jpeg, "image/jpeg");
        });
        routes.MapGet("/waveform", async Task<IResult> (Guid id, int? buckets, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            if (buckets is < 1 or > 10000) return Results.BadRequest();
            var task = (await workspace.GetAsync(id, false, 0, 1, ct)).Task;
            if (task.Inputs.FirstOrDefault() is not { } path) return Results.NotFound();
            var envelope = await AudioEnvelopeService.GetAsync(path, buckets ?? 2000, ct);
            return envelope is null ? Results.NotFound() : Results.Ok(envelope);
        });
        routes.MapGet("/segments", async Task<IResult> (Guid id, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            var progress = await workspace.AiProgressAsync(id, ct);
            return progress.SummarySegments is not null ? Results.Ok(progress.SummarySegments) : progress.Speech is null ? Results.NotFound() : Results.Ok(progress.Speech);
        });
        routes.MapGet("/summary/evidence/{evidenceId}", async Task<IResult> (Guid id, string evidenceId, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            if (evidenceId.Length > 100) return Results.BadRequest();
            var evidence = await workspace.SummaryEvidenceAsync(id, evidenceId, ct);
            return evidence is null ? Results.NotFound() : Results.Ok(evidence);
        });
        routes.MapGet("/results", async Task<IResult> (Guid id, int? after, IMcpWorkspace workspace, CancellationToken ct) =>
        {
            if (after is < 0) return Results.BadRequest();
            var result = await workspace.GetAsync(id, true, after ?? 0, 100, ct);
            if (result.Result is LongSummaryTaskSnapshot summary) return Results.Ok(summary);
            var cues = (result.Result as SubtitleTaskResult)?.Cues ?? [];
            return Results.Ok(new { total = cues.Length, cues = cues.Skip(after ?? 0).Select(cue =>
                new { start = cue.Start.TotalSeconds, end = cue.End.TotalSeconds, text = cue.Text }).ToArray() });
        });
    }
}
