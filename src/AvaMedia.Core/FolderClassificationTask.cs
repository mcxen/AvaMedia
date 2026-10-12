using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed class FolderClassificationTaskOptions
{
    public FolderClassificationRule[] Rules { get; set; } = [];
    public MediaTagOptions Analysis { get; set; } = new();
    public double TagThreshold { get; set; } = .5;
    public bool IncludeNsfw { get; set; }
    public bool AllowSemanticDownload { get; set; }
    public string OutputFolder { get; set; } = "";
    public bool SplitTypes { get; set; } = true;
    public bool WriteText { get; set; } = true;
    public bool Move { get; set; }
    public string[] ExcludedPaths { get; set; } = [];
    public string[] SourceFolders { get; set; } = [];
    public string? LastJournal { get; set; }

    public FolderClassificationTaskOptions Clone() => new()
    {
        Rules = Rules.Select(rule => rule with { Categories = rule.Categories.Select(category => category with
        { Tags = category.Tags.Select(tags => tags.ToArray()).ToArray(), SupersededBy = category.SupersededBy.ToArray() }).ToArray() }).ToArray(),
        Analysis = Analysis with { SemanticCandidates = Analysis.SemanticCandidates.ToArray() },
        TagThreshold = TagThreshold, IncludeNsfw = IncludeNsfw, AllowSemanticDownload = AllowSemanticDownload, OutputFolder = OutputFolder,
        SplitTypes = SplitTypes, WriteText = WriteText, Move = Move, ExcludedPaths = ExcludedPaths.ToArray(), SourceFolders = SourceFolders.ToArray(), LastJournal = LastJournal
    };
}

public sealed record FolderClassificationTaskFile(string Path, FolderClassifiedFile? Result = null, string? Error = null, bool Pending = true);
public sealed record FolderClassificationTaskSnapshot(FolderClassificationTaskFile[] Files)
{
    public int Completed => Files.Count(file => file.Result is not null && !file.Pending && file.Error is null);
    public int Failed => Files.Count(file => file.Error is not null);
}

public sealed record FolderRegroupState(int Ordinal, IReadOnlyDictionary<string, int> Added, DateTime UtcAt);

public sealed partial class Job
{
    public DateTime? ClassificationStartedUtc { get; set; }
    public DateTime? ClassificationFinishedUtc { get; set; }
    [JsonIgnore] public TimeSpan ClassificationElapsed => ClassificationStartedUtc is { } start
        ? (ClassificationFinishedUtc ?? DateTime.UtcNow) - start : TimeSpan.Zero;
    internal object ClassificationSync { get; } = new();
    internal SemaphoreSlim ClassificationWriteGate { get; } = new(1, 1);
    private FolderRegroupState? _classificationRegroup;
    [JsonIgnore]
    public FolderRegroupState? ClassificationRegroup
    {
        get => Volatile.Read(ref _classificationRegroup);
        set { Volatile.Write(ref _classificationRegroup, value); Raise(); }
    }
    private FolderClassificationTaskSnapshot? _classificationSnapshot;
    [JsonIgnore]
    public FolderClassificationTaskSnapshot? ClassificationSnapshot
    {
        get => Volatile.Read(ref _classificationSnapshot);
        set { Volatile.Write(ref _classificationSnapshot, value); Raise(); }
    }
    public void InitializeClassificationSnapshot(FolderClassificationTaskSnapshot snapshot)
    { if (Interlocked.CompareExchange(ref _classificationSnapshot, snapshot, null) is null) Raise(nameof(ClassificationSnapshot)); }
}

