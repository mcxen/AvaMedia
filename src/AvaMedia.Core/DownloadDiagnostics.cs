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
        var platform = DownloadLinks.Platform(url);
        var fileHost = platform is "Bunkr" or "Pixeldrain";
        var hint = platform == "Bunkr" && lower.Contains("impersonat")
            ? "当前下载引擎缺少 Bunkr 所需的浏览器网络支持，请使用应用内置 yt-dlp。"
            : platform == "视频直链" && lower.Contains("impersonat")
            ? "此视频服务器需要浏览器网络支持，请使用应用内置 yt-dlp，或检查自定义引擎的 curl_cffi 依赖。"
            : platform == "Pixeldrain" && (lower.Contains("captcha") || lower.Contains("hotlink_detected"))
            ? "Pixeldrain 要求网页验证或账号权限，请先在浏览器打开此文件完成验证，再使用登录态重试。"
            : platform == "Pixeldrain" && (lower.Contains("limit_exceeded") || lower.Contains("max_concurrent_downloads") || lower.Contains("ip_rate_limit_reached"))
            ? "Pixeldrain 的传输、下载次数或并发限额已达到，请等待限制解除或检查账号额度。"
            : lower.Contains("not a video") ? "此文件不是受支持的视频，请选择视频文件；图片、音频和压缩包不在此流程中。"
            : lower.Contains("empty video collection") ? "相册或文件列表中没有找到可下载视频。"
            : lower.Contains("invalid list item") ? "文件列表的 #item 序号无效，请复制有效的单项链接或展开整个文件列表。"
            : fileHost && (lower.Contains("404") || lower.Contains("not_found") || lower.Contains("unavailable_for_legal_reasons"))
            ? "文件或列表已失效、被移除或不可访问，请检查原始链接。"
            : lower.Contains("dpapi") || lower.Contains("decrypt") || lower.Contains("cookie database") || lower.Contains("could not copy")
            ? "无法读取浏览器登录态。可尝试关闭浏览器，改用 Firefox，或选择 Netscape 格式的 cookies.txt 后重新解析。"
            : lower.Contains("unsupported url") && fileHost
            ? "请使用 Bunkr 的视频页或相册链接，或 Pixeldrain 的 /u/ 单文件、/l/ 文件列表链接。"
            : lower.Contains("unsupported url") ? "当前 yt-dlp 未识别此链接。请使用视频分享链接；小红书应保留包含 xsec_token 的完整链接。"
            : lower.Contains("no video formats") && DownloadLinks.Platform(url)=="小红书" ? "未找到可下载视频。请复制含 xsec_token 的完整视频笔记分享链接，并尝试浏览器登录态；图文笔记不在此流程中。"
            : lower.Contains("sign in") || lower.Contains("login") || lower.Contains("cookies") || lower.Contains("403") || lower.Contains("verify")
                ? "网站要求登录或验证。先在浏览器中确认该视频可播放，再选择该浏览器登录态或 cookies.txt 重试。"
            : Regex.IsMatch(lower, @"\b(?:500|502|503|504|520|521|522|523|524)\b")
                ? "视频服务器或 CDN 暂时不可用。可稍后重试；若浏览器能播放，请使用浏览器识别以保留来源页与请求环境。"
            : lower.Contains("timed out") || lower.Contains("unable to download") || lower.Contains("connection") || lower.Contains("resolve")
                ? "网络请求失败。请检查网络或代理；YouTube 需要本机能够访问其视频服务。"
            : lower.Contains("requested format") ? "所选清晰度不可用，可改为“最佳”或较低清晰度后重试。"
            : "解析或下载失败，可更换登录态、检查链接，或查看任务日志后重试。";
        var detail = Redact(error).Trim();
        return platform + " · " + hint + "\n" + detail[..Math.Min(detail.Length, 1800)];
    }

    public static (double? Percent, string Detail, double BytesPerSecond, bool IsDownloading)? Progress(string line)
    {
        const string marker = "AVAMEDIA_PROGRESS:";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var parts = line[(index + marker.Length)..].Split('|');
        var match = Regex.Match(parts[0], @"\d+(?:\.\d+)?");
        double? percent = match.Success && double.TryParse(match.Value, System.Globalization.CultureInfo.InvariantCulture, out var rawPercent)
            && double.IsFinite(rawPercent) ? Math.Clamp(rawPercent, 0, 99) : null;
        var speed = parts.ElementAtOrDefault(1)?.Trim() ?? "";var eta = parts.ElementAtOrDefault(2)?.Trim() ?? "";
        var detail = (speed is "" or "NA" or "Unknown" ? "" : speed) + (eta is "" or "NA" or "Unknown" ? "" : " · 剩余 " + eta);
        var downloading = parts.ElementAtOrDefault(4)?.Trim() == "downloading";
        var bytesPerSecond = double.TryParse(parts.ElementAtOrDefault(3), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var rawSpeed) && double.IsFinite(rawSpeed) && rawSpeed > 0
            ? rawSpeed : 0;
        return (percent, detail.Trim(' ', '·'), downloading ? bytesPerSecond : 0, downloading);
    }
}
