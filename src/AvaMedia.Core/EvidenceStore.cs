using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record EvidenceLocation(string File, long Offset, int Length);
/// <summary>Bounded JSONL shards with byte offsets; readers fetch one original citation without loading transcripts.</summary>
public sealed class EvidenceStore(string folder)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(folder);
    public async Task WriteSegmentAsync(SegmentJob job, CancellationToken ct)
    {
        var evidence = job.Transcript.Where(c => !c.ContextOnly).Select(c => new SummaryEvidence(c.Id, c.Start, c.End, c.Text, c.Source is "Whisper" or "speech_transcript" ? "speech_transcript" : "transcript", c.Track, c.Language) { Source = c.Source })
            .Concat(job.Frames.Select(f => new SummaryEvidence(f.Id, f.T, f.End ?? f.T, f.Caption + (f.Ocr.Length > 0 ? "\nOCR: " + f.Ocr : ""), f.Kind) { FrameIds = f.FrameIds, Source = "video_frames" })).ToArray();
        await _gate.WaitAsync(ct);
        try
        {
            var index = await IndexAsync(ct); var shard = 0; var stream = new MemoryStream(); var offsets = new Dictionary<string, EvidenceLocation>();
            async Task Flush()
            {
                if (stream.Length == 0) return;
                var relative = $"evidence/{job.SegmentId}-{shard:000}.jsonl";
                await SummaryCheckpointStore.AtomicWriteAsync(Path.Combine(_root, relative), stream.ToArray(), ct);
                foreach (var pair in offsets) index[pair.Key] = pair.Value;
                offsets.Clear(); stream.SetLength(0); stream.Position = 0; shard++;
            }
            foreach (var item in evidence)
            {
                var line = Encoding.UTF8.GetBytes(LongSummaryJson.Write(item) + "\n");
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("单条证据超过 4 MB。");
                if (stream.Length > 0 && stream.Length + line.Length > 1024 * 1024) await Flush();
                offsets[item.Id] = new($"evidence/{job.SegmentId}-{shard:000}.jsonl", stream.Position, line.Length);
                await stream.WriteAsync(line, ct);
            }
            await Flush(); stream.Dispose();
            await SummaryCheckpointStore.AtomicWriteAsync(Path.Combine(_root, "evidence/index.json"), JsonSerializer.SerializeToUtf8Bytes(index, LongSummaryJson.Options), ct);
        }
        finally { _gate.Release(); }
    }
    private async Task<Dictionary<string, EvidenceLocation>> IndexAsync(CancellationToken ct)
    {
        var path = Path.Combine(_root, "evidence/index.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, EvidenceLocation>>(await File.ReadAllTextAsync(path, ct), LongSummaryJson.Options) ?? [] : [];
    }
    public async Task<SummaryEvidence?> LoadAsync(string id, CancellationToken ct)
    {
        var index = await IndexAsync(ct); if (!index.TryGetValue(id, out var location)) return null;
        var path = Path.GetFullPath(Path.Combine(_root, location.File));
        if (!path.StartsWith(Path.Combine(_root, "evidence") + Path.DirectorySeparatorChar, StringComparison.Ordinal) || location.Length is < 1 or > 4 * 1024 * 1024 || location.Offset < 0) throw new InvalidDataException("证据索引无效。");
        await using var stream = File.OpenRead(path); stream.Seek(location.Offset, SeekOrigin.Begin);
        var bytes = new byte[location.Length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<SummaryEvidence>(bytes, LongSummaryJson.Options);
    }
}
