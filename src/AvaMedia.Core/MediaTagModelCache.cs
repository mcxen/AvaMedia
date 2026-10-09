using System.Collections.Concurrent;
using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>Shared, serialized tagger sessions. Idle sessions retain memory, never file leases.</summary>
internal static class MediaTagModelCache
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(BatchRename.PathComparer);
    private static readonly Lazy<byte[]> WarmImage = new(() =>
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    });

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<string, string> Stamps = [];
        public ModelInferenceSession? Tags;
        public string[] Vocabulary = [];
        public RealNsfwClassifier? Nsfw;
        public GemmaMediaEmbedding? Embedding;
        public string? SemanticError;
        public bool? PreferGpu;
        public int BatchSize;
        public int Retainers;
        private CancellationTokenSource? _expiration;

        public void CancelExpiration() { _expiration?.Cancel(); _expiration = null; }
        public async Task ClearAsync()
        {
            CancelExpiration();
            Tags?.Dispose(); Tags = null; Vocabulary = [];
            Nsfw?.Dispose(); Nsfw = null;
            if (Embedding is { } embedding) { Embedding = null; await embedding.DisposeAsync().ConfigureAwait(false); }
            Stamps.Clear(); PreferGpu = null; SemanticError = null;
        }
        public void ScheduleExpiration() => _ = ExpireAsync();
        private async Task ExpireAsync()
        {
            CancellationTokenSource? expiration = null;
            try
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref Retainers) > 0) return;
                    CancelExpiration();
                    expiration = new(); _expiration = expiration;
                }
                finally { Gate.Release(); }
                await Task.Delay(TimeSpan.FromMinutes(5), expiration.Token).ConfigureAwait(false);
                await Gate.WaitAsync(expiration.Token).ConfigureAwait(false);
                try
                {
                    if (_expiration == expiration)
                    {
                        _expiration = null;
                        if (Volatile.Read(ref Retainers) == 0) await ClearAsync().ConfigureAwait(false);
                    }
                }
                finally { Gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("AI model cache expiration: {0}", error); }
            finally { expiration?.Dispose(); }
        }
    }

    private sealed class Retention(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry, null) is not { } retained) return;
            Interlocked.Decrement(ref retained.Retainers); retained.ScheduleExpiration();
        }
    }
    public static IDisposable Retain(ModelStore store)
    {
        var entry = Entries.GetOrAdd(store.Root, _ => new());
        Interlocked.Increment(ref entry.Retainers);
        return new Retention(entry);
    }

    internal sealed class Lease(Entry entry, List<ModelLease> files) : IDisposable
    {
        private Entry? _entry = entry;
        private Entry Current => _entry ?? throw new ObjectDisposedException(nameof(Lease));
        public ModelInferenceSession Tags => Current.Tags!;
        public string[] Vocabulary => Current.Vocabulary;
        public RealNsfwClassifier? Nsfw => Current.Nsfw;
        public GemmaMediaEmbedding? Embedding => Current.Embedding;
        public string? SemanticError => Current.SemanticError;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _entry, null) is not { } retained) return;
            foreach (var file in files.AsEnumerable().Reverse()) file.Dispose();
            retained.Gate.Release(); retained.ScheduleExpiration();
        }
    }

    public static async Task<Lease> AcquireAsync(ModelStore store, MediaTagOptions options, Action<string>? status, CancellationToken ct)
    {
        var entry = Entries.GetOrAdd(store.Root, _ => new());
        status?.Invoke("准备本地模型");
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        var files = new List<ModelLease>();
        try
        {
            entry.CancelExpiration();
            if (entry.PreferGpu != options.PreferGpu || entry.BatchSize != options.BatchSize
                || entry.Stamps.Any(item => item.Value != Stamp(store, item.Key)))
                await entry.ClearAsync().ConfigureAwait(false);
            entry.PreferGpu = options.PreferGpu; entry.BatchSize = options.BatchSize;
            entry.SemanticError = null;
            async Task<ModelLease> AcquireFile(string id)
            {
                var file = await store.AcquireAsync(id, ct, verify: !entry.Stamps.ContainsKey(id)).ConfigureAwait(false);
                files.Add(file); return file;
            }
            var tags = await AcquireFile(ModelCatalog.JoyTagId).ConfigureAwait(false);
            if (entry.Tags is null)
            {
                status?.Invoke("预热标签模型");
                entry.Vocabulary = (await File.ReadAllLinesAsync(Path.Combine(tags.Directory, ModelCatalog.JoyTagLabels), ct).ConfigureAwait(false))
                    .Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
                if (entry.Vocabulary.Length != 5813) throw new InvalidDataException("模型标签文件无效，请重新下载 JoyTag。");
                entry.Tags = new(Path.Combine(tags.Directory, ModelCatalog.JoyTagFile), ModelCatalog.Find(ModelCatalog.JoyTagId).Files[0].Sha256,
                    options.PreferGpu, options.BatchSize);
                MediaTagService.Predict(entry.Tags, [WarmImage.Value], options.BatchSize, entry.Vocabulary.Length, ct);
                entry.Stamps[ModelCatalog.JoyTagId] = Stamp(store, ModelCatalog.JoyTagId);
            }
            if (options.RecognizeNsfw)
            {
                var nsfw = await AcquireFile(ModelCatalog.NsfwId).ConfigureAwait(false);
                if (entry.Nsfw is null)
                {
                    status?.Invoke("预热 NSFW 模型");
                    entry.Nsfw = new(nsfw.Directory, options.PreferGpu);
                    entry.Nsfw.Analyze([WarmImage.Value], [0], [0], ct);
                    entry.Stamps[ModelCatalog.NsfwId] = Stamp(store, ModelCatalog.NsfwId);
                }
            }
            if (options.NeedsSemanticModel && await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: ct).ConfigureAwait(false))
            {
                try
                {
                    var embedding = await AcquireFile(ModelCatalog.EmbeddingId).ConfigureAwait(false);
                    if (entry.Embedding is null)
                    {
                        entry.Embedding = await GemmaMediaEmbedding.StartCachedAsync(embedding, ct, options.PreferGpu, status).ConfigureAwait(false);
                        status?.Invoke("预热图片嵌入模型");
                        await entry.Embedding.EmbedImageAsync(WarmImage.Value, ct).ConfigureAwait(false);
                        entry.Stamps[ModelCatalog.EmbeddingId] = Stamp(store, ModelCatalog.EmbeddingId);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    entry.SemanticError = error.Message;
                    if (entry.Embedding is { } failed) { entry.Embedding = null; await failed.DisposeAsync().ConfigureAwait(false); }
                    entry.Stamps.Remove(ModelCatalog.EmbeddingId);
                }
            }
            ct.ThrowIfCancellationRequested();
            return new(entry, files);
        }
        catch
        {
            try { await entry.ClearAsync().ConfigureAwait(false); }
            finally { foreach (var file in files.AsEnumerable().Reverse()) file.Dispose(); entry.Gate.Release(); }
            throw;
        }
    }

    public static async Task InvalidateAsync(string root, string id, CancellationToken ct)
    {
        if (id is not (ModelCatalog.JoyTagId or ModelCatalog.NsfwId or ModelCatalog.EmbeddingId)
            || !Entries.TryGetValue(root, out var entry)) return;
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await entry.ClearAsync().ConfigureAwait(false); }
        finally { entry.Gate.Release(); }
    }

    private static string Stamp(ModelStore store, string id)
    {
        return string.Join('|', ModelCatalog.Find(id).Files.Select(file => Path.Combine(store.DirectoryFor(id), file.Path))
            .Append(Path.Combine(store.DirectoryFor(id), "installed.json")).Select(path =>
            {
                var info = new FileInfo(path);
                return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
            }));
    }
}
