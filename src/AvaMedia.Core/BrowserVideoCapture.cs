using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record BrowserMediaContext(string Endpoint, string TargetId, string Referer, string UserAgent, string Extension)
{
    public void Validate()
    {
        _ = BrowserVideoCapture.EndpointUri(Endpoint);
        if (TargetId.Length is 0 or > 200 || !DownloadLinks.IsMediaExtension(Extension) ||
            UserAgent.Length > 2000 || UserAgent.Any(char.IsControl)) throw new ArgumentException("浏览器媒体信息无效，请重新识别。");
        if (Referer.Length > 0) _ = DownloadLinks.Normalize(Referer);
        if (Referer.Any(char.IsControl)) throw new ArgumentException("浏览器媒体信息无效，请重新识别。");
    }
}

public sealed class BrowserVideoCapture
{
    public const string DefaultEndpoint = "http://127.0.0.1:9222";
    private sealed record Target(string Id, string Url, string Socket);

    public static Uri EndpointUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("CDP 地址须为本机 HTTP 地址，例如 http://127.0.0.1:9222。");
        return uri;
    }

    private static async Task<Target[]> TargetsAsync(string endpoint, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(new Uri(EndpointUri(endpoint), "/json/list"), ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.EnumerateArray().Where(t => Text(t, "type") == "page")
                .Select(t => new Target(Text(t, "id"), Text(t, "url"), Text(t, "webSocketDebuggerUrl")))
                .Where(t => t.Id.Length > 0 && t.Socket.Length > 0 && Uri.TryCreate(t.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                .Take(30).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("无法连接浏览器 CDP。请用独立用户目录启动 Chrome / Edge，并启用 --remote-debugging-port=9222。", ex);
        }
    }

    public async Task<DownloadInspection> CaptureAsync(string endpoint, IReadOnlyList<string>? requestedUrls = null, CancellationToken ct = default)
    {
        endpoint = EndpointUri(endpoint).GetLeftPart(UriPartial.Authority);
        var targets = await TargetsAsync(endpoint, ct);
        var pages = requestedUrls?.Where(url => DownloadLinks.MediaExtension(url).Length == 0).ToArray() ?? [];
        var mediaUrls = requestedUrls?.Where(url => DownloadLinks.MediaExtension(url).Length > 0).ToArray() ?? [];
        if (pages.Length > 0) targets = targets.Where(t => pages.Any(url => SamePage(t.Url, url))).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("未找到可识别的浏览器页面，请在已连接的浏览器打开视频。");
        var videos = new Dictionary<string, DownloadVideo>(StringComparer.Ordinal);
        var failed = 0;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                using var cdp = await CdpConnection.ConnectAsync(endpoint, target.Socket, timeout.Token);
                var responses = new Dictionary<string, (string Extension, string Referer)>(StringComparer.Ordinal);
                var referrers = new Dictionary<string, string>(StringComparer.Ordinal);
                await cdp.CallAsync("Network.enable", new { }, timeout.Token);
                var result = await cdp.CallAsync("Runtime.evaluate", new
                {
                    expression = CaptureExpression, returnByValue = true, awaitPromise = true
                }, timeout.Token, message =>
                {
                    if (!message.TryGetProperty("params", out var p)) return;
                    var requestId = Text(p, "requestId");
                    if (Text(message, "method") == "Network.requestWillBeSent" && p.TryGetProperty("request", out var request) &&
                        request.TryGetProperty("headers", out var headers) && referrers.Count < 500)
                    {
                        referrers[requestId] = headers.EnumerateObject().Where(h => h.Name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                            .Select(h => h.Value.GetString() ?? "").FirstOrDefault() ?? "";
                        return;
                    }
                    if (Text(message, "method") != "Network.responseReceived" || !p.TryGetProperty("response", out var response)) return;
                    if (!response.TryGetProperty("status", out var status) || status.GetDouble() >= 400) return;
                    var url = Text(response, "url");var extension = DownloadLinks.MediaExtension(url, Text(response, "mimeType"));
                    if (extension.Length > 0 && extension is not ("ts" or "m2ts") && responses.Count < 100)
                        responses[url] = (extension, referrers.GetValueOrDefault(requestId) ?? "");
                });
                if (!result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var snapshot))
                    throw new InvalidDataException("未能读取浏览器媒体来源。");
                var page = Text(snapshot, "url");var agent = Text(snapshot, "userAgent");var title = Text(snapshot, "title");
                if (snapshot.TryGetProperty("media", out var media))
                    foreach (var item in media.EnumerateArray())
                        Add(Text(item, "url"), DownloadLinks.MediaExtension(Text(item, "url"), Text(item, "mime")),
                            item.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var seconds) ? seconds : 0,
                            title, Text(item, "referer"));
                foreach (var (url, response) in responses) Add(url, response.Extension, 0, "", response.Referer);

                void Add(string url, string extension, double duration, string mediaTitle, string referer)
                {
                    if (extension.Length == 0 || videos.Count >= 100 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0) return;
                    url = DownloadLinks.Normalize(url);
                    if (pages.Length == 0 && mediaUrls.Length > 0 && !mediaUrls.Any(media => SamePage(url, media))) return;
                    if (videos.ContainsKey(url)) return;
                    var context = new BrowserMediaContext(endpoint, target.Id, referer.Length > 0 ? referer : page, agent, extension);
                    context.Validate();
                    // A native browser media document's title is the filename. Preserve that filename without its extension.
                    if (SamePage(page, url)) mediaTitle = "";
                    videos.Add(url, DirectVideoDownloadProvider.Describe(url, mediaTitle, duration, context));
                }
                Add(page, DownloadLinks.MediaExtension(page), 0, "", Text(snapshot, "referrer"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or JsonException or OperationCanceledException or ArgumentException)
            { failed++; }
        }
        if (videos.Count == 0) throw new InvalidOperationException(failed == targets.Length
            ? "无法读取浏览器页面，请检查 CDP 连接后重试。"
            : "未发现视频来源。请先在浏览器播放视频，再重新识别；加密媒体不支持下载。");
        return new(videos.Values.ToArray(), videos.Count >= 100);
    }

    public static async Task<string> ReadCookiesAsync(BrowserMediaContext browser, string mediaUrl, CancellationToken ct)
    {
        try { return await ReadCookiesCoreAsync(browser, mediaUrl, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("读取 CDP 登录态超时，请检查浏览器连接后重试。"); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        { throw new InvalidOperationException("无法读取 CDP 登录态，请检查浏览器连接后重试。", ex); }
    }

    private static async Task<string> ReadCookiesCoreAsync(BrowserMediaContext browser, string mediaUrl, CancellationToken ct)
    {
        browser.Validate();mediaUrl = DownloadLinks.Normalize(mediaUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var targets = await TargetsAsync(browser.Endpoint, timeout.Token);
        var target = targets.FirstOrDefault(t => t.Id == browser.TargetId)
            ?? throw new InvalidOperationException("原浏览器页面已关闭，请重新识别视频或选择其他登录态。");
        using var cdp = await CdpConnection.ConnectAsync(browser.Endpoint, target.Socket, timeout.Token);
        var result = await cdp.CallAsync("Network.getCookies", new { urls = new[] { mediaUrl } }, timeout.Token);
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var cookie in result.GetProperty("cookies").EnumerateArray())
        {
            var domain = Text(cookie, "domain");var path = Text(cookie, "path");var name = Text(cookie, "name");var value = Text(cookie, "value");
            if (new[] { domain, path, name, value }.Any(v => v.Any(c => c is '\r' or '\n' or '\t'))) continue;
            // Netscape files cannot represent partitioned cookies; do not widen their scope.
            if (cookie.TryGetProperty("partitionKey", out _)) continue;
            var expires = cookie.TryGetProperty("expires", out var expiry) && expiry.TryGetDouble(out var seconds) && double.IsFinite(seconds)
                ? Math.Max(0, (long)seconds) : 0;
            text.Append(Bool(cookie, "httpOnly") ? "#HttpOnly_" : "").Append(domain).Append('\t')
                .Append(domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t').Append(path).Append('\t')
                .Append(Bool(cookie, "secure") ? "TRUE" : "FALSE").Append('\t').Append(expires).Append('\t')
                .Append(name).Append('\t').Append(value).Append('\n');
        }
        return text.ToString();
    }

    private static bool SamePage(string left, string right) => DownloadLinks.Normalize(left) == DownloadLinks.Normalize(right);
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private const string CaptureExpression = """
        new Promise(resolve => setTimeout(() => {
            const media = [];
            const collect = doc => {
                for (const video of doc.querySelectorAll('video')) {
                    if (video.mediaKeys) continue;
                    const sources = Array.from(video.querySelectorAll('source'));
                    for (const url of [video.currentSrc, video.src, ...sources.map(s => s.src)])
                        if (/^https?:/i.test(url)) media.push({url, mime: sources.find(s => s.src === url)?.type || '', duration: Number.isFinite(video.duration) ? video.duration : 0, referer: /^video\//i.test(doc.contentType) ? (doc.referrer || doc.URL) : doc.URL});
                }
                for (const frame of doc.querySelectorAll('iframe')) { try { if (frame.contentDocument) collect(frame.contentDocument); } catch {} }
            };
            collect(document);
            for (const entry of performance.getEntriesByType('resource').slice(-1000))
                if (/\.(m3u8|mpd)(?:[?#]|$)/i.test(entry.name) || entry.initiatorType === 'video' && /\.(mp4|mkv|webm|mov|m4v)(?:[?#]|$)/i.test(entry.name)) media.push({url: entry.name, referer: location.href});
            resolve({url: location.href, referrer: document.referrer, title: document.title, userAgent: navigator.userAgent, media: media.slice(0, 200)});
        }, 1000))
        """;

    private sealed class CdpConnection : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _id;
        public static async Task<CdpConnection> ConnectAsync(string endpoint, string socket, CancellationToken ct)
        {
            var origin = EndpointUri(endpoint);
            if (!Uri.TryCreate(socket, UriKind.Absolute, out var uri) || uri.Scheme != "ws" || !uri.IsLoopback ||
                uri.Port != origin.Port || uri.UserInfo.Length > 0)
                throw new InvalidDataException("浏览器返回了无效 CDP 连接。");
            var connection = new CdpConnection();
            connection._socket.Options.Proxy = null;
            try { await connection._socket.ConnectAsync(uri, ct);return connection; }
            catch { connection.Dispose();throw; }
        }
        public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken ct, Action<JsonElement>? onEvent = null)
        {
            var id = ++_id;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[16384];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (received.MessageType != WebSocketMessageType.Text) throw new IOException("浏览器 CDP 连接已关闭。");
                    message.Write(buffer, 0, received.Count);
                    if (message.Length > 4_000_000) throw new InvalidDataException("浏览器 CDP 响应过大。");
                } while (!received.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());var root = json.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) { onEvent?.Invoke(root);continue; }
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("浏览器未能执行媒体识别请求。");
                return root.GetProperty("result").Clone();
            }
        }
        public void Dispose() => _socket.Dispose();
    }
}
