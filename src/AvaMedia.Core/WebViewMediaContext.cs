using System.Net;
using System.Text;

namespace AvaMedia.Core;

// The queue stores media metadata and an opaque cookie-file ID, never cookie values.
public sealed record WebViewMediaContext(string MediaUrl, string PageUrl, string Referer, string UserAgent,
    string Origin, string Extension, string CookieSnapshotId = "")
{
    public void Validate()
    {
        _ = DownloadLinks.Normalize(MediaUrl);
        _ = DownloadLinks.Normalize(PageUrl);
        if (Referer.Length > 0) _ = DownloadLinks.Normalize(Referer);
        if (Origin.Length > 0 && (!Uri.TryCreate(Origin, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length > 0 || origin.AbsolutePath != "/" || origin.Query.Length > 0 || origin.Fragment.Length > 0))
            throw new ArgumentException("浏览器媒体来源无效，请重新嗅探。");
        if (!DownloadLinks.IsMediaExtension(Extension) || UserAgent.Length > 2000 ||
            new[] { Referer, UserAgent, Origin }.Any(value => value.Any(char.IsControl)))
            throw new ArgumentException("浏览器媒体信息无效，请重新嗅探。");
        if (CookieSnapshotId.Length > 0) _ = WebViewCookieStore.PathFor(CookieSnapshotId);
    }
}

public static class WebViewCookieStore
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "WebView", "Cookies");

    public static string PathFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("浏览器登录态无效，请重新嗅探。");
        return Path.Combine(Root, id + ".txt");
    }

    public static async Task<string> SaveAsync(IEnumerable<Cookie> cookies, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var id = Guid.NewGuid().ToString("N");
        var path = PathFor(id);
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var cookie in cookies)
        {
            if (cookie.Expired || new[] { cookie.Domain, cookie.Path, cookie.Name, cookie.Value }.Any(value => value.Any(c => c is '\r' or '\n' or '\t'))) continue;
            var expires = cookie.Expires == DateTime.MinValue ? 0 : Math.Max(0, new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds());
            text.Append(cookie.HttpOnly ? "#HttpOnly_" : "").Append(cookie.Domain).Append('\t')
                .Append(cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
                .Append(cookie.Path.Length > 0 ? cookie.Path : "/").Append('\t')
                .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t').Append(expires).Append('\t')
                .Append(cookie.Name).Append('\t').Append(cookie.Value).Append('\n');
        }
        // Create with private permissions before writing, including on a shared machine.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using var stream = new FileStream(path, options);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), ct);
        }
        catch { File.Delete(path); throw; }
        return id;
    }
}
