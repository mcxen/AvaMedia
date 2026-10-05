using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public static class DownloadDiagnostics
{
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)(Cookie|Authorization|Set-Cookie)\s*[:=][^\r\n]+", "$1: [已隐藏]");
        return Regex.Replace(text, "(?i)(?:https?|socks5h?)://[^\\s<>\"']+", m =>
        {
            if (!Uri.TryCreate(m.Value, UriKind.Absolute, out var uri)) return "[链接已隐藏]";
            return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath + (uri.Query.Length > 0 ? "?…" : "");
        });
    }

    public static string Explain(string error, string url)
    {
        var lower = error.ToLowerInvariant();
        var hint = lower.Contains("dpapi") || lower.Contains("decrypt") || lower.Contains("cookie database") || lower.Contains("could not copy")
            ? "无法读取浏览器登录态。可尝试关闭浏览器，改用 Firefox，或选择 Netscape 格式的 cookies.txt 后重新解析。"
            : lower.Contains("unsupported url") ? "当前 yt-dlp 未识别此链接。请使用视频分享链接；小红书应保留包含 xsec_token 的完整链接。"
            : lower.Contains("no video formats") && DownloadLinks.Platform(url)=="小红书" ? "未找到可下载视频。请复制含 xsec_token 的完整视频笔记分享链接，并尝试浏览器登录态；图文笔记不在此流程中。"
            : lower.Contains("sign in") || lower.Contains("login") || lower.Contains("cookies") || lower.Contains("403") || lower.Contains("verify")
                ? "网站要求登录或验证。先在浏览器中确认该视频可播放，再选择该浏览器登录态或 cookies.txt 重试。"
            : lower.Contains("timed out") || lower.Contains("unable to download") || lower.Contains("connection") || lower.Contains("resolve")
                ? "网络请求失败。请检查网络或代理；YouTube 需要本机能够访问其视频服务。"
            : lower.Contains("requested format") ? "所选清晰度不可用，可改为“最佳”或较低清晰度后重试。"
            : "解析或下载失败，可更换登录态、检查链接，或查看任务日志后重试。";
        return DownloadLinks.Platform(url) + " · " + hint + "\n" + Redact(error).Trim()[..Math.Min(Redact(error).Trim().Length, 1800)];
    }

    public static (double Percent, string Detail)? Progress(string line)
    {
        const string marker = "AVAMEDIA_PROGRESS:";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var parts = line[(index + marker.Length)..].Split('|');
        var match = Regex.Match(parts[0], @"\d+(?:\.\d+)?");
        if (!match.Success || !double.TryParse(match.Value, System.Globalization.CultureInfo.InvariantCulture, out var percent)) return null;
        var speed = parts.ElementAtOrDefault(1)?.Trim() ?? "";var eta = parts.ElementAtOrDefault(2)?.Trim() ?? "";
        var detail = (speed is "" or "NA" or "Unknown" ? "" : speed) + (eta is "" or "NA" or "Unknown" ? "" : " · 剩余 " + eta);
        return (Math.Clamp(percent, 0, 99), detail.Trim(' ', '·'));
    }
}
