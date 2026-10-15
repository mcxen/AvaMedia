using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>A scored detection sample and geometry normalized to its unpadded thumbnail.</summary>
public sealed record PersonClipReplayFrame(PersonClipSample Sample, string ThumbnailPath,
    IReadOnlyList<PersonClipDetectionBox> Boxes);

/// <summary>Task-owned JPEGs and an incrementally flushed manifest, independent of the source video.</summary>
internal sealed class PersonClipReplayArchive : IDisposable
{
    private readonly StreamWriter _writer;
    private int _count;
    internal string ManifestPath { get; }

    internal PersonClipReplayArchive(Job job)
    {
        var folder = Path.Combine(Path.GetDirectoryName(AiTaskResults.PathFor(job, "people"))!,
            "people-frames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        ManifestPath = Path.Combine(folder, "frames.jsonl");
        _writer = new(new FileStream(ManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    }

    internal async Task AppendAsync(PersonClipSample sample, byte[] rgb, int size,
        (int Left, int Top, int Width, int Height) content, IReadOnlyList<PersonDetectionEvidence> evidence, CancellationToken ct)
    {
        var boxes = evidence.SelectMany(item => item.Boxes).Distinct().ToArray();
        await WriteAsync(sample, PersonClipPreview.EncodeJpeg(rgb, size, content), boxes, ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(PersonClipSample sample, byte[] jpeg, IReadOnlyList<PersonClipDetectionBox> boxes, CancellationToken ct)
    {
        var name = (++_count).ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".jpg";
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(ManifestPath)!, name), jpeg, ct).ConfigureAwait(false);
        // Publish metadata only after its image exists; every completed sample survives a window/app reopen.
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new PersonClipReplayFrame(sample, name, boxes)).AsMemory(), ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    internal async Task ImportAsync(IEnumerable<PersonClipReplayFrame> frames, CancellationToken ct)
    {
        foreach (var frame in frames)
            await WriteAsync(frame.Sample, await File.ReadAllBytesAsync(frame.ThumbnailPath, ct).ConfigureAwait(false), frame.Boxes, ct).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<PersonClipReplayFrame>> ReadAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return [];
        var frames = new List<PersonClipReplayFrame>();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            try
            {
                var frame = JsonSerializer.Deserialize<PersonClipReplayFrame>(line);
                if (frame is null || frame.Boxes is null || !double.IsFinite(frame.Sample.Seconds) || frame.Sample.Seconds < 0
                    || !float.IsFinite(frame.Sample.Score) || frame.Sample.Score is < 0 or > 1
                    || string.IsNullOrEmpty(frame.ThumbnailPath) || Path.GetFileName(frame.ThumbnailPath) != frame.ThumbnailPath) continue;
                frames.Add(frame with { ThumbnailPath = Path.Combine(Path.GetDirectoryName(path)!, frame.ThumbnailPath) });
            }
            catch (JsonException) { } // An interrupted append may leave an incomplete last line.
        }
        return frames.OrderBy(frame => frame.Sample.Seconds).ToArray();
    }

    public void Dispose() => _writer.Dispose();
}
