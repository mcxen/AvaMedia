namespace AvaMedia.Core;

/// <summary>Task-local cooperative pause. Native work completes before its next checkpoint.</summary>
internal sealed class JobExecutionControl
{
    private static readonly AsyncLocal<JobExecutionControl?> Current = new();
    private readonly object _gate = new();
    private TaskCompletionSource? _resume;
    private int _deferred;
    private bool _parked;
    public event Action? Parked;
    public bool IsPaused { get { lock (_gate) return _resume is not null; } }
    public bool IsParked { get { lock (_gate) return _parked; } }

    public IDisposable Enter()
    {
        var previous = Current.Value; Current.Value = this;
        return new Scope(() => Current.Value = previous);
    }
    public void Pause() { lock (_gate) _resume ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume()
    {
        lock (_gate) { _resume?.TrySetResult(); _resume = null; _parked = false; }
    }
    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task? resume; bool parked;
            lock (_gate) { resume = _resume?.Task; parked = resume is not null && !_parked; if (resume is not null) _parked = true; }
            if (resume is null) return;
            if (parked) Parked?.Invoke();
            await resume.WaitAsync(ct).ConfigureAwait(false);
        }
    }
    public static Task CheckpointAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Current.Value is { } current && Volatile.Read(ref current._deferred) == 0
            ? current.WaitAsync(ct) : Task.CompletedTask;
    }
    // Never park while owning a shared inference session. Release it at the end of
    // the current analysis unit, then checkpoint before acquiring the next unit.
    public static IDisposable DeferPause()
    {
        var current = Current.Value;
        if (current is null) return new Scope(() => { });
        Interlocked.Increment(ref current._deferred);
        return new Scope(() => Interlocked.Decrement(ref current._deferred));
    }
    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
