using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
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
    bool Complete = false, string Error = "", string UserId = "", string UserName = "");
internal sealed record WifiTransferUser(string Id, string Name, string Address, bool Online, int Transfers);
internal sealed record WifiTransferOwner(string Id, string Name);
internal sealed record WifiSessionRequest(string? Name);

/// <summary>A temporary, explicitly started LAN endpoint. Only completed uploads become local inputs.</summary>
internal sealed class WifiTransferService : IAsyncDisposable
{
    internal const long MaxFileBytes = 50L * 1024 * 1024 * 1024;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    internal static string ReceiveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "WifiReceived");
    private readonly ConcurrentDictionary<string, WifiSharedFile> _shared = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _uploads = new(8);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private WifiTransferUser[] _lastUsers = [];
    private long _lastActivity = Environment.TickCount64;
    private int _activeTransfers;
    private bool _stopping;
    private bool _autoClose = true;
    private Task _monitor = Task.CompletedTask;
    private readonly string _prefix = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    private WebApplication? _application;
    private Task? _stopTask;
    public string Url { get; private set; } = "";
    public event Action<WifiTransferUpdate>? Updated;
    public event Action<WifiTransferUser[]>? UsersChanged;
    public event Action? IdleExpired;

    private sealed class Client(string id, string name, string address)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public string Address { get; } = address;
        public long LastSeen { get; set; } = Environment.TickCount64;
        public int Transfers { get; set; }
    }

    internal static WifiTransferUpdate[] LoadReceived()
    {
        Directory.CreateDirectory(ReceiveFolder);
        return Directory.EnumerateDirectories(ReceiveFolder)
            .Where(directory => Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            .SelectMany(directory => Directory.EnumerateFiles(directory).Where(path => Path.GetFileName(path) != ".uploading"))
            .Select(path => new FileInfo(path)).OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info =>
            {
                var id = Path.GetFileName(info.DirectoryName)!;
                WifiTransferOwner? owner = null;
                try
                {
                    var metadata = MetadataPath(id);
                    if (File.Exists(metadata)) owner = JsonSerializer.Deserialize<WifiTransferOwner>(File.ReadAllText(metadata));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                { System.Diagnostics.Trace.TraceWarning("WiFi owner metadata: " + exception.Message); }
                return new WifiTransferUpdate(id, info.FullName, info.Name, info.Length, info.Length,
                    Complete: true, UserId: owner?.Id ?? "", UserName: owner?.Name ?? "");
            }).ToArray();
    }

    private static string MetadataPath(string id) => Path.Combine(ReceiveFolder, ".metadata", id + ".json");

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

    public void AddShare(WifiSharedFile file) { _shared[file.Id] = file; TouchActivity(); }
    public void RemoveShare(string id) { _shared.TryRemove(id, out _); TouchActivity(); }

    private void TouchActivity() { lock (_stateGate) _lastActivity = Environment.TickCount64; }

    public void SetAutoClose(bool enabled)
    {
        lock (_stateGate) { _autoClose = enabled; _lastActivity = Environment.TickCount64; }
    }

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
            server.Limits.MaxConcurrentConnections = 64;
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
            var client = GetClient(context);
            var transfer = context.Request.Path == _prefix + "/upload" ||
                context.Request.Path.StartsWithSegments(_prefix + "/download");
            if (!transfer) { await next(context); return; }
            if (client is null) { context.Response.StatusCode = 401; return; }
            lock (_stateGate)
            {
                if (_stopping) { context.Response.StatusCode = 503; return; }
                _activeTransfers++; client.Transfers++; _lastActivity = Environment.TickCount64;
            }
            PublishUsers();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
            context.RequestAborted = cancellation.Token;
            try { await next(context); }
            finally
            {
                lock (_stateGate)
                {
                    _activeTransfers--; client.Transfers--; client.LastSeen = _lastActivity = Environment.TickCount64;
                }
                PublishUsers();
            }
        });
        app.MapGet(_prefix, () => Results.Content(page, "text/html; charset=utf-8"));
        app.MapGet(_prefix + "/", () => Results.Content(page, "text/html; charset=utf-8"));
        app.MapPost(_prefix + "/session", (Func<HttpContext, Task<IResult>>)RegisterAsync);
        app.MapGet(_prefix + "/files", (HttpContext context) => GetClient(context) is null ? Results.Unauthorized() :
            Results.Json(_shared.Values.Where(file => File.Exists(file.Path))
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
            TouchActivity();
            _monitor = MonitorAsync();
        }
        catch { await StopAsync(); throw; }
    }

    private Client? GetClient(HttpContext context)
    {
        var id = context.Request.Cookies["AvaMediaWifiUser"];
        lock (_stateGate)
        {
            if (id is null || !_clients.TryGetValue(id, out var client)) return null;
            client.LastSeen = Environment.TickCount64;
            return client;
        }
    }

    private async Task<IResult> RegisterAsync(HttpContext context)
    {
        if (context.Request.ContentLength is not { } size || size > 1024)
            return Error("用户信息过长。", 400);
        WifiSessionRequest? request;
        try { request = await context.Request.ReadFromJsonAsync<WifiSessionRequest>(context.RequestAborted); }
        catch (Exception exception) when (exception is JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        { return Error("用户信息无效。", 400); }
        var name = request?.Name?.Trim() ?? "";
        if (name.Length > 32 || name.Any(char.IsControl)) return Error("用户名称最多 32 个字符。", 400);
        Client client;
        var existing = GetClient(context);
        lock (_stateGate)
        {
            if (_stopping) return Error("传输已关闭，请重新扫码。", 503);
            if (existing is not null)
            {
                client = existing;
                if (name.Length > 0) client.Name = name;
            }
            else
            {
                if (_clients.Count >= 32) return Error("连接用户较多，请稍后重试。", 429);
                client = new(Guid.NewGuid().ToString("N"), name.Length > 0 ? name : $"用户 {_clients.Count + 1}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "");
                _clients.Add(client.Id, client);
            }
            _lastActivity = client.LastSeen = Environment.TickCount64;
        }
        context.Response.Cookies.Append("AvaMediaWifiUser", client.Id, new CookieOptions
        { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = _prefix, IsEssential = true });
        PublishUsers();
        return Results.Json(new { client.Id, client.Name });
    }

    private void PublishUsers()
    {
        lock (_stateGate)
        {
            var now = Environment.TickCount64;
            var users = _clients.Values.Select(client => new WifiTransferUser(client.Id, client.Name, client.Address,
                !_stopping && (client.Transfers > 0 || now - client.LastSeen < 30000), client.Transfers)).ToArray();
            if (_lastUsers.SequenceEqual(users)) return;
            _lastUsers = users;
            UsersChanged?.Invoke(users);
        }
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                PublishUsers();
                lock (_stateGate)
                {
                    if (!_autoClose || _activeTransfers > 0 || Environment.TickCount64 - _lastActivity < IdleTimeout.TotalMilliseconds) continue;
                    _stopping = true;
                }
                IdleExpired?.Invoke();
                await StopAsync();
                break;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task<IResult> ReceiveAsync(HttpContext context)
    {
        var client = GetClient(context);
        if (client is null) return Results.Unauthorized();
        WifiTransferOwner owner;
        lock (_stateGate) owner = new(client.Id, client.Name);
        if (!string.Equals(context.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return Error("请选择文件后上传。", 415);
        if (context.Request.ContentLength is not { } total || total < 0 || total > MaxFileBytes)
            return Error("单个文件须小于或等于 50 GB，且浏览器须提供文件大小。", 413);
        var rawName = context.Request.Headers["X-File-Name"].ToString();
        if (rawName.Length == 0 || rawName.Length > 4096) return Error("文件名无效。", 400);
        string name;
        try { name = SafeName(Uri.UnescapeDataString(rawName)); }
        catch (ArgumentException) { return Error("文件名无效。", 400); }
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(ReceiveFolder, id);
        var temporary = Path.Combine(folder, ".uploading");
        var destination = Path.Combine(folder, name);
        var complete = false;
        var acquired = false;
        long received = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
        var token = cancellation.Token;
        try
        {
            await _uploads.WaitAsync(token);
            acquired = true;
            var drive = new DriveInfo(Path.GetPathRoot(ReceiveFolder)!);
            if (drive.IsReady && drive.AvailableFreeSpace < total + 16 * 1024 * 1024)
                return Error("电脑接收目录的可用空间不足。", 507);
            Directory.CreateDirectory(folder);
            Report(0);
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
                        Report(received);
                        lastUpdate = Environment.TickCount64;
                    }
                }
                await output.FlushAsync(token);
            }
            if (received != total) throw new InvalidDataException("文件尚未完整接收，请重新上传。");
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath(id))!);
            await File.WriteAllTextAsync(MetadataPath(id), JsonSerializer.Serialize(owner), token);
            File.Move(temporary, destination);
            complete = true;
            Report(total, completed: true);
            return Results.Json(new { id, name, bytes = total });
        }
        catch (OperationCanceledException)
        {
            Report(received, error: "传输已中断，请重新上传。");
            return Error("传输已中断，请重新上传。", 409);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            var message = exception is InvalidDataException ? exception.Message : "接收失败，请检查电脑可用空间与目录权限后重试。";
            Report(received, error: message);
            return Error(message, 500);
        }
        finally
        {
            if (!complete)
            {
                try { File.Delete(temporary); File.Delete(MetadataPath(id)); if (Directory.Exists(folder)) Directory.Delete(folder); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning("WiFi partial upload cleanup: " + exception.Message); }
            }
            if (acquired) _uploads.Release();
        }
        void Report(long bytes, bool completed = false, string error = "") =>
            Updated?.Invoke(new(id, destination, name, bytes, total, completed, error, owner.Id, owner.Name));
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

    public Task StopAsync()
    {
        lock (_stateGate) { _stopping = true; return _stopTask ??= StopCoreAsync(); }
    }

    private async Task StopCoreAsync()
    {
        _lifetime.Cancel();
        PublishUsers();
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
        await _monitor;
        _lifetime.Dispose();
        _uploads.Dispose();
    }
}
