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
