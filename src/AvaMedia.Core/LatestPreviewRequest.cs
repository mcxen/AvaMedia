using System.Diagnostics;

namespace AvaMedia.Core;

/// <summary>Render one preview at a time, retaining only the latest waiting target.</summary>
public sealed class LatestPreviewRequest : IDisposable
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(50);
    private CancellationTokenSource? _source;
    private Func<CancellationToken, Task>? _pending;
    private Task _work = Task.CompletedTask;
    private long _started;
    private bool _disposed;
    public bool IsRunning { get; private set; }

    // Hosts call this on their UI thread. Moves replace pending work, never the
    // frame already being decoded; continuous input therefore cannot starve it.
    public Task Request(Func<CancellationToken, Task> render, CancellationToken lifetime, bool immediate = false)
    {
        if (_disposed || lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (immediate) { Cancel(); _started = 0; }
        _pending = render;
        if (IsRunning) return _work;
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _source = source; IsRunning = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work = completion.Task;
        _ = RunAsync(source, completion);
        return _work;
    }

    private async Task RunAsync(CancellationTokenSource source, TaskCompletionSource completion)
    {
        var token = source.Token;
        Exception? failure = null;
        try
        {
            while (_pending is not null && ReferenceEquals(_source, source))
            {
                var remaining = _started == 0 ? TimeSpan.Zero : _interval - Stopwatch.GetElapsedTime(_started);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                token.ThrowIfCancellationRequested();
                // Read after the throttle wait, so it uses the most recent move.
                var render = _pending; _pending = null;
                if (render is null) break;
                _started = Stopwatch.GetTimestamp();
                await render(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            if (ReferenceEquals(_source, source))
            { _source = null; _pending = null; IsRunning = false; }
            source.Dispose();
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }

    public void Cancel()
    {
        var source = _source; _source = null; _pending = null; IsRunning = false;
        source?.Cancel();
    }
    public void Dispose() { _disposed = true; Cancel(); }
}
