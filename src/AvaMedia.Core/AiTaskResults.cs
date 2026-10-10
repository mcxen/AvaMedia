using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed record PersonDetectionTaskResult(PersonClipResult Result, long SourceLength, DateTime SourceWriteUtc);
public sealed record SubtitleTaskResult(SubtitleCue[] Cues, long SourceLength, DateTime SourceWriteUtc);

public static class AiTaskResults
{
    public static string PathFor(Job job, string kind, string extension = "json") =>
        Path.Combine(Storage.DefaultRoot, "ai-task-results", job.Id.ToString("N"), kind + "." + extension);
    public static string InternalOutputFor(Job job) => job.Options.Orientation is not null ? PathFor(job, "orientation")
        : job.Options.PersonClip?.AnalysisOnly == true ? PathFor(job, "people")
        : job.Options.Transcription?.RecognitionOnly == true ? PathFor(job, "draft", "srt") : PathFor(job, "tags");

    public static async Task SaveAsync<T>(string path, T result, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, result, cancellationToken: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<T?> LoadAsync<T>(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public static void DeleteResult(Job job, string kind)
    {
        var path = PathFor(job, kind);
        if (File.Exists(path)) File.Delete(path);
    }

    public static void ClearForRetry(Job job)
    {
        var folder = Path.GetDirectoryName(PathFor(job, "tags"))!;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}

public partial class Job
{
    private OrientationTaskResult? _orientationResult;
    [JsonIgnore] public OrientationTaskResult? OrientationResult { get => Volatile.Read(ref _orientationResult); set { Volatile.Write(ref _orientationResult, value); Raise(); } }
    private MediaTagResult? _mediaTagResult;
    private PersonDetectionTaskResult? _personDetectionResult;
    private SubtitleTaskResult? _subtitleResult;
    [JsonIgnore] public MediaTagResult? MediaTagResult { get => Volatile.Read(ref _mediaTagResult); set { Volatile.Write(ref _mediaTagResult, value); Raise(); } }
    [JsonIgnore] public PersonDetectionTaskResult? PersonDetectionResult { get => Volatile.Read(ref _personDetectionResult); set { Volatile.Write(ref _personDetectionResult, value); Raise(); } }
    [JsonIgnore] public SubtitleTaskResult? SubtitleResult { get => Volatile.Read(ref _subtitleResult); set { Volatile.Write(ref _subtitleResult, value); Raise(); } }
    [JsonIgnore] public bool HasInternalOutput => Options.Orientation is not null || Options.PersonClip?.AnalysisOnly == true || Options.Transcription?.RecognitionOnly == true || Options.MediaTag?.WriteTextReport == false;
}
