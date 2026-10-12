using System.Net;
using AvaMedia.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace AvaMedia.Mcp;

/// <summary>Embedded HTTP endpoint sharing the desktop application's workspace and lifetime.</summary>
public sealed class McpService(IMcpWorkspace workspace) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly object _activity = new();
    private WebApplication? _app;
    private McpSettings? _running;
    private int _activeRequests;
    private long _quietSince = System.Diagnostics.Stopwatch.GetTimestamp();
    public string Status { get; private set; } = "已关闭";
    public string? Endpoint { get; private set; }
    public event Action? Changed;
    private readonly McpClientLog _clients = new();
    /// <summary>Clients actually seen by this listener (address, user agent, last request); nothing is persisted.</summary>
    public McpClientView[] RecentClients => _clients.Snapshot();
    public event Action? ClientsChanged { add => _clients.Changed += value; remove => _clients.Changed -= value; }
    public bool HasActiveRequests { get { lock (_activity) return _activeRequests > 0; } }
    public TimeSpan QuietFor => HasActiveRequests ? TimeSpan.Zero : System.Diagnostics.Stopwatch.GetElapsedTime(_quietSince);

    public async Task ConfigureAsync(McpSettings settings, CancellationToken ct = default)
    {
        settings = settings.Clone(); settings.Validate();
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is not null && _running is { } active && settings.Enabled && active.Port == settings.Port && active.AllowLan == settings.AllowLan) return;
            await StopCoreAsync().ConfigureAwait(false);
            if (!settings.Enabled) return;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { ApplicationName = typeof(McpService).Assembly.GetName().Name, Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(settings.AllowLan ? IPAddress.Any : IPAddress.Loopback, settings.Port);
                options.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
            });
            builder.Services.AddSingleton(workspace);
            builder.Services.AddSingleton<McpFiles>();
            builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
                .WithExposedHeaders("Mcp-Protocol-Version", "Mcp-Session-Id")));
            builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "AvaMedia", Version = typeof(McpService).Assembly.GetName().Version?.ToString() ?? "1.0" };
                options.ServerInstructions = "AvaMedia exposes the desktop's shared task queue and local media services. Call avamedia_capabilities first. "
                    + "Use avamedia_get_runtime and avamedia_list_models to inspect resource policy and model availability. No task tool opens a classification window. "
                    + "Create tasks with a fresh UUID requestId and poll their durable queue IDs. File paths are absolute paths on this server's machine. "
                    + "For remote file selection use avamedia_file_locations then avamedia_browse_directory; send returned paths unchanged to avamedia_create_task without uploading the media. "
                    + "Use avamedia_wait_task for bounded waits, get_task_log for diagnostics, and export_task_result to save a complete paginated JSON report. "
                    + "Classification analyzes only; preview_organization/apply_organization copy results into folders while keeping originals. "
                    + "For renaming: analyze labels, preview exact names, then apply the plan. File/service access is unrestricted; no authentication is required.";
            }).WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
              .WithTools<McpTools>()
              .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
              {
                  NoteActivity(1);
                  try { return await next(request, token).ConfigureAwait(false); }
                  catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException
                      or UnauthorizedAccessException or System.Text.Json.JsonException)
                  {
                      // Return actionable service/validation errors without exposing stack traces.
                      throw new McpException(error.Message, error);
                  }
                  finally { NoteActivity(-1); }
              }));
            var app = builder.Build();
            app.UseCors();
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                if (path.StartsWithSegments("/mcp") || path.StartsWithSegments("/web"))
                    _clients.Note(context.Connection.RemoteIpAddress, context.Request.Headers.UserAgent.ToString(), path.StartsWithSegments("/mcp"));
                await next(context);
            });
            WebConsole.Map(app);
            app.MapMcp("/mcp");
            try { await app.StartAsync(ct).ConfigureAwait(false); }
            catch { await app.DisposeAsync().ConfigureAwait(false); throw; }
            _app = app; _running = settings; Endpoint = $"http://127.0.0.1:{settings.Port}/mcp";
            Status = settings.AllowLan ? "运行中 · 局域网" : "运行中 · 本机"; Changed?.Invoke();
        }
        catch (Exception error)
        {
            Status = "启动失败：" + error.Message; Changed?.Invoke(); throw;
        }
        finally { _lifecycle.Release(); }
    }

    private void NoteActivity(int delta)
    {
        lock (_activity)
        {
            _activeRequests = Math.Max(0, _activeRequests + delta);
            if (_activeRequests == 0) _quietSince = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private async Task StopCoreAsync()
    {
        var app = _app; _app = null; _running = null; Endpoint = null;
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally { await app.DisposeAsync().ConfigureAwait(false); }
        }
        Status = "已关闭"; Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
}
