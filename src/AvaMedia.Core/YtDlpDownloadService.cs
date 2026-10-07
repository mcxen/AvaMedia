using System.Text.Json;

namespace AvaMedia.Core;

public sealed class YtDlpDownloadService : IVideoDownloadProvider
{
    private readonly AppSettings _settings;
    private readonly string? _extractorNames;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Action<string>?, Task<ProcessResult>> _run;
    public string Name => "yt-dlp";
    public bool CanHandle(Uri url) => url.Scheme is "http" or "https";
    public YtDlpDownloadService(AppSettings settings,
        Func<string, IReadOnlyList<string>, CancellationToken, Action<string>?, Task<ProcessResult>>? runner = null,
        string? extractorNames = null)
    {
        _settings = settings.Clone();
        _extractorNames = extractorNames;
        _run = runner ?? ((exe, args, ct, callback) => ProcessRunner.Run(exe, args, ct, callback, 4_000_000));
    }

    public async Task<DownloadInspection> InspectAsync(string url, DownloadOptions options, CancellationToken ct = default)
    {
        url = DownloadLinks.Normalize(url);options.Validate();
        using var cookies = await CookieLease.CreateAsync(options, url, ct);
        var args = CommonArguments(options, cookies.Path);
        args.AddRange(["--skip-download", "--dump-single-json", "--no-warnings", "--playlist-end", "100"]);
        if (options.ExpandPlaylist) args.AddRange(["--yes-playlist", "--flat-playlist", "--playlist-end", "100"]);
        else args.Add("--no-playlist");
        args.AddRange(["--", url]);
        var result = await _run(MediaEngine.Resolve(_settings.YtDlpPath, "yt-dlp"), args, ct, null);
        ct.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new InvalidOperationException(DownloadDiagnostics.Explain(result.Error, url));
        try
        {
            if(!options.ExpandPlaylist){using var json=JsonDocument.Parse(result.Output);if(json.RootElement.TryGetProperty("entries",out var entries) && entries.ValueKind==JsonValueKind.Array)throw new InvalidDataException("这是播放列表、分P、相册或文件列表，请勾选“展开播放列表 / 分P / 相册”后重新解析。");}
            return ParseInspection(result.Output, url);
        }
        catch (JsonException) { throw new InvalidDataException("下载引擎未返回有效视频信息。请检查 yt-dlp 版本或重新解析。"); }
    }