/// <summary>Per-file checkpoints keep large score vectors out of queue.json and survive a closed inspector.</summary>
public static class FolderClassificationTaskStore
{
    public static string Folder(Job job) => Path.Combine(Storage.DefaultRoot, "classification-tasks", job.Id.ToString("N"));
    private static string RecordPath(Job job, string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Path.Combine(Folder(job), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) + ".json");
    }

    public static async Task<FolderClassificationTaskSnapshot> LoadAsync(Job job, CancellationToken ct = default)
    {
        var files = new List<FolderClassificationTaskFile>();
        foreach (var path in job.Inputs.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = RecordPath(job, path);
                if (!File.Exists(record)) { files.Add(new(path)); continue; }
                await using var stream = File.OpenRead(record);
                var file = await JsonSerializer.DeserializeAsync<FolderClassificationTaskFile>(stream, cancellationToken: ct).ConfigureAwait(false);
                files.Add(file is not null && BatchRename.PathComparer.Equals(path, file.Path) ? file : new(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { files.Add(new(path, Error: error.Message)); }
        }
        return new(files.ToArray());
    }

    public static async Task SaveFileAsync(Job job, FolderClassificationTaskFile file, CancellationToken ct = default)
    {
        await job.ClassificationWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = job.ClassificationSnapshot?.Files.FirstOrDefault(value => BatchRename.PathComparer.Equals(value.Path, file.Path));
            if (file.Result is { } result && previous?.Result?.Decisions.Any(decision => decision.Manual) == true)
                file = file with { Result = FolderClassification.KeepManual(result, previous.Result) };
            await WriteFileAsync(job, file, ct).ConfigureAwait(false);
        }
        finally { job.ClassificationWriteGate.Release(); }
    }

    private static async Task WriteFileAsync(Job job, FolderClassificationTaskFile file, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder(job));
        var path = RecordPath(job, file.Path); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(stream, file, cancellationToken: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Publish worker results while retaining decisions confirmed since its last snapshot.</summary>
    internal static void Publish(Job job, FolderClassificationTaskFile[] files)
    {
        lock (job.ClassificationSync)
        {
            var previous = job.ClassificationSnapshot?.Files.ToDictionary(file => file.Path, BatchRename.PathComparer);
            job.ClassificationSnapshot = new(files.Select(file =>
            {
                var old = previous?.GetValueOrDefault(file.Path)?.Result;
                return file.Result is { } result && !ReferenceEquals(result, old) && old?.Decisions.Any(decision => decision.Manual) == true
                    ? file with { Result = FolderClassification.KeepManual(result, old) } : file;
            }).ToArray());
        }
    }

    public static async Task<FolderClassificationTaskFile> ResolveAsync(Job job, string path, string ruleId, string categoryId, CancellationToken ct = default)
    {
        await job.ClassificationWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var spec = job.Options.FolderClassification ?? throw new ArgumentException("不是分类任务。");
            var snapshot = job.ClassificationSnapshot ?? await LoadAsync(job, ct).ConfigureAwait(false);
            var file = snapshot.Files.FirstOrDefault(value => BatchRename.PathComparer.Equals(value.Path, path)) ?? throw new ArgumentException("文件不属于此任务。");
            var rule = spec.Rules.FirstOrDefault(value => value.Id == ruleId) ?? throw new ArgumentException("分类规则不存在。");
            var category = FolderOutfitClassification.Categories(rule, snapshot.Files.Select(value => value.Result).OfType<FolderClassifiedFile>())
                .FirstOrDefault(value => value.Id == categoryId) ?? throw new ArgumentException("分类类别不存在。");
            while (true)
            {
                var resolved = FolderClassificationInsights.ResolvePending(file, ruleId, categoryId, category.Name);
                await WriteFileAsync(job, resolved, ct).ConfigureAwait(false);
                lock (job.ClassificationSync)
                {
                    var latest = job.ClassificationSnapshot ?? snapshot;
                    var current = latest.Files.FirstOrDefault(value => BatchRename.PathComparer.Equals(value.Path, path)) ?? file;
                    // Retry with fresher inference data rather than publishing a checkpoint different from the saved file.
                    if (!ReferenceEquals(current.Result, file.Result) || current.Pending != file.Pending || current.Error != file.Error)
                    { file = current; continue; }
                    job.ClassificationSnapshot = new(latest.Files.Select(value => BatchRename.PathComparer.Equals(value.Path, path) ? resolved : value).ToArray());
                    return resolved;
                }
            }
        }
        finally { job.ClassificationWriteGate.Release(); }
    }

    private static string RegroupPath(Job job) => Path.Combine(Folder(job), "regroup.json");

    /// <summary>Last regroup ordinal and per-group additions, so a reopened detail window shows the same "+N" state.</summary>
    public static void SaveRegroup(Job job, FolderRegroupState state)
    {
        try
        {
            Directory.CreateDirectory(Folder(job));
            var temporary = RegroupPath(job) + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(state));
            File.Move(temporary, RegroupPath(job), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static FolderRegroupState? LoadRegroup(Job job)
    {
        try { return File.Exists(RegroupPath(job)) ? System.Text.Json.JsonSerializer.Deserialize<FolderRegroupState>(File.ReadAllText(RegroupPath(job))) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException) { return null; }
    }

    public static void Delete(Job job)
    { if (Directory.Exists(Folder(job))) Directory.Delete(Folder(job), true); }
}
