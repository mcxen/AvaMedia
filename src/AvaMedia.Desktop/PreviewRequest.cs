namespace AvaMedia.Desktop;

/// <summary>One active UI preview request; replacement cancels the previous work.</summary>
internal sealed class PreviewRequest : IDisposable
{
    private CancellationTokenSource? _current;

    public CancellationToken Token => _current?.Token ?? CancellationToken.None;

    public CancellationToken Restart(CancellationToken lifetime)
    {
        Cancel();
        _current = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        return _current.Token;
    }

    public bool IsCurrent(CancellationToken token) => _current?.Token == token && !token.IsCancellationRequested;

    public void Cancel()
    {
        var current = _current; _current = null;
        current?.Cancel(); current?.Dispose();
    }

    public void Dispose() => Cancel();
}
