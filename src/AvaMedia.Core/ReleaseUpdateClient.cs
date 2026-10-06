using System.Net;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record UpdateResult(bool HasUpdate, string Message, Uri? ReleasePage = null,
    bool CheckSucceeded = true, DateTimeOffset? RetryAt = null);

public sealed class ReleaseUpdateClient(HttpClient client)
{
    public static readonly Uri Endpoint = new("https://api.github.com/repos/mcxen/AvaMedia/releases/latest");
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private UpdateResult? _cached;
    private Version? _cachedVersion;
    private DateTimeOffset _cacheUntil;
    public async Task<UpdateResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is not null && _cachedVersion == current && now < _cacheUntil) return _cached;
            UpdateResult result;
            try { result = await FetchAsync(current, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { result = Unavailable("检查更新超时，请稍后重试", now.AddMinutes(1)); }
            catch (HttpRequestException)
            { result = Unavailable("暂时无法连接更新服务，请检查网络后重试", now.AddMinutes(1)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { result = Unavailable("更新服务返回的信息无法识别，请稍后重试", now.AddMinutes(1)); }
            ct.ThrowIfCancellationRequested();
            _cached = result; _cachedVersion = current;
            _cacheUntil = result.RetryAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
            return result;
        }
        finally { _checkGate.Release(); }
    }
    private async Task<UpdateResult> FetchAsync(Version current, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("AvaMedia/" + current.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "尚无正式发布版本。");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var now = DateTimeOffset.UtcNow;
            var body = await response.Content.ReadAsStringAsync(ct);
            var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.Contains("0");
            var limited = response.StatusCode == HttpStatusCode.TooManyRequests || exhausted || response.Headers.RetryAfter is not null ||
                body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                response.ReasonPhrase?.Contains("rate limit", StringComparison.OrdinalIgnoreCase) == true;
            var retryAt = RetryTime(response, now, exhausted);
            return Unavailable(limited ? $"检查更新暂时受限，请在 {retryAt.LocalDateTime:MM-dd HH:mm:ss} 后重试" :
                "更新服务暂时拒绝请求，请稍后重试", retryAt);
        }
        if (!response.IsSuccessStatusCode)
            return Unavailable("更新服务暂时不可用，请稍后重试", DateTimeOffset.UtcNow.AddMinutes(1));
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = data.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out var preview) && preview.GetBoolean())
            return new(false, "未发现更新的正式版本。");
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new InvalidDataException("发布版本号无法识别。");
        var normalizedCurrent = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        var normalizedLatest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (normalizedLatest <= normalizedCurrent) return new(false, $"当前版本 {normalizedCurrent}，已是最新正式版本。");
        if (!Uri.TryCreate(release.GetProperty("html_url").GetString(), UriKind.Absolute, out var page) ||
            page.Scheme != "https" || page.Host != "github.com" || page.UserInfo.Length>0 || page.Port!=443 || !page.AbsolutePath.StartsWith("/mcxen/AvaMedia/releases/tag/", StringComparison.Ordinal))
            throw new InvalidDataException("发布页面地址无效。");
        return new(true, $"新版本 {normalizedLatest} 可用，当前版本 {normalizedCurrent}。", page);
    }
    private static UpdateResult Unavailable(string message, DateTimeOffset retryAt) =>
        new(false, message + "。转换和已保存设置不受影响。", CheckSucceeded: false, RetryAt: retryAt);

    private static DateTimeOffset RetryTime(HttpResponseMessage response, DateTimeOffset now, bool exhausted)
    {
        var retryAt = now.AddMinutes(1);
        var retryHeader = response.Headers.RetryAfter;
        var instructed = retryHeader?.Date ?? (retryHeader?.Delta is {} delay ? now.Add(delay) : now);
        if (instructed > retryAt) retryAt = instructed;
        if (exhausted && response.Headers.TryGetValues("x-ratelimit-reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out var epoch))
        {
            try { var reset = DateTimeOffset.FromUnixTimeSeconds(epoch).AddSeconds(1); if (reset > retryAt) retryAt = reset; }
            catch (ArgumentOutOfRangeException) { }
        }
        return retryAt;
    }
}
