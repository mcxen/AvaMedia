using System.Collections.Concurrent;

namespace AvaMedia.Core;

public sealed class ConcurrencyGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Shared = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RateWindow> Rates = new(StringComparer.Ordinal);
    private sealed class RateWindow { public readonly Queue<(DateTimeOffset Time, int Tokens)> Requests = new(); }
    private readonly SemaphoreSlim _gate;
    private readonly RateWindow? _rate;
    private readonly int _rpm, _tpm;
    public ConcurrencyGate(string modelKey, bool local, int concurrency = 4, int requestsPerMinute = 120, int tokensPerMinute = 256000)
    {
        if (concurrency is < 1 or > 16 || requestsPerMinute < 1 || tokensPerMinute < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
        _gate = Shared.GetOrAdd(local ? "local-summary" : modelKey + ":" + concurrency, _ => new(local ? 1 : concurrency));
        _rate = local ? null : Rates.GetOrAdd(modelKey, _ => new()); _rpm = requestsPerMinute; _tpm = tokensPerMinute;
    }
    public async Task<T> RunAsync<T>(int tokens, Func<Task<T>> call, CancellationToken ct)
    {
        if (_rate is not null && tokens > _tpm) throw new InvalidOperationException("单次请求超过每分钟 token 配额。");
        await _gate.WaitAsync(ct);
        try
        {
            if (_rate is not null)
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (_rate)
                    {
                        var now = DateTimeOffset.UtcNow;
                        while (_rate.Requests.TryPeek(out var first) && now - first.Time >= TimeSpan.FromMinutes(1)) _rate.Requests.Dequeue();
                        if (_rate.Requests.Count < _rpm && _rate.Requests.Sum(r => r.Tokens) + tokens <= _tpm)
                        { _rate.Requests.Enqueue((now, tokens)); break; }
                        wait = TimeSpan.FromMinutes(1) - (now - _rate.Requests.Peek().Time);
                    }
                    await Task.Delay(wait, ct);
                }
            }
            ct.ThrowIfCancellationRequested(); return await call();
        }
        finally { _gate.Release(); }
    }
}
