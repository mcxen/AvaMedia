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

    internal sealed class Entry(string root)
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
        public readonly ConcurrentDictionary<string, ModelRuntimeStatus> States = new();
        public DateTime LastUsedUtc = DateTime.UtcNow;
        public void State(string id, ModelLoadState state, string backend = "", string? error = null)
        {
            States.TryGetValue(id, out var previous);
            States[id] = new(id, state, DateTime.UtcNow)
            {
                StartedUtc = state == ModelLoadState.Loading ? DateTime.UtcNow : previous?.StartedUtc,
                Backend = backend.Length > 0 ? backend : previous?.Backend ?? "", Error = error
            };
            MediaTagRuntime.Notify(root);
        }
        public void SetReady()
        {
            LastUsedUtc = DateTime.UtcNow;
            foreach (var id in States.Keys)
            {
                if (States[id].State is not (ModelLoadState.InUse or ModelLoadState.Ready)) continue;
                var backend = id == ModelCatalog.JoyTagId ? Tags?.Backend : id == ModelCatalog.NsfwId ? Nsfw?.Backend : Embedding?.Backend;
                State(id, ModelLoadState.Ready, backend ?? "");
            }
        }
        private CancellationTokenSource? _expiration;

        public void CancelExpiration() { _expiration?.Cancel(); _expiration = null; }
        public async Task ClearAsync()
        {
            CancelExpiration();
            Tags?.Dispose(); Tags = null; Vocabulary = [];
            Nsfw?.Dispose(); Nsfw = null;
            if (Embedding is { } embedding) { Embedding = null; await embedding.DisposeAsync().ConfigureAwait(false); }
            Stamps.Clear(); PreferGpu = null; SemanticError = null;
            foreach (var id in States.Keys.Where(id => States[id].State != ModelLoadState.Failed))
                State(id, ModelLoadState.Unloaded);
        }
        public void ScheduleExpiration() => _ = ExpireAsync();
        private async Task ExpireAsync()
        {
            CancellationTokenSource? expiration = null;
            DateTime release;
            try
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    CancelExpiration();
                    foreach (var id in States.Keys) States[id] = States[id] with { ReleaseUtc = null };
                    MediaTagRuntime.Notify(root);
                    if (!States.Values.Any(value => value.Loaded) || MediaTagRuntime.IdleMinutes < 0) return;
                    expiration = new(); _expiration = expiration;
                    release = LastUsedUtc.AddMinutes(MediaTagRuntime.IdleMinutes);
                    foreach (var id in States.Keys.Where(id => States[id].State == ModelLoadState.Ready))
                        States[id] = States[id] with { ReleaseUtc = release };
                    MediaTagRuntime.Notify(root);
                }
                finally { Gate.Release(); }
                var remaining = release - DateTime.UtcNow;
                await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, expiration.Token).ConfigureAwait(false);
                await Gate.WaitAsync(expiration.Token).ConfigureAwait(false);
                try
                {
                    if (_expiration == expiration && MediaTagRuntime.IdleMinutes >= 0
                        && DateTime.UtcNow >= LastUsedUtc.AddMinutes(MediaTagRuntime.IdleMinutes))
                    {
                        _expiration = null;
                        await ClearAsync().ConfigureAwait(false);
                    }
                }
                finally { Gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("AI model cache expiration: {0}", error); }
            finally
            {
                if (expiration is not null)
                {
                    await Gate.WaitAsync().ConfigureAwait(false);
                    try { if (_expiration == expiration) _expiration = null; }
                    finally { Gate.Release(); expiration.Dispose(); }
                }
            }
        }
    }

    public static ModelRuntimeStatus Status(string root, string id) => Entries.TryGetValue(root, out var entry)
        && entry.States.TryGetValue(id, out var status) ? status : new(id, ModelLoadState.Unloaded, DateTime.UtcNow);
    public static void RescheduleExpiration()
    {
        foreach (var entry in Entries.Values) entry.ScheduleExpiration();
    }
    public static async Task<bool> ReleaseAsync(string root, CancellationToken ct)
    {
        if (!Entries.TryGetValue(root, out var entry)) return true;
        if (!await entry.Gate.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try { await entry.ClearAsync().ConfigureAwait(false); return true; }
        finally { entry.Gate.Release(); }
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
            retained.SetReady();
            retained.Gate.Release(); retained.ScheduleExpiration();
        }
    }

    public static async Task<Lease> AcquireAsync(ModelStore store, MediaTagOptions options, Action<string>? status, CancellationToken ct, bool preparing = false)
    {
        var entry = Entries.GetOrAdd(store.Root, root => new(root));
        status?.Invoke("等待本地模型");
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
            if (entry.Tags is null)
            {
                entry.State(ModelCatalog.JoyTagId, ModelLoadState.Loading); status?.Invoke("加载标签模型");
            }
            var tags = await AcquireFile(ModelCatalog.JoyTagId).ConfigureAwait(false);
            if (entry.Tags is null)
            {
                entry.Vocabulary = (await File.ReadAllLinesAsync(Path.Combine(tags.Directory, ModelCatalog.JoyTagLabels), ct).ConfigureAwait(false))
                    .Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
                if (entry.Vocabulary.Length != 5813) throw new InvalidDataException("模型标签文件无效，请重新下载 JoyTag。");
                entry.Tags = new(Path.Combine(tags.Directory, ModelCatalog.JoyTagFile), ModelCatalog.Find(ModelCatalog.JoyTagId).Files[0].Sha256,
                    options.PreferGpu, options.BatchSize);
                ct.ThrowIfCancellationRequested();
                if (preparing)
                {
                    entry.State(ModelCatalog.JoyTagId, ModelLoadState.Warming, entry.Tags.Backend); status?.Invoke("预热标签模型");
                    MediaTagService.Predict(entry.Tags, [WarmImage.Value], options.BatchSize, entry.Vocabulary.Length, ct);
                }
                entry.Stamps[ModelCatalog.JoyTagId] = Stamp(store, ModelCatalog.JoyTagId);
            }
            entry.State(ModelCatalog.JoyTagId, preparing ? ModelLoadState.Ready : ModelLoadState.InUse, entry.Tags.Backend);
            if (options.RecognizeNsfw)
            {
                if (entry.Nsfw is null) { entry.State(ModelCatalog.NsfwId, ModelLoadState.Loading); status?.Invoke("加载成人内容模型"); }
                var nsfw = await AcquireFile(ModelCatalog.NsfwId).ConfigureAwait(false);
                if (entry.Nsfw is null)
                {
                    entry.Nsfw = new(nsfw.Directory, options.PreferGpu);
                    ct.ThrowIfCancellationRequested();
                    if (preparing)
                    {
                        entry.State(ModelCatalog.NsfwId, ModelLoadState.Warming, entry.Nsfw.Backend); status?.Invoke("预热成人内容模型");
                        entry.Nsfw.Analyze([WarmImage.Value], [0], [0], ct);
                    }
                    entry.Stamps[ModelCatalog.NsfwId] = Stamp(store, ModelCatalog.NsfwId);
                }
                entry.State(ModelCatalog.NsfwId, preparing ? ModelLoadState.Ready : ModelLoadState.InUse, entry.Nsfw.Backend);
            }
            if (options.NeedsSemanticModel && await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: ct).ConfigureAwait(false))
            {
                try
                {
                    if (entry.Embedding is null) entry.State(ModelCatalog.EmbeddingId, ModelLoadState.Loading);
                    var embedding = await AcquireFile(ModelCatalog.EmbeddingId).ConfigureAwait(false);
                    if (entry.Embedding is null)
                    {
                        entry.Embedding = await GemmaMediaEmbedding.StartCachedAsync(embedding, ct, options.PreferGpu, status).ConfigureAwait(false);
                        if (preparing)
                        {
                            entry.State(ModelCatalog.EmbeddingId, ModelLoadState.Warming, entry.Embedding.Backend);
                            status?.Invoke("预热图片嵌入模型");
                            await entry.Embedding.EmbedImageAsync(WarmImage.Value, ct).ConfigureAwait(false);
                        }
                        entry.Stamps[ModelCatalog.EmbeddingId] = Stamp(store, ModelCatalog.EmbeddingId);
                    }
                    entry.State(ModelCatalog.EmbeddingId, preparing ? ModelLoadState.Ready : ModelLoadState.InUse, entry.Embedding.Backend);
                }
                catch (Exception error) when (error is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    entry.SemanticError = error.Message;
                    if (entry.Embedding is { } failed) { entry.Embedding = null; await failed.DisposeAsync().ConfigureAwait(false); }
                    entry.Stamps.Remove(ModelCatalog.EmbeddingId);
                    entry.State(ModelCatalog.EmbeddingId, ModelLoadState.Failed, error: error.Message);
                }
            }
            ct.ThrowIfCancellationRequested();
            return new(entry, files);
        }
        catch (Exception error)
        {
            var failed = entry.States.Values.FirstOrDefault(value => value.Preparing)?.Id;
            try { await entry.ClearAsync().ConfigureAwait(false); }
            finally
            {
                foreach (var file in files.AsEnumerable().Reverse()) file.Dispose();
                if (failed is not null && error is not OperationCanceledException) entry.State(failed, ModelLoadState.Failed, error: error.Message);
                entry.Gate.Release();
            }
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
