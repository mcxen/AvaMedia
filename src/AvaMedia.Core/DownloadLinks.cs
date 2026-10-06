using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public static class DownloadLinks
{
    public static IReadOnlyList<string> Extract(string? text)
    {
        // Share messages often contain prose around the URL. Keep signed query strings intact.
        var matches = Regex.Matches(text ?? "", "https?://[^\\s<>\"'，。；）】]+", RegexOptions.IgnoreCase)
            .Select(m => m.Value.TrimEnd(')', ']', '}', ',', '.', ';', '!', '。'));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var match in matches) result.Add(Normalize(match));
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (line.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) result.Add(Normalize("https://" + line));
        }
        if (result.Count > 100) throw new ArgumentException("一次最多解析 100 个链接，请分批添加。");
        return result.ToArray();
    }

    public static string Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入有效 HTTP / HTTPS 视频链接，链接中不能包含账号密码。");
        // File-host fragments select a list member or carry a Bunkr numeric file ID.
        var fragment = Platform(url) is "Bunkr" or "Pixeldrain" ? uri.Fragment : "";
        return uri.GetLeftPart(UriPartial.Path) + uri.Query + fragment;
    }

    public static string Platform(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "其他网站";
        bool Host(string host) => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
        if (Host("youtube.com") || Host("youtu.be")) return "YouTube";
        if (Host("bilibili.com") || Host("b23.tv")) return "哔哩哔哩";
        if (Host("douyin.com") || Host("iesdouyin.com")) return "抖音";
        if (Host("xiaohongshu.com") || Host("xhslink.com")) return "小红书";
        if (Regex.IsMatch(uri.Host, @"^(?:www\.)?bunkr\.[a-z0-9-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Bunkr";
        if (uri.Host.Equals("pixeldrain.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.pixeldrain.com", StringComparison.OrdinalIgnoreCase)) return "Pixeldrain";
        return "其他网站";
    }

    public static string Display(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        return uri.Host + uri.AbsolutePath + (uri.Query.Length > 0 ? "?…" : "");
    }
}
