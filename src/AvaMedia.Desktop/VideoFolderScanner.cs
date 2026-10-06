namespace AvaMedia.Desktop;

public interface IVideoFolderScanner
{
    Task<IReadOnlyList<string>> ScanAsync(string directory, CancellationToken token);
}

public sealed class VideoFolderScanner : IVideoFolderScanner
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg",
        ".ts", ".mts", ".m2ts", ".vob", ".ogv", ".3gp", ".3g2", ".asf", ".rm", ".rmvb", ".divx", ".f4v", ".mxf"
    };

    internal static bool IsVideoFile(string path) => Extensions.Contains(Path.GetExtension(path));

    public Task<IReadOnlyList<string>> ScanAsync(string directory, CancellationToken token) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var videos = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            token.ThrowIfCancellationRequested();
            if (IsVideoFile(file)) videos.Add(Path.GetFullPath(file));
        }
        videos.Sort((left, right) => CompareNames(Path.GetFileName(left), Path.GetFileName(right)));
        token.ThrowIfCancellationRequested();
        return videos;
    }, token);

    // Numeric runs make episode 2 precede episode 10 without parsing or overflowing integers.
    private static int CompareNames(string left, string right)
    {
        var a = 0; var b = 0;
        while (a < left.Length && b < right.Length)
        {
            if (char.IsAsciiDigit(left[a]) && char.IsAsciiDigit(right[b]))
            {
                var startA = a; var startB = b;
                while (a < left.Length && char.IsAsciiDigit(left[a])) a++;
                while (b < right.Length && char.IsAsciiDigit(right[b])) b++;
                while (startA < a - 1 && left[startA] == '0') startA++;
                while (startB < b - 1 && right[startB] == '0') startB++;
                var length = (a - startA).CompareTo(b - startB);
                if (length != 0) return length;
                var number = left.AsSpan(startA, a - startA).SequenceCompareTo(right.AsSpan(startB, b - startB));
                if (number != 0) return number;
            }
            else
            {
                var letter = char.ToUpperInvariant(left[a++]).CompareTo(char.ToUpperInvariant(right[b++]));
                if (letter != 0) return letter;
            }
        }
        var remainder = (left.Length - a).CompareTo(right.Length - b);
        return remainder != 0 ? remainder : StringComparer.Ordinal.Compare(left, right);
    }
}
