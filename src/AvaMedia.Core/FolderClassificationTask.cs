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
    public string? LastJournal { get; set; }

    public FolderClassificationTaskOptions Clone() => new()
    {
        Rules = Rules.Select(rule => rule with { Categories = rule.Categories.Select(category => category with
        { Tags = category.Tags.Select(tags => tags.ToArray()).ToArray(), SupersededBy = category.SupersededBy.ToArray() }).ToArray() }).ToArray(),
        Analysis = Analysis with { SemanticCandidates = Analysis.SemanticCandidates.ToArray() },
        TagThreshold = TagThreshold, IncludeNsfw = IncludeNsfw, AllowSemanticDownload = AllowSemanticDownload, OutputFolder = OutputFolder,
        SplitTypes = SplitTypes, WriteText = WriteText, Move = Move, ExcludedPaths = ExcludedPaths.ToArray(), LastJournal = LastJournal
    };
}

public sealed record FolderClassificationTaskFile(string Path, FolderClassifiedFile? Result = null, string? Error = null, bool Pending = true);
public sealed record FolderClassificationTaskSnapshot(FolderClassificationTaskFile[] Files)
{
    public int Completed => Files.Count(file => file.Result is not null && !file.Pending && file.Error is null);
    public int Failed => Files.Count(file => file.Error is not null);
}

public sealed partial class Job
{
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

    public static void Delete(Job job)
    { if (Directory.Exists(Folder(job))) Directory.Delete(Folder(job), true); }
}
