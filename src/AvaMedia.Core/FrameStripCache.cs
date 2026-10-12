using System.Runtime.ExceptionServices;

namespace AvaMedia.Core;

public sealed record FrameStripFrame(int Index, double Seconds, byte[] Image);

/// <summary>Shares in-flight extractions and retains the most recently used completed strips.</summary>
public sealed class FrameStripCache(IMediaPreview preview, int capacity = 8)
{
    private readonly IMediaPreview _preview = preview ?? throw new ArgumentNullException(nameof(preview));
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly LinkedList<Key> _lru = new();

    public async Task<IReadOnlyList<FrameStripFrame>> GetAsync(string path, double duration, int count,
        int width = FrameStrip.Width, int height = FrameStrip.Height, int videoStreamIndex = 0,
        IProgress<FrameStripFrame>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var file = new FileInfo(Path.GetFullPath(path));
        count = Math.Clamp(count, 1, FrameStrip.MaxCount);
        var key = new Key(OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName,
            file.Length, file.LastWriteTimeUtc.Ticks, count, width, height, videoStreamIndex);
        Entry entry;
        bool extract;
        lock (_gate)
        {
            extract = !_entries.TryGetValue(key, out entry!);
            if (extract) _entries.Add(key, entry = new Entry());
            else if (entry.Node is { } node)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }
        }
        // A caller only cancels its wait. The shared extraction finishes and can populate the cache.
        if (extract) _ = ExtractAsync(key, entry, file.FullName, duration);
        var index = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FrameStripFrame? frame;
            Task changed;
            lock (_gate)
            {
                frame = index < entry.Frames.Count ? entry.Frames[index++] : null;
                if (frame is null)
                {
                    entry.Error?.Throw();
                    if (entry.Result is { } result) return result;
                }
                changed = entry.Changed.Task;
            }
            // Each caller replays existing frames before waiting for new ones, preserving order.
            if (frame is not null) progress?.Report(frame);
            else await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ExtractAsync(Key key, Entry entry, string path, double duration)
    {
        try
        {
            var times = FrameStrip.Times(duration, key.Count);
            for (var index = 0; index < times.Length; index++)
            {
                var image = await _preview.Thumbnail(path, times[index], key.Width, key.Height,
                    CancellationToken.None, pad: false, videoStreamIndex: key.Stream).ConfigureAwait(false);
                lock (_gate)
                {
                    entry.Frames.Add(new FrameStripFrame(index, times[index], image));
                    entry.Notify();
                }
            }
            lock (_gate)
            {
                entry.Result = entry.Frames.AsReadOnly();
                entry.Node = _lru.AddFirst(key);
                while (_lru.Count > _capacity)
                {
                    _entries.Remove(_lru.Last!.Value);
                    _lru.RemoveLast();
                }
                entry.Notify();
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _entries.Remove(key);
                entry.Error = ExceptionDispatchInfo.Capture(error);
                entry.Notify();
            }
        }
    }

    private sealed record Key(string Path, long Length, long Modified, int Count, int Width, int Height, int Stream);

    private sealed class Entry
    {
        public readonly List<FrameStripFrame> Frames = [];
        public IReadOnlyList<FrameStripFrame>? Result;
        public ExceptionDispatchInfo? Error;
        public LinkedListNode<Key>? Node;
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Notify()
        {
            var changed = Changed;
            Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.SetResult();
        }
    }
}
