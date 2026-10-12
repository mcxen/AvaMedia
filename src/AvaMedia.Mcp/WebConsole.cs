using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace AvaMedia.Mcp;

internal static class WebConsole
{
    public static void Map(WebApplication app)
    {
        WebPreview.Map(app);
        WebClassification.Map(app);
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(webRoot, "index.html")))
        {
            var provider = new PhysicalFileProvider(webRoot);
            app.Lifetime.ApplicationStopped.Register(provider.Dispose);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".wasm"] = "application/wasm";
            types.Mappings[".dat"] = "application/octet-stream";
            types.Mappings[".blat"] = "application/octet-stream";
            types.Mappings[".dll"] = "application/octet-stream";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider, ContentTypeProvider = types,
                OnPrepareResponse = response =>
                {
                    response.Context.Response.Headers.CacheControl = "no-cache";
                    response.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    // Avalonia creates canvas/style nodes; .NET WebAssembly needs wasm compilation.
                    response.Context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
                }
            });
        }
        else app.MapGet("/", () => Results.Problem("网页界面未构建。请发布 AvaMedia.Browser 后重新构建客户端。", statusCode: 503));
        app.MapGet("/web/media", IResult (string path, HttpContext context) =>
        {
            try
            {
                path = new McpFiles().Check(path, true);
                var types = new FileExtensionContentTypeProvider();
                if (!File.Exists(path) || !types.TryGetContentType(path, out var type) ||
                    !(type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal)))
                    return Results.BadRequest("请选择可播放的视频或音频文件。");
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.File(path, type, enableRangeProcessing: true);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(error.Message); }
        });
        // Only expose paths from the shared task record; never accept a client-supplied filesystem path.
        app.MapGet("/web/tasks/{taskId:guid}/file", async Task<IResult> (Guid taskId, string source, int? index,
            bool? download, IMcpWorkspace workspace, HttpContext context) =>
        {
            try
            {
                var task = (await workspace.GetAsync(taskId, false, 0, 1, context.RequestAborted)).Task;
                string path;
                if (source == "input" && (index ?? 0) >= 0 && (index ?? 0) < task.Inputs.Length) path = task.Inputs[index ?? 0];
                else if (source == "output" && task.State is "Completed" or "Failed" or "Cancelled") path = task.Output;
                else return Results.BadRequest("文件尚不可用。");
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return Results.NotFound("文件不存在或结果为目录，请在文件浏览器中查看。");
                var types = new FileExtensionContentTypeProvider();
                if (!types.TryGetContentType(path, out var type)) type = "application/octet-stream";
                // Unknown/active document formats download instead of executing in this origin.
                var inline = type.StartsWith("image/", StringComparison.Ordinal) && type != "image/svg+xml"
                    || type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.File(path, type, download == true || !inline ? Path.GetFileName(path) : null, enableRangeProcessing: true);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
            { return Results.BadRequest(error.Message); }
        });
    }
}
