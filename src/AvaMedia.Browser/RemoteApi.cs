using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Nodes;

namespace AvaMedia.Browser;

internal static partial class BrowserHost
{
    [JSImport("call", "console")] internal static partial Task<string> Call(string name, string args);
    [JSImport("readPending", "console")] internal static partial string ReadPending();
    [JSImport("writePending", "console")] internal static partial void WritePending(string value);
    [JSImport("open", "console")] internal static partial void Open(string url);
}

internal static class RemoteApi
{
    public static async Task<JsonNode> Call(string name, JsonObject? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await BrowserHost.Call("avamedia_" + name, (args ?? new()).ToJsonString());
        ct.ThrowIfCancellationRequested();
        return JsonNode.Parse(result) ?? throw new InvalidOperationException("服务器没有返回结果。");
    }
    public static string Text(this JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    public static bool Flag(this JsonNode? node, string key) => node?[key]?.GetValue<bool>() == true;
    public static IEnumerable<JsonNode> Items(this JsonNode? node) => node is JsonArray array ? array.OfType<JsonNode>() : [];
}
