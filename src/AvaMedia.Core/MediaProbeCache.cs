using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

/// <summary>Bounded, process-wide source metadata; track selection stays with each caller.</summary>
internal static class MediaProbeCache
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Results = [];
    private static readonly SemaphoreSlim[] Sources = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim Workers = new(4, 4);
    private static int _bytes;

    public static async Task<string> ReadAsync(string path, string tool, Func<Task<string>> read, CancellationToken ct)
    {
        async Task<string?> Identity() => await Task.Run(() =>
        {
            var source = new FileInfo(Path.GetFullPath(path));
            if (!source.Exists) return null;
            var full = OperatingSystem.IsWindows() ? source.FullName.ToUpperInvariant() : source.FullName;
            return System.Text.Json.JsonSerializer.Serialize(new { Path = full, source.Length, Modified = source.LastWriteTimeUtc.Ticks, Tool = tool });
        }, ct).ConfigureAwait(false);
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        var identity = await Identity().ConfigureAwait(false);
        if (identity is null) return await read().ConfigureAwait(false);
        var slot = Sources[SHA256.HashData(Encoding.UTF8.GetBytes(identity))[0] % Sources.Length];
        await slot.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var boundary = JobExecutionControl.DeferPause();
            string? result;
            lock (Gate) Results.TryGetValue(identity, out result);
            if (result is null)
            {
                await Workers.WaitAsync(ct).ConfigureAwait(false);
                try { result = await read().ConfigureAwait(false); }
                finally { Workers.Release(); }
            }
            if (identity != await Identity().ConfigureAwait(false)) throw new IOException("源文件在读取媒体信息时发生变化。");
            ct.ThrowIfCancellationRequested();
            if (result.Length <= 1024 * 1024)
                lock (Gate)
                {
                    if (!Results.ContainsKey(identity))
                    {
                        while (Results.Count >= 128 || _bytes + result.Length * 2 > MaximumBytes)
                        {
                            var oldest = Results.First();
                            _bytes -= oldest.Value.Length * 2; Results.Remove(oldest.Key);
                        }
                        Results.Add(identity, result); _bytes += result.Length * 2;
                    }
                }
            return result;
        }
        finally { slot.Release(); }
    }
}
