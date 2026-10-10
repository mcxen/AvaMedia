using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>One idle process per model/device. Running tasks own separate leases and never wait on another task.</summary>
public static class LocalSummaryModelCache
{
    private sealed class Entry(string root, string id, bool gpu)
    {
        public string Root { get; } = root;
        public string Id { get; } = id;
        public bool Gpu { get; } = gpu;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public LocalSummaryModel? Idle;
        public LocalModelWarmupBudget? Budget;
        public CancellationTokenSource? Expiration;
        public int Users, Revision;
        public string Stamp = "";
        public DateTime LastUsedUtc;
        public ModelRuntimeStatus Status = new(id, ModelLoadState.Unloaded, DateTime.UtcNow);
        public void State(ModelLoadState state, string backend = "", string? error = null)
        {
            Status = new(Id, state, DateTime.UtcNow) { Backend = backend, Error = error,
                StartedUtc = state == ModelLoadState.Loading ? DateTime.UtcNow : Status.StartedUtc };
            MediaTagRuntime.Notify(Root);
        }
        public void CancelExpiration() { Expiration?.Cancel(); Expiration = null; }
    }
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(BatchRename.PathComparer);
    private static readonly SemaphoreSlim BackgroundGate = new(1, 1);
    internal static bool IsLocal(ISummaryModel model) => model is LocalSummaryModel or Lease;
    private static Entry Get(ModelStore store, string id, bool gpu)
    {
        if (!ModelCatalog.RequiresSummaryRuntime(id)) throw new ArgumentException("请选择本地生成模型。");
        return Entries.GetOrAdd(store.Root + '\0' + id + '\0' + gpu, _ => new(store.Root, id, gpu));
    }
    public static ModelRuntimeStatus Status(string root, string id) => Entries.Values
        .Where(entry => BatchRename.PathComparer.Equals(entry.Root, root) && entry.Id == id)
        .Select(entry => entry.Status).OrderByDescending(state => state.State == ModelLoadState.InUse ? 4 : state.Preparing ? 3 : state.Loaded ? 2 : 1)
        .ThenByDescending(state => state.ChangedUtc).FirstOrDefault() ?? new(id, ModelLoadState.Unloaded, DateTime.UtcNow);
    internal static int IdleReaders(string root, string id) => Entries.Values.Count(entry =>
        BatchRename.PathComparer.Equals(root, entry.Root) && (entry.Id == id || id == ModelCatalog.SummaryRuntimeId)
        && Volatile.Read(ref entry.Idle) is not null && entry.Status.State is ModelLoadState.Ready or ModelLoadState.InUse);

    public static async Task WarmAsync(ModelStore store, string id, bool gpu, LocalModelWarmupBudget budget, CancellationToken ct,
        Func<CancellationToken, Task<byte[]>>? sample = null)
    {
        var entry = Get(store, id, gpu);
        if (!await store.IsInstalledAsync(id, ct: ct).ConfigureAwait(false)
            || !await store.IsInstalledAsync(ModelCatalog.SummaryRuntimeId, ct: ct).ConfigureAwait(false)) return;
        await BackgroundGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (entry.Users > 0) return;
                var existing = entry.Idle is { IsAlive: true } && entry.Stamp == Stamp(store, id);
                if (existing && (sample is null || entry.Idle!.VisualWarmed)) return;
                entry.CancelExpiration();
                if (!existing) await ClearIdleAsync(entry).ConfigureAwait(false);
                budget.BeginPreparation(); entry.Budget = budget;
                using var scope = budget.Enter();
                if (!existing) entry.State(ModelLoadState.Loading);
                var model = entry.Idle ?? await StartAsync(store, entry, ct).ConfigureAwait(false);
                // Keep a completed native load even when the window closes during that uninterruptible phase.
                entry.Idle = model; entry.Stamp = Stamp(store, id);
                model.SetBackground(true);
                try
                {
                    entry.State(ModelLoadState.Warming, model.Backend);
                    if (!budget.Foreground)
                    {
                        var image = sample is null ? null : await sample(ct).ConfigureAwait(false);
                        if (!budget.Foreground)
                        {
                            var started = Stopwatch.GetTimestamp();
                            await model.WarmAsync(ct, image).ConfigureAwait(false);
                            await budget.PaceAsync(started, gpu, ct).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    entry.LastUsedUtc = DateTime.UtcNow;
                    entry.State(ModelLoadState.Ready, model.Backend);
                    ScheduleExpiration(entry);
                }
            }
            catch (OperationCanceledException) { if (entry.Idle is null) entry.State(ModelLoadState.Unloaded); throw; }
            catch (Exception error)
            {
                await ClearIdleAsync(entry).ConfigureAwait(false);
                entry.State(ModelLoadState.Failed, error: error.Message); throw;
            }
            finally { entry.Budget = null; entry.Gate.Release(); }
        }
        finally { BackgroundGate.Release(); }
    }

