using System.Text.Json;

namespace AvaMedia.Core;
public sealed partial class Storage
{
    private readonly string _root;
    private readonly JobLogStore _logs;
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia");
    private static readonly JsonSerializerOptions Json = new() {WriteIndented=true};
    private readonly object _queueWrite = new();
    private long _queueVersion;
    public Storage(string? root = null) { _root = root ?? DefaultRoot; _logs = new(_root); Directory.CreateDirectory(_root); }
    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>("settings.json") ?? new();
        LoadOnlineAiKey(settings.OnlineAi);
        // Model downloads follow the saved source choice in every entry point (main window, standalone tools).
        try { ModelDownloadSources.Preference = ModelSourcePreference.From(settings); } catch (ArgumentException) { }
        MediaTagRuntime.Configure(settings);
        return settings;
    }
    public List<Job> LoadJobs()
    {
        var jobs=Read<List<Job>>("queue.json") ?? [];
        foreach (var job in jobs) job.AttachLogs(_logs);
        foreach(var j in jobs.Where(j=>j.State is JobState.Running or JobState.Stopping)) {j.State=JobState.Cancelled;j.Error="应用在任务完成前退出，可重试。";}
        return jobs;
    }
    public void SaveSettings(AppSettings s)
    {
        Write("settings.json", s); SaveOnlineAiKey(s.OnlineAi);
        MediaTagRuntime.Configure(s);
    }
    public void SaveJobs(IEnumerable<Job> jobs) => WriteJobs(PrepareJobs(jobs), Interlocked.Increment(ref _queueVersion));
    public Task SaveJobsAsync(IEnumerable<Job> jobs)
    {
        var snapshot = PrepareJobs(jobs);
        var version = Interlocked.Increment(ref _queueVersion);
        return Task.Run(() => WriteJobs(snapshot, version));
    }
    private void WriteJobs(Job[] snapshot, long version)
    {
        if (version != Volatile.Read(ref _queueVersion)) return;
        lock (_queueWrite)
        {
            // An older progress snapshot must never replace a newer edit or clear action.
            if (version != Volatile.Read(ref _queueVersion)) return;
            var path = Path.Combine(_root, "queue.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, snapshot, Json);
                if (version == Volatile.Read(ref _queueVersion)) File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private Job[] PrepareJobs(IEnumerable<Job> jobs)
    {
        var snapshot = jobs.ToArray();
        foreach (var job in snapshot) job.AttachLogs(_logs);
        return snapshot;
    }

    public Task DeleteJobLogsAsync(IEnumerable<Job> jobs)
    {
        var snapshot = jobs.ToArray();
        return Task.Run(() => { foreach (var job in snapshot) job.Log = ""; });
    }

    public async Task ExportJobsAsync(string path, IEnumerable<Job> jobs, CancellationToken ct = default, bool includeNsfw = false)
    {
        var snapshot = jobs.ToArray();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
                writer.WriteStartArray();
                foreach (var job in snapshot)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    foreach (var property in JsonSerializer.SerializeToElement(job, Json).EnumerateObject())
                    {
                        if (!includeNsfw && property.Name == nameof(Job.Options) && job.Options.FolderClassification is { } classification)
                        {
                            var options = job.Options.Clone(); var safe = classification.Clone();
                            safe.Rules = safe.Rules.Where(rule => !MediaPrivacy.IsSensitiveRule(rule)).ToArray();
                            safe.IncludeNsfw = false;
                            safe.Analysis = safe.Analysis with { SemanticCandidates = safe.Rules.SelectMany(rule => rule.Candidates()).ToArray() };
                            options.FolderClassification = safe;
                            writer.WritePropertyName(nameof(Job.Options)); JsonSerializer.Serialize(writer, options, Json);
                        }
                        else property.WriteTo(writer);
                    }
                    writer.WriteString(nameof(Job.Log), await job.ReadLogAsync(ct).ConfigureAwait(false));
                    writer.WriteEndObject();
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
                writer.WriteEndArray();
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<List<Job>> ImportJobsAsync(string path, CancellationToken ct = default)
    {
        var jobs = new List<Job>();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream, Json, ct).ConfigureAwait(false))
            {
                var job = entry.Deserialize<Job>(Json) ?? throw new InvalidDataException("任务列表格式无效。");
                Catalog.Find(job.FeatureId);
                if (job.Inputs is null || job.Options is null || string.IsNullOrWhiteSpace(job.Output))
                    throw new InvalidDataException("任务列表格式无效。");
                job.Id = Guid.NewGuid();
                if (job.FeatureId == "folder-classification")
                {
                    job.Output = FolderClassificationTaskStore.Folder(job); job.State = JobState.Waiting;
                    if (job.Options.FolderClassification is { } classification) classification.LastJournal = null;
                }
                job.AttachLogs(_logs);
                if (job.State is JobState.Running or JobState.Stopping) job.State = JobState.Cancelled;
                jobs.Add(job);
                if (entry.TryGetProperty(nameof(Job.Log), out var log))
                {
                    if (log.ValueKind != JsonValueKind.String) throw new InvalidDataException("任务列表格式无效。");
                    var text = log.GetString()!;
                    await Task.Run(() => job.Log = text, ct).ConfigureAwait(false);
                }
            }
            return jobs;
        }
        catch
        {
            await DeleteJobLogsAsync(jobs).ConfigureAwait(false);
            throw;
        }
    }
    public Dictionary<string,ConversionOptions> LoadPresets() => Read<Dictionary<string,ConversionOptions>>("presets.json")??[];
    public void SavePreset(string key,ConversionOptions options){var presets=LoadPresets();presets[key]=options.Clone();Write("presets.json",presets);}
    private T? Read<T>(string name)
    { try {using var stream = File.OpenRead(Path.Combine(_root,name));return JsonSerializer.Deserialize<T>(stream);} catch(IOException) {return default;} catch(JsonException) {return default;} }
    private void Write<T>(string name,T value)
    {
        var path=Path.Combine(_root,name); var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(value,Json)); File.Move(temp,path,true);
    }
}
