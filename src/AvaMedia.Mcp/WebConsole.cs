using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace AvaMedia.Mcp;

internal static class WebConsole
{
    public static void Map(WebApplication app)
    {
        foreach (var (route, file, contentType) in new[]
        {
            ("/", "index.html", "text/html; charset=utf-8"),
            ("/console.css", "console.css", "text/css; charset=utf-8"),
            ("/console.js", "console.js", "text/javascript; charset=utf-8"),
            ("/mcp-client.js", "mcp-client.js", "text/javascript; charset=utf-8"),
            ("/task-results.js", "task-results.js", "text/javascript; charset=utf-8")
        })
        {
            using var stream = typeof(WebConsole).Assembly.GetManifestResourceStream($"AvaMedia.Mcp.Web.{file}")
                ?? throw new InvalidOperationException("网页资源缺失：" + file);
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            app.MapGet(route, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; media-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
                return Results.Content(content, contentType);
            });
        }
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
