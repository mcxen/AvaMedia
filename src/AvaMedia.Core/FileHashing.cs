using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace AvaMedia.Core;

public static class FileHashing
{
    public static Task<IReadOnlyList<string>> Sha256Async(IReadOnlyList<string> paths, Action<double> progress, CancellationToken token = default)
    {
        var snapshot = paths.ToArray();
        return Task.Run(() => ComputeAsync(snapshot, progress, token), token);
    }
    private static async Task<IReadOnlyList<string>> ComputeAsync(string[] paths, Action<double> progress, CancellationToken token)
    {
        var sizes = paths.Select(path => { token.ThrowIfCancellationRequested(); return new FileInfo(path).Length; }).ToArray();
        var total = sizes.Sum();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var result = new List<string>(paths.Length);
        var feedback = Stopwatch.StartNew();
        long completed = 0;
        try
        {
            progress(0);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                await using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
                    BufferSize = 1, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(0, 1024 * 1024), token).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, count); completed += count;
                    if (feedback.ElapsedMilliseconds >= 100)
                    { progress(total > 0 ? Math.Min(99.9, completed * 100d / total) : 0); feedback.Restart(); }
                }
                result.Add(Convert.ToHexString(hash.GetHashAndReset()) + "  " + Path.GetFileName(path));
            }
            token.ThrowIfCancellationRequested(); progress(100);
            return result;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