    public static DownloadInspection ParseInspection(string json, string sourceUrl)
    {
        using var document = JsonDocument.Parse(json);var root = document.RootElement;
        var videos = new List<DownloadVideo>();var seen = new HashSet<string>(StringComparer.Ordinal);
        var playlist = root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array;
        var items = playlist ? entries.EnumerateArray().ToArray() : [root];
        foreach (var item in items.Take(100))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var url = Text(item, "webpage_url");
            if (url.Length == 0) url = Text(item, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                var id = Text(item, "id");
                if (playlist && (DownloadLinks.Platform(sourceUrl) == "YouTube" || Text(root, "extractor_key").StartsWith("Youtube", StringComparison.OrdinalIgnoreCase)) && id.Length > 0)
                    url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
                else if (!playlist) url = sourceUrl;
                else continue;
            }
            url = DownloadLinks.Normalize(url);
            // A resolved short URL may require the signed query from the original share URL.
            if (!playlist && DownloadLinks.Platform(sourceUrl) == "小红书" && new Uri(sourceUrl).Query.Length > 0) url = sourceUrl;
            if (!seen.Add(url)) continue;
            var title = Text(item, "title");if (title.Length == 0) title = "视频 " + Text(item, "id");
            var duration = item.TryGetProperty("duration", out var length) && length.ValueKind==JsonValueKind.Number && length.TryGetDouble(out var value) && double.IsFinite(value) ? Math.Max(0, value) : 0;
            videos.Add(new(url, Text(item, "id"), title, Text(item, "uploader"), duration, DownloadLinks.Platform(sourceUrl),
                item.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True));
        }
        if (videos.Count == 0) throw new InvalidDataException("没有解析出可下载视频。请使用视频链接；图文笔记和空播放列表不在此流程中。");
        var count = root.TryGetProperty("playlist_count", out var total) && total.ValueKind==JsonValueKind.Number && total.TryGetInt32(out var n) ? n : items.Length;
        return new(videos, count > 100 || items.Length > 100);
        static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
    }

    public Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
        => ExecuteResolvedAsync(job, job.Inputs.Single(), job.Options.Download ?? new(), progress, ct);

    internal async Task ExecuteResolvedAsync(Job job, string resolvedUrl, DownloadOptions options, Action<double> progress, CancellationToken ct)
    {
        options.Validate();
        var url = DownloadLinks.Normalize(resolvedUrl);
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(job.Output))!;
        Directory.CreateDirectory(folder);
        // Resume the same request; an edited URL or download option must not reuse its old media.
        var request = JsonSerializer.Serialize(new { Url = DownloadLinks.Normalize(job.Inputs.Single()), Format = job.Options.Format, Options = job.Options.Download ?? new() });
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request)))[..16];
        var staging = System.IO.Path.Combine(folder, ".avamedia-download-" + job.Id.ToString("N") + "-" + revision);
        Directory.CreateDirectory(staging);
        using var cookies = await CookieLease.CreateAsync(options, url, ct);
        var args = DownloadArguments(options, job.Options.Format, System.IO.Path.Combine(staging, "media.%(ext)s"), cookies.Path).ToList();
        args.AddRange(["--ffmpeg-location", System.IO.Path.GetDirectoryName(MediaEngine.Resolve(_settings.FFmpegPath, "ffmpeg"))!, "--", url]);
        job.ProgressDetail = "连接视频服务";
        var result = await _run(MediaEngine.Resolve(_settings.YtDlpPath, "yt-dlp"), args, ct, line =>
        {
            if (DownloadDiagnostics.Progress(line) is { } update)
            { job.ProgressDetail = update.Detail;progress(update.Percent); }
            else if (line.StartsWith("[Merger]", StringComparison.Ordinal) || line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal) || line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
                job.ProgressDetail = "合并或整理媒体";
        });
        ct.ThrowIfCancellationRequested();job.Log = DownloadDiagnostics.Redact(result.Output + "\n" + result.Error);
        if (result.ExitCode != 0) throw new InvalidOperationException(DownloadDiagnostics.Explain(result.Error, url));
        var media = System.IO.Path.Combine(staging, "media." + job.Options.Format);
        if (!File.Exists(media) || new FileInfo(media).Length == 0)
            throw new InvalidDataException("没有生成所选格式的媒体文件，可能是视频不可用、格式无法封装或正在直播。请查看日志。");
        File.Move(media, job.Output, overwrite: false);
        foreach (var subtitle in Directory.EnumerateFiles(staging, "media.*.srt"))
        {
            var suffix = System.IO.Path.GetFileName(subtitle)["media".Length..];
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(job.Output) + suffix);
            // A sidecar conflict must never overwrite a pre-existing user subtitle.
            if (File.Exists(target)) target = MediaEngine.UniqueOutput(folder, System.IO.Path.GetFileNameWithoutExtension(target), "srt");
            File.Move(subtitle, target, overwrite: false);
        }
        // The exact directory is generated from the output folder and this job's GUID.
        Directory.Delete(staging, recursive: true);job.ProgressDetail = "";progress(100);
    }

    private List<string> CommonArguments(DownloadOptions options, string cookiePath)
    {
        var args = new List<string> { "--ignore-config", "--no-color", "--encoding", "utf-8", "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3" };
        if (_extractorNames is not null)
        {
            var plugins = System.IO.Path.Combine(AppContext.BaseDirectory, "download-plugins");
            if (!File.Exists(System.IO.Path.Combine(plugins, "avamedia", "yt_dlp_plugins", "extractor", "avamedia_hosts.py")))
                throw new FileNotFoundException("缺少随应用提供的站点解析器，请重新安装应用。");
            args.AddRange(["--no-plugin-dirs", "--plugin-dirs", plugins, "--use-extractors", _extractorNames]);
        }
        if (cookiePath.Length > 0) args.AddRange(["--cookies", cookiePath]);
        else if (options.CookieBrowser.Length > 0) args.AddRange(["--cookies-from-browser", options.CookieBrowser]);
        if (options.Proxy.Length > 0) args.AddRange(["--proxy", options.Proxy]);
        if (options.Browser is {} browser)
        {
            if (browser.Referer.Length > 0) args.AddRange(["--referer", browser.Referer]);
            if (browser.UserAgent.Length > 0) args.AddRange(["--user-agent", browser.UserAgent]);
            if (_extractorNames == "avamedia:direct") args.AddRange(["--extractor-args", "AvaMediaDirect:ext=" + browser.Extension]);
        }
        // Clear yt-dlp's default Deno runtime so an installed Deno cannot take precedence.
        args.Add("--no-js-runtimes");
        try { args.AddRange(["--js-runtimes", "quickjs:" + MediaEngine.Resolve("", "qjs")]); }
        catch (FileNotFoundException) { /* yt-dlp can still handle sites not requiring JavaScript. */ }
        return args;
    }

    public IReadOnlyList<string> DownloadArguments(DownloadOptions options, string format, string template, string cookiePath = "")
    {
        options.Validate();if (format is not ("mp4" or "mkv" or "mp3" or "m4a")) throw new ArgumentException("下载格式无效。");
        var args = CommonArguments(options, cookiePath);
        args.AddRange(["--no-playlist", "--no-overwrites", "--continue", "--newline", "--no-mtime", "--concurrent-fragments", "4",
            "--match-filter", "!is_live", "--progress-template", "download:AVAMEDIA_PROGRESS:%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--output", template]);
        if (format is "mp3" or "m4a") args.AddRange(["--format", "ba/b", "--extract-audio", "--audio-format", format, "--audio-quality", "0"]);
        else
        {
            var limit = _extractorNames != "avamedia:direct" && options.MaxHeight > 0 ? "[height<=?" + options.MaxHeight + "]" : "";
            var selector = format == "mp4" ? $"bv*[ext=mp4]{limit}+ba[ext=m4a]/b[ext=mp4]{limit}/bv*{limit}+ba/b{limit}" : $"bv*{limit}+ba/b{limit}";
            args.AddRange(["--format", selector, "--merge-output-format", format, "--remux-video", format]);
        }
        if (options.Metadata) args.Add("--embed-metadata");
        if (options.Subtitles)
        {
            args.AddRange(["--write-subs", "--sub-langs", "zh.*,en.*", "--convert-subs", "srt"]);
            if (options.AutoSubtitles) args.Add("--write-auto-subs");
        }
        return args;
    }

    private sealed class CookieLease(string path, string? directory) : IDisposable
    {
        public string Path { get; } = path;
        public static async Task<CookieLease> CreateAsync(DownloadOptions options, string url, CancellationToken ct)
        {
            if (options.CookieFile.Length == 0 && !options.UseBrowserCookies) return new("", null);
            if (options.UseBrowserCookies && options.Browser is null) throw new InvalidOperationException("请先从浏览器识别视频，再使用 CDP 登录态。");
            var browserCookies = options.CookieFile.Length == 0
                ? await BrowserVideoCapture.ReadCookiesAsync(options.Browser!, url, ct) : null;
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-cookies-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = System.IO.Path.Combine(directory, "cookies.txt");
            try
            {
                if (browserCookies is not null) await File.WriteAllTextAsync(path, browserCookies, ct);
                else File.Copy(options.CookieFile, path);
                return new(path, directory);
            }
            catch { Directory.Delete(directory, recursive: true);throw; }
        }
        public void Dispose() { if (directory is not null) Directory.Delete(directory, recursive: true); }
    }
}
