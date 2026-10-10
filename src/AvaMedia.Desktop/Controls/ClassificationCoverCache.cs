using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Window-owned decoded LRU. Leases keep visible images alive through eviction and closing.</summary>
internal sealed class ClassificationCoverCache : IDisposable
{
    private const int MaxEntries = 256;
    private const long MaxBytes = 48L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly LinkedList<Entry> _recent = [];
    private long _bytes;
    private bool _disposed;
    private readonly record struct Key(string Path, double Seconds, int Width, long Length, DateTime Modified);

    public async Task<Lease> AcquireAsync(IMediaEngine engine, string path, double seconds, int width,
        MediaTagResult? media, CancellationToken ct)
    {
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        var key = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (media is not null) MediaTagService.ValidateSource(media);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("源文件不存在。", path);
            return new Key(path, seconds, width, info.Length, info.LastWriteTimeUtc);
        }, ct).ConfigureAwait(false);
        Entry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key); _entries.Add(key, entry);
                entry.Node = _recent.AddFirst(entry);
                entry.Work = Task.Run(() => LoadAsync(entry, engine));
                _ = entry.Work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            Touch(entry);
            if (entry.Bitmap is not null)
            { entry.Users++; return new Lease(entry.Bitmap, () => Release(entry)); }
            entry.Waiters++;
        }
        try
        {
            await entry.Work.WaitAsync(ct).ConfigureAwait(false);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                entry.Users++; Touch(entry);
                return new Lease(entry.Bitmap!, () => Release(entry));
            }
        }
        finally
        {
            lock (_sync)
            {
                entry.Waiters--;
                if (entry.Loading && !entry.Started && entry.Waiters == 0 && entry.Users == 0)
                { entry.Request.Cancel(); Remove(entry); }
                ReleasePixels(entry); Trim();
            }
        }
    }

    private async Task LoadAsync(Entry entry, IMediaEngine engine)
    {
        var entered = false;
        try
        {
            var ct = entry.Request.Token;
            await _gate.WaitAsync(ct).ConfigureAwait(false); entered = true;
            lock (_sync) { ct.ThrowIfCancellationRequested(); entry.Started = true; }
            var key = entry.Key;
            var bytes = await engine.Thumbnail(key.Path, key.Seconds, key.Width, key.Width * 9 / 16, ct, pad: false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(key.Path);
            if (!info.Exists || info.Length != key.Length || info.LastWriteTimeUtc != key.Modified)
                throw new IOException("源文件在读取预览时发生变化。");
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);
            lock (_sync)
            {
                if (_disposed || ct.IsCancellationRequested || !entry.Cached)
                { bitmap.Dispose(); throw new OperationCanceledException(ct); }
                entry.Bitmap = bitmap;
                entry.Bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
                _bytes += entry.Bytes; Trim();
            }
        }
        catch
        {
            lock (_sync) Remove(entry);
            throw;
        }
        finally
        {
            if (entered) _gate.Release();
            lock (_sync)
            { entry.Loading = false; entry.Request.Dispose(); ReleasePixels(entry); Trim(); }
        }
    }

    private void Touch(Entry entry)
    {
        if (entry.Node is not { } node) return;
        _recent.Remove(node); _recent.AddFirst(node);
    }
    private void Trim()
    {
        var node = _recent.Last;
        while (node is not null && (_entries.Count > MaxEntries || _bytes > MaxBytes))
        {
            var previous = node.Previous; var entry = node.Value;
            if (!entry.Loading && entry.Users == 0 && entry.Waiters == 0) Remove(entry);
            node = previous;
        }
    }
    private void Remove(Entry entry)
    {
        if (!entry.Cached) return;
        entry.Cached = false;
        _entries.Remove(entry.Key);
        if (entry.Node is { } node) _recent.Remove(node);
        entry.Node = null; _bytes -= entry.Bytes;
        ReleasePixels(entry);
    }
    private static void ReleasePixels(Entry entry)
    {
        if (entry.Cached || entry.Users != 0 || entry.Waiters != 0) return;
        entry.Bitmap?.Dispose(); entry.Bitmap = null;
    }
    private void Release(Entry entry)
    {
        lock (_sync) { entry.Users--; ReleasePixels(entry); Trim(); }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _recent.ToArray())
            { if (entry.Loading) entry.Request.Cancel(); Remove(entry); }
        }
    }
    private sealed class Entry(Key key)
    {
        public Key Key { get; } = key;
        public CancellationTokenSource Request { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public LinkedListNode<Entry>? Node;
        public Bitmap? Bitmap;
        public long Bytes;
        public int Users, Waiters;
        public bool Cached = true, Loading = true, Started;
    }
    internal sealed class Lease : IDisposable
    {
        private Action? _release;
        public Bitmap Bitmap { get; }
        internal Lease(Bitmap bitmap, Action release)
        { Bitmap = bitmap; _release = release; }
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
