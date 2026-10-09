namespace AvaMedia.Core;

public enum ModelLoadState { Unloaded, Loading, Warming, Ready, InUse, Failed }

public sealed record ModelRuntimeStatus(string Id, ModelLoadState State, DateTime ChangedUtc)
{
    public DateTime? StartedUtc { get; init; }
    public DateTime? ReleaseUtc { get; init; }
    public string Backend { get; init; } = "";
    public string? Error { get; init; }
    public bool Loaded => State is ModelLoadState.Ready or ModelLoadState.InUse;
    public bool Preparing => State is ModelLoadState.Loading or ModelLoadState.Warming;
}

/// <summary>Live state of the shared tagger cache; downloading and installing remain separate operations.</summary>
public static class MediaTagRuntime
{
    private static int _idleMinutes = 5;
    public static event Action<string>? Changed;
    public static int IdleMinutes => Volatile.Read(ref _idleMinutes);
    public static bool Supports(string id) => id is ModelCatalog.JoyTagId or ModelCatalog.NsfwId or ModelCatalog.EmbeddingId;
    public static ModelRuntimeStatus Status(string root, string id) => MediaTagModelCache.Status(root, id);
    public static Task<bool> ReleaseAsync(string root, CancellationToken ct = default) => MediaTagModelCache.ReleaseAsync(root, ct);
    public static void Configure(AppSettings settings)
    {
        var minutes = settings.TagModelIdleMinutes;
        if (minutes is not (-1 or 1 or 5 or 15 or 30)) minutes = 5;
        if (Interlocked.Exchange(ref _idleMinutes, minutes) != minutes) MediaTagModelCache.RescheduleExpiration();
    }
    internal static void Notify(string root)
    {
        if (Changed is not { } changed) return;
        foreach (Action<string> subscriber in changed.GetInvocationList())
            try { subscriber(root); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Model state observer: {0}", error); }
    }
}
