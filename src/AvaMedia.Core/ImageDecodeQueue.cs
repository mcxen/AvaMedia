namespace AvaMedia.Core;

// Keep native decoder resource use bounded, but let an explicitly selected image pass queued thumbnails.
internal sealed class ImageDecodeQueue
{
    private readonly object _gate = new();
    private readonly LinkedList<Request> _foreground = new(), _background = new();
    private bool _busy;

    public async Task WaitAsync(CancellationToken token, bool background = false)
    {
        token.ThrowIfCancellationRequested();
        Request request;
        lock (_gate)
        {
            if (!_busy) { _busy = true; return; }
            var queue = background ? _background : _foreground;
            request = new(queue); request.Node = queue.AddLast(request);
        }
        using var registration = token.Register(() =>
        {
            lock (_gate)
            {
                if (request.Node is not { } node) return;
                request.Queue.Remove(node); request.Node = null;
                request.Ready.TrySetCanceled(token);
            }
        });
        await request.Ready.Task.ConfigureAwait(false);
    }

    public void Release()
    {
        lock (_gate)
        {
            var queue = _foreground.Count > 0 ? _foreground : _background;
            if (queue.First is not { } node) { _busy = false; return; }
            queue.RemoveFirst(); node.Value.Node = null; node.Value.Ready.TrySetResult();
        }
    }

    private sealed class Request(LinkedList<Request> queue)
    {
        public LinkedList<Request> Queue { get; } = queue;
        public LinkedListNode<Request>? Node;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
