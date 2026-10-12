namespace AvaMedia.Core;

/// <summary>Coalesces speech UI notifications, including a trailing update on failure or cancellation.</summary>
internal sealed class AiProgressUpdates : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Action> _pending = [];
    private readonly Task _worker;
    private bool _finished;

    public AiProgressUpdates() => _worker = Task.Run(PublishAsync);

    public void Set(string key, Action publish)
    {
        lock (_gate) _pending[key] = publish;
    }

    private async Task PublishAsync()
    {
        while (true)
        {
            await Task.Delay(250).ConfigureAwait(false);
            Action[] pending;
            bool finished;
            lock (_gate) { pending = _pending.Values.ToArray(); _pending.Clear(); finished = _finished; }
            foreach (var publish in pending) publish();
            if (finished) return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _finished = true;
        await _worker.ConfigureAwait(false);
    }
}
