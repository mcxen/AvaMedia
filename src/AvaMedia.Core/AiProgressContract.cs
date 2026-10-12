using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

public sealed record SpeechSegmentProgress(int Index, double StartSeconds, double EndSeconds, string State,
    double RecognizedSeconds, int VoiceIntervalCount, int CueCount);
public sealed record SummarySegmentProgress(string SegmentId, double Start, double End, string State, string Summary);
public sealed record VoiceInterval(double StartSeconds, double EndSeconds);
public sealed record SpeechProgressSnapshot(double DurationSeconds, double SegmentSeconds, int CurrentSegment,
    SpeechSegmentProgress[] Segments, VoiceInterval[] VoiceIntervals, int CueCount, string SubStage);
public sealed record AudioEnvelope(double DurationSeconds, int BucketCount, float[] Peaks);
public sealed record AiQueueSummary(int SubtitleJobsWaiting, int SubtitleJobsRunning);
public sealed record AiStageNodeDto(int Index, string Title, string State, double? ElapsedSeconds,
    string[] SubSteps, string Detail);
public sealed record AiTaskProgressDto(string JobId, string Stage, string Detail, double Percent, string Backend,
    int CurrentNode, AiStageNodeDto[] Nodes, string[] RecentResults, string[] RecentStages, bool HasPreview,
    SpeechProgressSnapshot? Speech, AiQueueSummary Queue)
{
    public SummarySegmentProgress[]? SummarySegments { get; init; }
}

public static class AiProgressStore
{
    private sealed class Entry
    {
        public SpeechProgressSnapshot? Snapshot;
        public bool Pending;
        public bool Publishing;
    }

    private static readonly ConcurrentDictionary<string, Entry> Speech = new(StringComparer.Ordinal);
    public static event Action<string>? SpeechChanged;

    public static SpeechProgressSnapshot? GetSpeech(string jobId)
    {
        if (!Speech.TryGetValue(jobId, out var entry)) return null;
        lock (entry) return entry.Snapshot is { } snapshot ? Copy(snapshot) : null;
    }

    public static void SetSpeech(string jobId, SpeechProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var copy = Copy(snapshot);
        var entry = Speech.GetOrAdd(jobId, _ => new Entry());
        lock (entry)
        {
            entry.Snapshot = copy;
            entry.Pending = true;
            if (entry.Publishing) return;
            entry.Publishing = true;
            _ = Task.Run(() => PublishAsync(jobId, entry));
        }
    }

    private static SpeechProgressSnapshot Copy(SpeechProgressSnapshot snapshot) => snapshot with
    {
        Segments = snapshot.Segments.ToArray(), VoiceIntervals = snapshot.VoiceIntervals.ToArray()
    };

    private static async Task PublishAsync(string jobId, Entry entry)
    {
        while (true)
        {
            lock (entry)
            {
                if (!entry.Pending) { entry.Publishing = false; return; }
                entry.Pending = false;
            }
            // A slow or failing subscriber must not block writers or strand the final update.
            if (SpeechChanged is { } handlers)
                foreach (Action<string> handler in handlers.GetInvocationList())
                    try { handler(jobId); }
                    catch (Exception error) { Trace.TraceError("SpeechChanged: {0}", error); }
            // Coalesce updates, including a trailing notification for the latest snapshot.
            await Task.Delay(250).ConfigureAwait(false);
        }
    }
}

public static class AudioEnvelopeService
{
    private const int SampleRate = 8000;
    private const int CacheMagic = 0x41564531;

    public static Task<AudioEnvelope?> GetAsync(string mediaPath, int buckets = 2000, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        // Include path resolution and all disk access in the worker, even for cache hits.
        return Task.Run(() => LoadAsync(mediaPath, buckets, ct), ct);
    }

    private static async Task<AudioEnvelope?> LoadAsync(string mediaPath, int buckets, CancellationToken ct)
    {
        string? pcmPath = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(Path.GetFullPath(mediaPath));
            if (!file.Exists) return null;
            var length = file.Length;
            var modified = file.LastWriteTimeUtc.Ticks;
            var identityPath = OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName;
            var identity = FormattableString.Invariant($"{identityPath.Length}:{identityPath}|{length}|{modified}");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            var cachePath = Path.Combine(Storage.DefaultRoot, "ai-task-results", "envelopes", hash + ".bin");
            if (ReadCache(cachePath, buckets, ct) is { } cached) return cached;

            pcmPath = Path.GetTempFileName();
            var result = await ProcessRunner.Run(MediaEngine.Resolve("", "ffmpeg"),
                ["-v", "error", "-nostdin", "-y", "-i", file.FullName, "-map", "0:a:0", "-vn",
                    "-ac", "1", "-ar", "8000", "-c:a", "pcm_s16le", "-f", "s16le", pcmPath],
                ct, maximumOutputChars: 8192).ConfigureAwait(false);
            if (result.ExitCode != 0) return null;

            var envelope = Compute(pcmPath, buckets, ct);
            if (envelope is null) return null;
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != modified) return null;
            WriteCache(cachePath, envelope, ct);
            ct.ThrowIfCancellationRequested();
            return envelope;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
        finally { DeleteTemporary(pcmPath); }
    }

    private static AudioEnvelope? Compute(string path, int buckets, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length == 0 || stream.Length % 2 != 0) return null;
        var samples = stream.Length / 2;
        var peaks = new float[buckets];
        var buffer = new byte[65536];
        long position = 0;
        // Decode to a temporary file so long recordings do not require a PCM-sized allocation.
        while (position < samples)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length / 2, samples - position);
            stream.ReadExactly(buffer.AsSpan(0, count * 2));
            for (var i = 0; i < count; i++, position++)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2, 2));
                var bucket = (int)((decimal)position * buckets / samples);
                peaks[bucket] = Math.Max(peaks[bucket], Math.Abs((int)sample) / 32768f);
            }
        }
        return new(samples / (double)SampleRate, buckets, peaks);
    }

    private static AudioEnvelope? ReadCache(string path, int buckets, CancellationToken ct)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length != 16L + buckets * 4L) return null;
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != CacheMagic) return null;
            var duration = reader.ReadDouble();
            if (!double.IsFinite(duration) || duration <= 0 || reader.ReadInt32() != buckets) return null;
            var peaks = new float[buckets];
            for (var i = 0; i < buckets; i++)
            {
                ct.ThrowIfCancellationRequested();
                var peak = reader.ReadSingle();
                if (!float.IsFinite(peak) || peak is < 0 or > 1) return null;
                peaks[i] = peak;
            }
            return new(duration, buckets, peaks);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteCache(string path, AudioEnvelope envelope, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CacheMagic);
                writer.Write(envelope.DurationSeconds);
                writer.Write(envelope.BucketCount);
                foreach (var peak in envelope.Peaks)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write(peak);
                }
            }
            ct.ThrowIfCancellationRequested();
            // Atomic replacement also keeps concurrent requests from observing partial data.
            // A different bucket count replaces this variant and is checked on every read.
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally { DeleteTemporary(temporary); }
    }

    private static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

public static class AiQueueInfo
{
    public static AiQueueSummary Summarize(IEnumerable<Job> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var waiting = 0;
        var running = 0;
        foreach (var job in jobs)
        {
            if (Catalog.Find(job.FeatureId).Operation != Operation.Transcribe) continue;
            var state = job.State;
            if (state == JobState.Waiting) waiting++;
            else if (state == JobState.Running)
            {
                if (job.Activity?.Stage == "等待语音识别") waiting++;
                else running++;
            }
        }
        return new(waiting, running);
    }
}
