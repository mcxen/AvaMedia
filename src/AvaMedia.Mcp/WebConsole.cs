using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
            ("/mcp-client.js", "mcp-client.js", "text/javascript; charset=utf-8")
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
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
                return Results.Content(content, contentType);
            });
        }
    }
}
