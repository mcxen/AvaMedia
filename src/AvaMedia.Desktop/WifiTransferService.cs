using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using AvaMedia.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AvaMedia.Desktop;

internal sealed record WifiNetwork(IPAddress Address, string Adapter, bool Wireless, bool HasGateway)
{
    public override string ToString() => $"{Adapter} · {Address}";
}

internal sealed record WifiSharedFile(string Id, string Path, string Name, long Bytes);
internal sealed record WifiTransferUpdate(string Id, string Path, string Name, long Bytes, long Total,
    bool Complete = false, string Error = "");

/// <summary>A temporary, explicitly started LAN endpoint. Only completed uploads become local inputs.</summary>
internal sealed class WifiTransferService : IAsyncDisposable
{
    internal const long MaxFileBytes = 50L * 1024 * 1024 * 1024;
    internal static string ReceiveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "WifiReceived");
    private readonly ConcurrentDictionary<string, WifiSharedFile> _shared = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _uploads = new(3);
    private readonly string _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private WebApplication? _application;
    private Task? _stopTask;
    public string Url { get; private set; } = "";
    public event Action<WifiTransferUpdate>? Updated;

    public static WifiNetwork[] Networks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
            adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .SelectMany(adapter =>
        {
            var properties = adapter.GetIPProperties();
            return properties.UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip.Address) && !ip.Address.Equals(IPAddress.Any))
                .Select(ip => new WifiNetwork(ip.Address, adapter.Name,
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    properties.GatewayAddresses.Any(gateway => !gateway.Address.Equals(IPAddress.Any))));
        })
        .OrderByDescending(network => network.HasGateway).ThenByDescending(network => network.Wireless)
        .DistinctBy(network => network.Address).ToArray();

    public static WifiSharedFile Share(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException("文件不存在。", path);
        return new(Guid.NewGuid().ToString("N"), info.FullName, info.Name, info.Length);
    }

    public void AddShare(WifiSharedFile file) => _shared[file.Id] = file;
    public void RemoveShare(string id) => _shared.TryRemove(id, out _);

    public async Task StartAsync(IPAddress address, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ReceiveFolder);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(WifiTransferService).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders(); // Session URLs must not be written to HTTP logs.
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(address, 0);
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = MaxFileBytes;
            server.Limits.MaxConcurrentConnections = 16;
            server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(20);
            server.Limits.MinRequestBodyDataRate = null;
        });
        var app = builder.Build();
        _application = app;
        using var pageStream = typeof(WifiTransferService).Assembly.GetManifestResourceStream("AvaMedia.Desktop.WifiTransfer.html")!;
        using var reader = new StreamReader(pageStream);
        var page = (await reader.ReadToEndAsync(cancellationToken))
            .Replace("{{NONCE}}", _nonce).Replace("{{APP_NAME}}", WebUtility.HtmlEncode(AppIdentity.ChineseName));
        app.Use(async (context, next) =>
        {
            if (Url.Length == 0 || !context.Request.Path.StartsWithSegments(_prefix) || _lifetime.IsCancellationRequested)
            { context.Response.StatusCode = 404; return; }
            var endpoint = new Uri(Url);
            if (!string.Equals(context.Request.Host.Value, endpoint.Authority, StringComparison.OrdinalIgnoreCase) ||
                (context.Request.Headers.Origin.Count > 0 &&
                 context.Request.Headers.Origin.ToString() != endpoint.GetLeftPart(UriPartial.Authority)))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{_nonce}'; " +
                $"style-src 'nonce-{_nonce}'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            await next(context);
        });
        app.MapGet(_prefix, () => Results.Content(page, "text/html; charset=utf-8"));
        app.MapGet(_prefix + "/", () => Results.Content(page, "text/html; charset=utf-8"));
        app.MapGet(_prefix + "/files", () => Results.Json(_shared.Values.Where(file => File.Exists(file.Path))
            .Select(file => new { file.Id, file.Name, bytes = new FileInfo(file.Path).Length }).OrderBy(file => file.Name)));
        app.MapPost(_prefix + "/upload", (Func<HttpContext, Task<IResult>>)ReceiveAsync);
        app.MapGet(_prefix + "/download/{id}", (string id) =>
            _shared.TryGetValue(id, out var file) && File.Exists(file.Path)
                ? Results.File(file.Path, "application/octet-stream", file.Name, enableRangeProcessing: true)
                : Results.NotFound());
        try
        {
            await app.StartAsync(cancellationToken);
            var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Url = bound.TrimEnd('/') + _prefix + "/";
        }
        catch { await StopAsync(); throw; }
    }

    private async Task<IResult> ReceiveAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return Error("请选择文件后上传。", 415);
        if (context.Request.ContentLength is not { } total || total < 0 || total > MaxFileBytes)
            return Error("单个文件须小于或等于 50 GB，且浏览器须提供文件大小。", 413);
        var rawName = context.Request.Headers["X-File-Name"].ToString();
        if (rawName.Length == 0 || rawName.Length > 4096) return Error("文件名无效。", 400);
        string name;
        try { name = SafeName(Uri.UnescapeDataString(rawName)); }
        catch (ArgumentException) { return Error("文件名无效。", 400); }
        if (!await _uploads.WaitAsync(0, context.RequestAborted)) return Error("接收任务较多，请稍后重试。", 429);
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(ReceiveFolder, id);
        var temporary = Path.Combine(folder, ".uploading");
        var destination = Path.Combine(folder, name);
        var complete = false;
        long received = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
        var token = cancellation.Token;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(ReceiveFolder)!);
            if (drive.IsReady && drive.AvailableFreeSpace < total + 16 * 1024 * 1024)
                return Error("电脑接收目录的可用空间不足。", 507);
            Directory.CreateDirectory(folder);
            Updated?.Invoke(new(id, destination, name, 0, total));
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                var lastUpdate = Environment.TickCount64;
                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(buffer, token);
                    if (read == 0) break;
                    received += read;
                    if (received > total) throw new InvalidDataException("接收大小与文件大小不一致。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (Environment.TickCount64 - lastUpdate >= 300)
                    {
                        Updated?.Invoke(new(id, destination, name, received, total));
                        lastUpdate = Environment.TickCount64;
                    }
                }
                await output.FlushAsync(token);
            }
            if (received != total) throw new InvalidDataException("文件尚未完整接收，请重新上传。");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            complete = true;
            Updated?.Invoke(new(id, destination, name, total, total, Complete: true));
            return Results.Json(new { id, name, bytes = total });
        }
        catch (OperationCanceledException)
        {
            Updated?.Invoke(new(id, destination, name, received, total, Error: "传输已中断，请重新上传。"));
            return Error("传输已中断，请重新上传。", 409);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            var message = exception is InvalidDataException ? exception.Message : "接收失败，请检查电脑可用空间与目录权限后重试。";
            Updated?.Invoke(new(id, destination, name, received, total, Error: message));
            return Error(message, 500);
        }
        finally
        {
            if (!complete)
            {
                try { File.Delete(temporary); if (Directory.Exists(folder)) Directory.Delete(folder); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("WiFi partial upload cleanup: " + exception.Message); }
            }
            _uploads.Release();
        }
    }

    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);

    private static string SafeName(string supplied)
    {
        var name = supplied.Replace('\\', '/').Split('/').Last();
        var invalid = "<>:\"/\\|?*";
        name = new string(name.Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..") throw new ArgumentException("文件名无效。");
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 30) extension = "";
            name = name[..(180 - extension.Length)] + extension;
        }
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
            (char.IsDigit(stem[3]) || stem[3] is '¹' or '²' or '³')) name = "_" + name;
        return name == ".uploading" ? "_uploading" : name;
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _lifetime.Cancel();
        if (_application is { } app)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            finally { await app.DisposeAsync(); _application = null; }
        }
        Url = "";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
        _uploads.Dispose();
    }
}
