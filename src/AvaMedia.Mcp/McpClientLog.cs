using System.Net;

namespace AvaMedia.Mcp;

public sealed record McpClientView(string Address, string Agent, string Kind, DateTime FirstSeenUtc, DateTime LastSeenUtc, long Requests);

/// <summary>In-memory record of peers that reached the listener. Only data from the HTTP request itself is kept.</summary>
internal sealed class McpClientLog
{
    private const int Limit = 20;
    private readonly object _gate = new();
    private readonly Dictionary<string, McpClientView> _items = new(StringComparer.Ordinal);
    private DateTime _lastNotice;
    public event Action? Changed;

    public void Note(IPAddress? address, string agent, bool mcp)
    {
        var ip = address is null ? "" : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        if (agent.Length > 160) agent = agent[..160];
        var kind = mcp && !agent.Contains("Mozilla", StringComparison.Ordinal) ? "MCP" : "Web";
        var key = ip + "|" + kind + "|" + agent; var now = DateTime.UtcNow; bool notify;
        lock (_gate)
        {
            _items[key] = _items.TryGetValue(key, out var seen)
                ? seen with { LastSeenUtc = now, Requests = seen.Requests + 1 }
                : new(ip, agent, kind, now, now, 1);
            if (_items.Count > Limit) _items.Remove(_items.MinBy(item => item.Value.LastSeenUtc).Key);
            // Throttle UI refreshes; polling clients call every few seconds.
            notify = now - _lastNotice > TimeSpan.FromSeconds(2); if (notify) _lastNotice = now;
        }
        if (notify) Changed?.Invoke();
    }

    public McpClientView[] Snapshot()
    { lock (_gate) return _items.Values.OrderByDescending(item => item.LastSeenUtc).ToArray(); }
}
