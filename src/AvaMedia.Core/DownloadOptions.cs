namespace AvaMedia.Core;

public sealed record DownloadOptions
{
    public int MaxHeight { get; init; } = 1080;
    public bool ExpandPlaylist { get; init; }
    public string CookieBrowser { get; init; } = "";
    public string CookieFile { get; init; } = "";
    public string Proxy { get; init; } = "";
    public bool Subtitles { get; init; }
    public bool AutoSubtitles { get; init; }
    public bool Metadata { get; init; } = true;
    public BrowserMediaContext? Browser { get; init; }
    public WebViewMediaContext? WebView { get; init; }
    public bool UseBrowserCookies { get; init; }
    public bool UseWebViewCookies { get; init; }
    public string CdpEndpoint { get; init; } = BrowserVideoCapture.DefaultEndpoint;

    public void Validate()
    {
        Browser?.Validate();
        WebView?.Validate();
        if (Browser is not null && WebView is not null) throw new ArgumentException("请选择一个浏览器媒体来源。");
        if (UseWebViewCookies && (UseBrowserCookies || CookieBrowser.Length > 0 || CookieFile.Length > 0)) throw new ArgumentException("内嵌浏览器登录态和其他登录态请选择一种。");
        if (UseBrowserCookies && (CookieBrowser.Length > 0 || CookieFile.Length > 0)) throw new ArgumentException("浏览器登录态和 cookies.txt 请选择一种。");
        if (MaxHeight is < 0 or > 4320) throw new ArgumentException("清晰度须在 0–4320p 之间，0 表示最佳画质。");
        if (!new[] { "", "firefox", "chrome", "edge", "safari", "brave" }.Contains(CookieBrowser))
            throw new ArgumentException("请选择支持的浏览器登录态。");
        if (CookieBrowser.Length > 0 && CookieFile.Length > 0) throw new ArgumentException("浏览器登录态和 cookies.txt 请选择一种。");
        if (CookieFile.Length > 0 && !File.Exists(CookieFile)) throw new ArgumentException("cookies.txt 文件不存在。");
        if (Proxy.Length > 0 && (!Uri.TryCreate(Proxy, UriKind.Absolute, out var proxy) ||
            proxy.Scheme is not ("http" or "https" or "socks5" or "socks5h") || string.IsNullOrEmpty(proxy.Host)))
            throw new ArgumentException("代理请输入 HTTP、HTTPS 或 SOCKS5 地址，例如 http://127.0.0.1:7890。");
    }
}

public sealed record DownloadVideo(string Url, string Id, string Title, string Uploader,
    double Duration, string Platform, bool IsLive = false, BrowserMediaContext? Browser = null, string SourceUrl = "", WebViewMediaContext? WebView = null, string ThumbnailUrl = "");
public sealed record DownloadInspection(IReadOnlyList<DownloadVideo> Videos, bool Truncated = false);
public sealed record VideoDownloadRequest(IReadOnlyList<DownloadVideo> Videos, string Folder,
    string Format, DownloadOptions Options, string OutputName = "");

public interface IVideoDownloadService
{
    Task<DownloadInspection> InspectAsync(string url, DownloadOptions options, CancellationToken ct = default);
}

public static class DownloadBatch
{
    public static void ValidateOutputName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("请输入有效文件名，不包含路径或特殊字符。");
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("此文件名为系统保留名称，请更换。");
    }

    public static IReadOnlyList<Job> CreateJobs(VideoDownloadRequest request, IEnumerable<string>? reserved = null)
    {
        request.Options.Validate();
        if (request.Format is not ("mp4" or "mkv" or "mp3" or "m4a")) throw new ArgumentException("请选择 MP4、MKV、MP3 或 M4A。");
        if (request.Videos.Count is < 1 or > 100) throw new ArgumentException("请选择 1–100 个视频。");
        if (request.OutputName.Length > 0)
        {
            if (request.Videos.Count != 1) throw new ArgumentException("编辑任务时请选择一个视频。");
            ValidateOutputName(request.OutputName);
        }
        if (string.IsNullOrWhiteSpace(request.Folder)) throw new ArgumentException("请选择保存位置。");
        foreach (var video in request.Videos)
        {
            _ = DownloadLinks.Normalize(video.Url);
            if (video.SourceUrl.Length > 0)
            {
                _ = DownloadLinks.Normalize(video.SourceUrl);
                if (video.WebView is null && !DownloadLinks.IsFileditchPage(video.SourceUrl)) throw new ArgumentException("视频来源页无效，请重新解析。");
            }
            if (video.IsLive) throw new ArgumentException("当前下载流程不支持正在进行的直播。");
        }
        var used = new HashSet<string>(reserved ?? [], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var jobs = new List<Job>();
        foreach (var video in request.Videos)
        {
            var title = string.IsNullOrWhiteSpace(video.Title) ? "Video" : video.Title;
            // Keep filenames short and portable, including emoji and Windows device names.
            var elements = System.Globalization.StringInfo.ParseCombiningCharacters(title);
            if (elements.Length > 70) title = title[..elements[70]];
            title = string.Concat(title.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(title)) title = "Video";
            if (System.Text.RegularExpressions.Regex.IsMatch(title, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) title = "_" + title;
            var job = new Job { FeatureId = "download", Inputs = [video.WebView is {} web ? DownloadLinks.NormalizePageUrl(web.PageUrl) : video.SourceUrl.Length > 0 ? DownloadLinks.Normalize(video.SourceUrl) : video.Url], DownloadTitle = video.Title,
                Duration = double.IsFinite(video.Duration)?Math.Max(0,video.Duration):0,
                Options = new() { Format = request.Format, Download = request.Options with { ExpandPlaylist = false, Browser = video.Browser, WebView = video.WebView, UseWebViewCookies = request.Options.UseWebViewCookies && video.WebView is not null } },
                Output = MediaEngine.UniqueOutput(request.Folder, request.OutputName.Length > 0 ? request.OutputName : title, request.Format, used) };
            MediaEngine.Validate(job);used.Add(job.Output);jobs.Add(job);
        }
        return jobs;
    }
}
