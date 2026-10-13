using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace AvaMedia.Browser;

internal static class RemotePreview
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(35) };

    public static Task<(byte[] Image, JsonNode Metadata)> Read(string path, double seconds, int width, int videoStreamIndex, int audioStreamIndex, CancellationToken ct)
    {
        var url = BrowserHost.Origin() + "/web/preview/frame?path=" + Uri.EscapeDataString(path)
            + "&seconds=" + seconds.ToString("R", CultureInfo.InvariantCulture) + "&width=" + width
            + "&videoStreamIndex=" + videoStreamIndex + "&audioStreamIndex=" + audioStreamIndex;
        return Send(new(HttpMethod.Get, url), ct);
    }
    public static Task<(byte[] Image, JsonNode Metadata)> ContactSheet(string path, JsonNode options, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BrowserHost.Origin() + "/web/preview/contact-sheet")
        { Content = new StringContent(new JsonObject { ["path"] = path, ["options"] = options }.ToJsonString(), Encoding.UTF8, "application/json") };
        return Send(request, ct);
    }
    /// <summary>N evenly spaced thumbnails for the filmstrip.</summary>
    public static Task<JsonNode> Strip(string path, int count, int videoStreamIndex, CancellationToken ct)
        => Json(BrowserHost.Origin() + "/web/preview/strip?path=" + Uri.EscapeDataString(path) + "&count=" + count + "&videoStreamIndex=" + videoStreamIndex, ct);
    /// <summary>Start frame and last frame before the end of a segment.</summary>
    public static Task<JsonNode> Boundaries(string path, double start, double end, int videoStreamIndex, CancellationToken ct)
        => Json(BrowserHost.Origin() + "/web/preview/boundaries?path=" + Uri.EscapeDataString(path) + "&start=" + start.ToString("R", CultureInfo.InvariantCulture)
            + "&end=" + end.ToString("R", CultureInfo.InvariantCulture) + "&width=320&videoStreamIndex=" + videoStreamIndex, ct);
    /// <summary>Moves a time by whole frames using real frame timestamps.</summary>
    public static async Task<double> Nudge(string path, double seconds, int frames, int videoStreamIndex, CancellationToken ct)
        => (await Json(BrowserHost.Origin() + "/web/preview/nudge?path=" + Uri.EscapeDataString(path) + "&seconds=" + seconds.ToString("R", CultureInfo.InvariantCulture)
            + "&frames=" + frames + "&videoStreamIndex=" + videoStreamIndex, ct))["seconds"]!.GetValue<double>();
    private static async Task<JsonNode> Json(string url, CancellationToken ct)
    {
        using var response = await Client.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("预览服务繁忙，请稍后重试。");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body.Text("detail") is { Length: > 0 } detail ? detail : "预览加载失败，请重试。");
        return body ?? throw new InvalidDataException("预览服务没有返回内容。");
    }
    public static Task<(byte[] Image, JsonNode Metadata)> Subtitle(JsonObject body, CancellationToken ct) =>
        Send(new(HttpMethod.Post, BrowserHost.Origin() + "/web/preview/subtitle")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }, ct);

    private static async Task<(byte[] Image, JsonNode Metadata)> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using var ownedRequest = request;
        // Browser HttpClient forwards cancellation to fetch/AbortController. Never carry image bytes in MCP JSON.
        using var response = await Client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("预览服务繁忙，请稍后再读取预览帧。");
        if (!response.IsSuccessStatusCode)
        {
            var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            throw new InvalidOperationException(problem.Text("detail") is { Length: > 0 } detail ? detail : "预览加载失败，请重试。");
        }
        var metadata = JsonNode.Parse(response.Headers.GetValues("X-AvaMedia-Preview").Single())
            ?? throw new InvalidDataException("预览缺少媒体信息。");
        return (await response.Content.ReadAsByteArrayAsync(ct), metadata);
    }
}
