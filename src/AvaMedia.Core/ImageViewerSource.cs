using LibArchive.Net;

namespace AvaMedia.Core;

public sealed record ImageViewerEntry(string Container, string? Member = null, long Bytes = 0)
{
    public string Name => Member ?? Path.GetFileName(Container);
    public string Identity => Container + "\n" + Member;
    public bool InArchive => Member is not null;
    public override string ToString() => Name;
}

/// <summary>Archive members are read into bounded streams; their paths are never extracted.</summary>
public static class ImageViewerSource
{
    public static bool IsArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is ".zip" or ".rar" or ".7z"
        or ".tar" or ".lzh" or ".lha" or ".cbr" or ".cbz" or ".cb7" or ".cbt" or ".gz" or ".bz2";
    public static bool Supports(string path) => ImageFormats.Supports(path) || IsArchive(path);
    public static Task<ImageViewerEntry[]> ListAsync(IEnumerable<string> inputs, bool recursive, CancellationToken token) => Task.Run(() =>
    {
        var entries = new List<ImageViewerEntry>();
        foreach (var path in inputs.Select(Path.GetFullPath).Distinct(BatchRename.PathComparer))
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Path.GetFileName(file).StartsWith("._", StringComparison.Ordinal) && ImageFormats.Supports(file))
                        entries.Add(new(file, Bytes: new FileInfo(file).Length));
                }
            }
            else if (IsArchive(path))
            {
                using var archive = new LibArchiveReader(path);
                foreach (var entry in archive.Entries())
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.IsRegularFile && entry.Name is { } name && ImageFormats.Extensions.Contains(Path.GetExtension(name).TrimStart('.')))
                        entries.Add(new(path, name, entry.LengthBytes ?? 0));
                }
            }
            else if (File.Exists(path) && ImageFormats.Supports(path)) entries.Add(new(path, Bytes: new FileInfo(path).Length));
        }
        return entries.DistinctBy(entry => entry.Identity, BatchRename.PathComparer).OrderBy(entry => entry.Name, NaturalNames.Instance).ToArray();
    }, token);

    public static async Task<byte[]> ReadAsync(ImageViewerEntry entry, CancellationToken token)
    {
        const long maximum = 256L * 1024 * 1024;
        if (entry.Bytes > maximum) throw new InvalidDataException("图片文件超过 256 MB，请先缩小或转换。");
        if (!entry.InArchive)
        {
            if (new FileInfo(entry.Container).Length > maximum) throw new InvalidDataException("图片文件超过 256 MB。");
            return await File.ReadAllBytesAsync(entry.Container, token).ConfigureAwait(false);
        }
        return await Task.Run(async () =>
        {
            using var archive = new LibArchiveReader(entry.Container);
            foreach (var member in archive.Entries())
            {
                token.ThrowIfCancellationRequested();
                if (member.Name != entry.Member) continue;
                if (member.LengthBytes > maximum) throw new InvalidDataException("压缩包中的图片超过 256 MB。");
                using var input = member.Stream; using var output = new MemoryStream();
                var buffer = new byte[81920]; int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > maximum) throw new InvalidDataException("压缩包中的图片超过 256 MB。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                return output.ToArray();
            }
            throw new FileNotFoundException("压缩包中的图片已不存在。", entry.Name);
        }, token).ConfigureAwait(false);
    }

    private sealed class NaturalNames : IComparer<string>
    {
        public static readonly NaturalNames Instance = new();
        public int Compare(string? left, string? right)
        {
            left ??= ""; right ??= ""; var a = 0; var b = 0;
            while (a < left.Length && b < right.Length)
            {
                if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
                {
                    var startA = a; var startB = b;
                    while (a < left.Length && char.IsAsciiDigit(left[a])) a++;
                    while (b < right.Length && char.IsAsciiDigit(right[b])) b++;
                    var x = left.AsSpan(startA, a - startA).TrimStart('0'); var y = right.AsSpan(startB, b - startB).TrimStart('0');
                    var numeric = x.Length.CompareTo(y.Length); if (numeric == 0) numeric = x.SequenceCompareTo(y);
                    if (numeric != 0) return numeric;
                }
                else
                {
                    var order = char.ToUpperInvariant(left[a++]).CompareTo(char.ToUpperInvariant(right[b++]));
                    if (order != 0) return order;
                }
            }
            return (left.Length - a).CompareTo(right.Length - b);
        }
    }
}