    public static async Task<ISummaryToolModel> AcquireAsync(ModelStore store, string id, bool gpu, CancellationToken ct,
        Action<string>? status = null)
    {
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        using var pauseBoundary = JobExecutionControl.DeferPause();
        var entry = Get(store, id, gpu);
        foreach (var pending in Entries.Values.Where(pending => BatchRename.PathComparer.Equals(pending.Root, store.Root)))
            pending.Budget?.Promote();
        LocalSummaryModel? model;
        int revision;
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            entry.CancelExpiration();
            if (entry.Idle is { } stale && (!stale.IsAlive || entry.Stamp != Stamp(store, id))) await ClearIdleAsync(entry).ConfigureAwait(false);
            model = entry.Idle; entry.Idle = null;
            if (model is null && entry.Users == 0)
            { entry.State(ModelLoadState.Loading); model = await StartAsync(store, entry, ct, status).ConfigureAwait(false); }
            entry.Users++;
            revision = entry.Revision;
            entry.State(ModelLoadState.InUse, model?.Backend ?? "");
        }
        catch (Exception error) { entry.State(ModelLoadState.Failed, error: error.Message); throw; }
        finally { entry.Gate.Release(); }
        // A second active task creates its process outside the cache gate. In particular,
        // a waiting model writer must not prevent an existing task from returning its file leases.
        try
        {
            model ??= await StartAsync(store, entry, ct, status).ConfigureAwait(false);
            model.SetBackground(false); model.SetStatus(status);
            return new Lease(entry, model, revision, Stamp(store, id));
        }
        catch
        {
            if (model is not null) await DisposeModelAsync(model).ConfigureAwait(false);
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try { entry.Users--; entry.State(entry.Users > 0 ? ModelLoadState.InUse : ModelLoadState.Unloaded); }
            finally { entry.Gate.Release(); }
            throw;
        }
    }
    private static async Task<LocalSummaryModel> StartAsync(ModelStore store, Entry entry, CancellationToken ct, Action<string>? status = null)
    {
        var model = await LocalSummaryModel.StartAsync(store, entry.Id, entry.Gpu, ct, status).ConfigureAwait(false);
        return model;
    }
    private sealed class Lease(Entry entry, LocalSummaryModel model, int revision, string stamp) : ISummaryToolModel
    {
        private LocalSummaryModel? _model = model;
        private LocalSummaryModel Model => _model ?? throw new ObjectDisposedException(nameof(Lease));
        public string ModelId => Model.ModelId;
        public string Backend => Model.Backend;
        public Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null, int tokens = 1024,
            JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null) => Model.CompleteAsync(system, prompt, ct, image, tokens, schema, images);
        public Task<string> CompleteWithToolsAsync(string system, string prompt, IReadOnlyList<SummaryModelImage> images,
            IReadOnlyList<SummaryModelTool> tools, CancellationToken ct, int tokens = 2048) => Model.CompleteWithToolsAsync(system, prompt, images, tools, ct, tokens);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _model, null) is not { } returned) return;
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                entry.Users--;
                returned.SetStatus(null);
                if (revision == entry.Revision && entry.Idle is null && returned.IsAlive)
                { entry.Idle = returned; entry.Stamp = stamp; returned.SetBackground(true); }
                else await DisposeModelAsync(returned).ConfigureAwait(false);
                entry.LastUsedUtc = DateTime.UtcNow;
                entry.State(entry.Users > 0 ? ModelLoadState.InUse : entry.Idle is null ? ModelLoadState.Unloaded : ModelLoadState.Ready,
                    entry.Idle?.Backend ?? returned.Backend);
                ScheduleExpiration(entry);
            }
            finally { entry.Gate.Release(); }
        }
    }
    private static async Task DisposeModelAsync(LocalSummaryModel model)
    { await model.DisposeAsync().ConfigureAwait(false); }
    private static async Task ClearIdleAsync(Entry entry)
    {
        entry.CancelExpiration();
        var idle = entry.Idle; entry.Idle = null;
        if (idle is not null) await DisposeModelAsync(idle).ConfigureAwait(false);
    }
    public static async Task<bool> ReleaseAsync(string root, CancellationToken ct = default)
    {
        var released = true;
        foreach (var entry in Entries.Values.Where(entry => BatchRename.PathComparer.Equals(root, entry.Root)))
        {
            if (!await entry.Gate.WaitAsync(0, ct).ConfigureAwait(false)) { released = false; continue; }
            try { entry.Revision++; await ClearIdleAsync(entry).ConfigureAwait(false); entry.State(entry.Users > 0 ? ModelLoadState.InUse : ModelLoadState.Unloaded); released &= entry.Users == 0; }
            finally { entry.Gate.Release(); }
        }
        return released;
    }
    internal static async Task InvalidateAsync(string root, string id, CancellationToken ct)
    {
        foreach (var entry in Entries.Values.Where(entry => BatchRename.PathComparer.Equals(root, entry.Root)
            && (entry.Id == id || id == ModelCatalog.SummaryRuntimeId)))
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try { entry.Revision++; await ClearIdleAsync(entry).ConfigureAwait(false); entry.State(entry.Users > 0 ? ModelLoadState.InUse : ModelLoadState.Unloaded); }
            finally { entry.Gate.Release(); }
        }
    }
    internal static void RescheduleExpiration()
    { foreach (var entry in Entries.Values) _ = RescheduleAsync(entry); }
    private static async Task RescheduleAsync(Entry entry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try { ScheduleExpiration(entry); } finally { entry.Gate.Release(); }
    }
    private static void ScheduleExpiration(Entry entry)
    {
        entry.CancelExpiration();
        if (entry.Idle is null || MediaTagRuntime.IdleMinutes < 0) return;
        var expiration = new CancellationTokenSource(); entry.Expiration = expiration;
        entry.Status = entry.Status with { ReleaseUtc = entry.LastUsedUtc.AddMinutes(MediaTagRuntime.IdleMinutes) };
        MediaTagRuntime.Notify(entry.Root); _ = ExpireAsync(entry, expiration);
    }
    private static async Task ExpireAsync(Entry entry, CancellationTokenSource expiration)
    {
        try
        {
            var remaining = entry.LastUsedUtc.AddMinutes(MediaTagRuntime.IdleMinutes) - DateTime.UtcNow;
            await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, expiration.Token).ConfigureAwait(false);
            await entry.Gate.WaitAsync(expiration.Token).ConfigureAwait(false);
            try
            {
                if (entry.Expiration != expiration) return;
                entry.Expiration = null; await ClearIdleAsync(entry).ConfigureAwait(false);
                entry.State(entry.Users > 0 ? ModelLoadState.InUse : ModelLoadState.Unloaded);
            }
            finally { entry.Gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Trace.TraceError("Local model expiration: {0}", error); }
        finally { expiration.Dispose(); }
    }
    private static string Stamp(ModelStore store, string id) => store.InstalledStamp(id) + "|" + store.InstalledStamp(ModelCatalog.SummaryRuntimeId);
}
