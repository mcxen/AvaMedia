using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public sealed record UpdateResult(bool HasUpdate, string Message, Uri? ReleasePage = null,
    bool CheckSucceeded = true, DateTimeOffset? RetryAt = null, Version? LatestVersion = null,
    ReleaseAsset? Asset = null);

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string Sha256);

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
            { result = Unavailable(); }
            catch (HttpRequestException)
            { result = Unavailable(); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { result = Unavailable(); }
            ct.ThrowIfCancellationRequested();
            _cached = result; _cachedVersion = current;
            _cacheUntil = result.RetryAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
            return result;
        }
        finally { _checkGate.Release(); }
    }
    private Task<UpdateResult> FetchAsync(Version current, CancellationToken ct) =>
        GitHubUpdateRoutes.Shared.FetchReleaseAsync(client, Endpoint, current,
            (response, token) => ReadReleaseAsync(response, current, token), ct);

    private static async Task<UpdateResult> ReadReleaseAsync(HttpResponseMessage response, Version current, CancellationToken ct)
    {
        using var data = JsonDocument.Parse(await GitHubUpdateRoutes.ReadReleaseAsync(response, ct));
        var release = data.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out var preview) && preview.GetBoolean())
            throw new InvalidDataException("更新线路返回的不是正式版本。");
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new InvalidDataException("发布版本号无法识别。");
        var normalizedCurrent = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        var normalizedLatest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (!Uri.TryCreate(release.GetProperty("html_url").GetString(), UriKind.Absolute, out var page) ||
            page.Scheme != "https" || page.Host != "github.com" || page.UserInfo.Length>0 || page.Port!=443 ||
            page.AbsolutePath != $"/mcxen/AvaMedia/releases/tag/{tag}" || page.Query.Length > 0 || page.Fragment.Length > 0)
            throw new InvalidDataException("发布页面地址无效。");
        if (normalizedLatest <= normalizedCurrent) return new(false, $"当前版本 {normalizedCurrent}，已是最新正式版本。", LatestVersion: normalizedLatest);
        var suffix = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? (File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) ? "win-x64-setup.exe" : "win-x64-portable.zip")
            : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64.dmg" : null;
        ReleaseAsset? asset = null;
        var name = $"AvaMedia-{normalizedLatest}-{suffix}";
        if (suffix is not null && release.TryGetProperty("assets", out var assets))
        {
            foreach (var item in assets.EnumerateArray())
            {
                if (item.GetProperty("name").GetString() != name) continue;
                if (!Uri.TryCreate(item.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) ||
                    url.Scheme != "https" || url.Host != "github.com" || url.Port != 443 || url.UserInfo.Length > 0 ||
                    url.AbsolutePath != $"/mcxen/AvaMedia/releases/download/{tag}/{name}")
                    throw new InvalidDataException("安装包地址无效。");
                var digest = item.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                var sha256 = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : "";
                if (!Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$") && release.TryGetProperty("body", out var bodyText))
                {
                    var match = Regex.Match(bodyText.GetString() ?? "", @"(?m)^([a-fA-F0-9]{64})[ \t]+" + Regex.Escape(name) + @"[ \t]*\r?$");
                    sha256 = match.Success ? match.Groups[1].Value : "";
                }
                var size = item.GetProperty("size").GetInt64();
                if (size > 0 && Regex.IsMatch(sha256, "^[a-fA-F0-9]{64}$")) asset = new(name, url, size, sha256);
                break;
            }
        }
        return new(true, $"新版本 {normalizedLatest} 可用，当前版本 {normalizedCurrent}。", page,
            LatestVersion: normalizedLatest, Asset: asset);
    }
    private static UpdateResult Unavailable() => new(false, "网络暂时不可用，正在后台重试更新检查。",
        CheckSucceeded: false, RetryAt: DateTimeOffset.UtcNow.AddMinutes(1));
}
