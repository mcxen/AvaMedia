using System.Net;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record UpdateResult(bool HasUpdate, string Message, Uri? ReleasePage = null);

public sealed class ReleaseUpdateClient(HttpClient client)
{
    public static readonly Uri Endpoint = new("https://api.github.com/repos/mcxen/AvaMedia/releases/latest");
    public async Task<UpdateResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("AvaMedia/" + current.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "尚无正式发布版本。");
        response.EnsureSuccessStatusCode();
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
}
