namespace AvaMedia.Core;

/// <summary>Model files may be read by independent runtimes; installation and deletion are exclusive.</summary>
internal sealed class ModelAccessGate
{
    private readonly object _gate = new();
    private int _readers, _writersWaiting;
    private bool _writing;
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsBusy { get { lock (_gate) return _writing || _readers > 0 || _writersWaiting > 0; } }
    public bool IsBusyExceptReaders(int idleReaders) { lock (_gate) return _writing || _readers > idleReaders || _writersWaiting > 0; }
    private void Wake() { _changed.TrySetResult(); _changed = NewSignal(); }

    public async Task<Action> AcquireReadAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                if (!_writing && _writersWaiting == 0) { _readers++; return ReleaseRead; }
                wait = _changed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }
    private void ReleaseRead() { lock (_gate) { _readers--; Wake(); } }

    public async Task WaitAsync(CancellationToken ct)
    {
        lock (_gate) _writersWaiting++;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task wait;
                lock (_gate)
                {
                    if (!_writing && _readers == 0) { _writing = true; return; }
                    wait = _changed.Task;
                }
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally { lock (_gate) { _writersWaiting--; Wake(); } }
    }
    public Task<bool> WaitAsync(int milliseconds, CancellationToken ct)
    {
        if (milliseconds != 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_writing || _readers > 0 || _writersWaiting > 0) return Task.FromResult(false);
            _writing = true; return Task.FromResult(true);
        }
    }
    public void Release() { lock (_gate) { _writing = false; Wake(); } }
}
