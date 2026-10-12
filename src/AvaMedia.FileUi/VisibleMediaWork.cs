namespace AvaMedia.FileUi;

/// <summary>Only the current visible set owns work and results. Removed requests cannot publish.</summary>
public sealed class VisibleMediaWork<TKey, TResult>(Func<TKey, CancellationToken, Task<TResult>> read,
    Action<TKey, TResult> completed, int concurrency = 2) : IDisposable where TKey : notnull
{
    private sealed class Request
    {
        public readonly CancellationTokenSource Cancellation = new();
        public bool Finished, HasResult;
        public TResult? Result;
    }
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Request> _requests = [];
    private readonly SemaphoreSlim _slots = new(concurrency > 0 ? concurrency : throw new ArgumentOutOfRangeException(nameof(concurrency)));
    private bool _disposed;

    // The UI rechecks this after dispatcher delivery and when an inspector or cell is recreated.
    public bool TryGetResult(TKey key, out TResult result)
    {
        lock (_gate)
        {
            if (!_disposed && _requests.TryGetValue(key, out var request) && request.HasResult)
            { result = request.Result!; return true; }
            result = default!; return false;
        }
    }
    public void Update(IEnumerable<TKey> visible)
    {
        var wanted = visible.ToHashSet();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var key in _requests.Keys.Where(key => !wanted.Contains(key)).ToArray())
            {
                var old = _requests[key]; _requests.Remove(key); Cancel(old);
            }
            foreach (var key in wanted)
            {
                if (_requests.ContainsKey(key)) continue;
                var request = new Request(); _requests.Add(key, request);
                _ = RunAsync(key, request);
            }
        }
    }
    private static void Cancel(Request request)
    {
        request.Cancellation.Cancel();
        if (request.Finished) request.Cancellation.Dispose();
    }
    private async Task RunAsync(TKey key, Request request)
    {
        var token = request.Cancellation.Token;
        var acquired = false;
        try
        {
            await Task.Yield();
            await _slots.WaitAsync(token).ConfigureAwait(false); acquired = true;
            var result = await read(key, token).ConfigureAwait(false);
            lock (_gate)
                if (!_disposed && !token.IsCancellationRequested && _requests.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                {
                    request.Result = result; request.HasResult = true;
                    completed(key, result);
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* A failed cover never blocks browsing or selection. */ }
        finally
        {
            if (acquired) _slots.Release();
            lock (_gate)
            {
                request.Finished = true;
                if (_disposed || !_requests.TryGetValue(key, out var current) || !ReferenceEquals(current, request)) request.Cancellation.Dispose();
            }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var request in _requests.Values) Cancel(request);
            _requests.Clear();
        }
    }
}
