using System.Collections.Concurrent;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Long tasks share one underlying cache lease per model instead of causing the cache to open another process per reader.</summary>
public static class SharedSummaryModelLease
{
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public ISummaryModel? Model; public int Users; }
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Inference = new(1, 1);
    public static Task<ISummaryModel> AcquireAsync(ModelStore store, string id, CancellationToken ct, Action<string>? status = null) =>
        AcquireAsync(store.Root + "\0" + id, async token => await LocalSummaryModelCache.AcquireAsync(store, id, token, status), ct);
    public static async Task<ISummaryModel> AcquireAsync(string key, Func<CancellationToken, Task<ISummaryModel>> open, CancellationToken ct)
    {
        var entry = Entries.GetOrAdd(key, _ => new()); await entry.Gate.WaitAsync(ct);
        try { entry.Model ??= await open(ct); entry.Users++; return new Lease(entry, entry.Model); }
        finally { entry.Gate.Release(); }
    }
    private sealed class Lease(Entry entry, ISummaryModel model) : ISummaryModel
    {
        private int _disposed;
        public string ModelId => model.ModelId;
        public string Backend => model.Backend;
        public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
            int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await Inference.WaitAsync(ct);
            try { return await model.CompleteAsync(system, prompt, ct, image, tokens, schema, images); }
            finally { Inference.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await entry.Gate.WaitAsync();
            try { if (--entry.Users == 0) { entry.Model = null; await model.DisposeAsync(); } }
            finally { entry.Gate.Release(); }
        }
    }
}
