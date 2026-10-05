namespace AvaMedia.Core;

/// <summary>The execution contract used by the queue, independent of codecs and UI.</summary>
public interface IJobExecutor
{
    Task Execute(Job job, Action<double> progress, CancellationToken ct);
}

/// <summary>Replaceable media backend shared by editing, inspection and conversion.</summary>
public interface IMediaEngine : IMediaPreview, IJobExecutor
{
    AppSettings Settings { get; }
    string FFmpeg { get; }
    string FFprobe { get; }
    Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0);
}
