using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Public update routes; HttpClient retains the system/environment proxy configuration.</summary>
public sealed class GitHubUpdateRoutes
{
    // Service documentation and the bounded live probes are recorded in docs/UPDATE-NETWORK.md.
    private static readonly string[] Proxies =
    [
        "https://tvv.tw/", "https://gh-proxy.com/", "https://gh-proxy.org/",
        "https://hk.gh-proxy.com/", "https://cdn.gh-proxy.com/", "https://v6.gh-proxy.com/",
        "https://v4.gh-proxy.org/", "https://v6.gh-proxy.org/", "https://cdn.gh-proxy.org/",
        "https://axisnow.gh-proxy.org/"
    ];
    private readonly object _gate = new();
    private readonly Dictionary<string, RouteHealth> _health = new();
    private static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AvaMedia", "Updates", "routes.json");
    private sealed record RouteHealth(double Milliseconds, DateTimeOffset CheckedAt, DateTimeOffset RetryAt, int Failures);
    private static readonly TimeSpan HealthLifetime = TimeSpan.FromHours(24);
    public static GitHubUpdateRoutes Shared { get; } = new();
    private static string Key(string route, bool package) => (package ? "package:" : "release:") + route;

    public GitHubUpdateRoutes()
    {
        try
        {
            if (!File.Exists(CachePath) || new FileInfo(CachePath).Length > 65536) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, RouteHealth>>(File.ReadAllText(CachePath));
            if (saved is null) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in saved)
                if (pair.Value is { } health && health.CheckedAt <= now && now - health.CheckedAt < HealthLifetime &&
                    double.IsFinite(health.Milliseconds) && health.Milliseconds >= 0)
                    _health[pair.Key] = health;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Trace.TraceWarning("读取更新线路缓存失败：{0}", ex.Message); }
    }

    private string[] Candidates(bool package)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            RouteHealth? Health(string route) => _health.TryGetValue(Key(route, package), out var health) &&
                now - health.CheckedAt < HealthLifetime ? health : null;
            return new[] { "" }.Concat(Proxies)
                .Where(route => Health(route) is not { } health || health.RetryAt <= now)
                .OrderBy(route => Health(route) is { Failures: 0 } good ? good.Milliseconds :
                    package && _health.TryGetValue(Key(route, false), out var release) && release.Failures == 0 &&
                    now - release.CheckedAt < HealthLifetime ? release.Milliseconds : double.MaxValue)
                .ToArray();
        }
    }

    private void Remember(string route, bool package, double milliseconds, DateTimeOffset? retryAt = null)
    {
        lock (_gate)
        {
            var key = Key(route, package); var now = DateTimeOffset.UtcNow;
            _health.TryGetValue(key, out var previous);
            var failures = retryAt is null ? 0 : Math.Min(10, (previous?.Failures ?? 0) + 1);
            _health[key] = new(retryAt is null ? milliseconds : previous?.Milliseconds ?? 0, now,
                retryAt ?? DateTimeOffset.MinValue, failures);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                var temporary = CachePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_health));
                File.Move(temporary, CachePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("保存更新线路缓存失败：{0}", ex.Message); }
        }
    }

    private static Uri Address(string route, Uri original) => route.Length == 0 ? original : new(route + original.AbsoluteUri);
    private static HttpRequestMessage Request(string route, Uri original, Version version, bool package)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Address(route, original));
        request.Headers.UserAgent.ParseAdd("AvaMedia/" + version.ToString(3));
        if (!package)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.CacheControl = new() { NoCache = true };
        }
        return request;
    }

    private static DateTimeOffset RetryAt(HttpResponseMessage? response)
    {
        var now = DateTimeOffset.UtcNow; var retry = now.AddMinutes(1);
        if (response?.Headers.RetryAfter is { } header)
        {
            var instructed = header.Date ?? now.Add(header.Delta ?? TimeSpan.Zero);
            if (instructed > retry) retry = instructed;
        }
        if (response?.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) == true && remaining.Contains("0") &&
            response.Headers.TryGetValues("x-ratelimit-reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out var epoch))
        {
            try { var reset = DateTimeOffset.FromUnixTimeSeconds(epoch).AddSeconds(1); if (reset > retry) retry = reset; }
            catch (ArgumentOutOfRangeException) { }
        }
        return retry;
    }

    /// <summary>At most three requests in flight; briefly collect valid replies and prefer the newest version.</summary>
    public async Task<UpdateResult> FetchReleaseAsync(HttpClient client, Uri endpoint, Version version,
        Func<HttpResponseMessage, CancellationToken, Task<UpdateResult>> read, CancellationToken ct)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var slots = new SemaphoreSlim(3, 3);
        var tasks = Candidates(package: false).Select((route, index) => AttemptAsync(route, index)).ToList();
        var pending = tasks.ToList();
        try
        {
            UpdateResult? best = null; Task? settle = null;
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(settle is null ? pending.Cast<Task>() : pending.Cast<Task>().Append(settle));
                if (completed == settle) break;
                var attempt = (Task<(bool Succeeded, UpdateResult? Value)>)completed;
                pending.Remove(attempt);
                var result = await attempt;
                if (!result.Succeeded || result.Value is null) continue;
                if (best is null || result.Value.LatestVersion > best.LatestVersion) best = result.Value;
                settle ??= Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            }
            ct.ThrowIfCancellationRequested();
            if (best is not null) return best;
            throw new HttpRequestException("所有更新线路暂时不可用。");
        }
        finally
        {
            race.Cancel();
            await Task.WhenAll(tasks);
        }

        async Task<(bool Succeeded, UpdateResult? Value)> AttemptAsync(string route, int index)
        {
            var entered = false; HttpResponseMessage? response = null;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(index * 350), race.Token);
                await slots.WaitAsync(race.Token); entered = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var request = Request(route, endpoint, version, package: false);
                var clock = Stopwatch.StartNew();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                var value = await read(response, timeout.Token);
                Remember(route, false, clock.Elapsed.TotalMilliseconds);
                return (true, value);
            }
            catch (OperationCanceledException) when (race.IsCancellationRequested) { return (false, default); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or
                JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Remember(route, false, 0, RetryAt(response));
                Trace.TraceWarning("更新检查线路 {0}：{1}", route.Length == 0 ? "GitHub" : route, ex.Message);
                return (false, default);
            }
            finally { response?.Dispose(); if (entered) slots.Release(); }
        }
    }

    public static async Task<string> ReadReleaseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = 2 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("发布信息过大。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("发布信息过大。");
            output.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    /// <summary>Only complete, size- and SHA256-verified packages are accepted from any route.</summary>
    public async Task DownloadAsync(HttpClient client, ReleaseAsset asset, string package, Version version,
        Action<long> progress, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var route in Candidates(package: true))
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage? response = null;
            try
            {
                progress(0);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var request = Request(route, asset.DownloadUrl, version, package: true);
                var clock = Stopwatch.StartNew();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                    throw new InvalidDataException("安装包大小不匹配。");
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(package, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long received = 0; var lastReport = Environment.TickCount64;
                while (true)
                {
                    int count;
                    try { count = await input.ReadAsync(buffer, timeout.Token); }
                    catch (IOException ex) { throw new HttpRequestException("安装包传输中断。", ex); }
                    if (count == 0) break;
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("安装包大小不匹配。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    var now = Environment.TickCount64;
                    if (now - lastReport >= 100 || received == asset.Size) { progress(received); lastReport = now; }
                }
                if (received != asset.Size) throw new HttpRequestException("安装包传输不完整。");
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包 SHA256 校验失败。");
                // Normalize by size so large-package latency ranks routes by actual throughput.
                Remember(route, true, clock.Elapsed.TotalMilliseconds / Math.Max(1, asset.Size / 1048576d));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException)
            {
                last = ex; Remember(route, true, 0, RetryAt(response));
                Trace.TraceWarning("更新下载线路 {0}：{1}", route.Length == 0 ? "GitHub" : route, ex.Message);
            }
            finally { response?.Dispose(); }
        }
        if (last is InvalidDataException) throw last;
        throw new HttpRequestException("所有更新下载线路暂时不可用。", last);
    }
}
