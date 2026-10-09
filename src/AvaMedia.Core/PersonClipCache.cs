using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Freeze completed detection for unchanged source media, model code and detection settings.</summary>
internal sealed class PersonClipCache
{
    private readonly string _source, _file;
    private readonly long _bytes;
    private readonly DateTime _modified;
    public PersonClipCache(string source, PersonClipOptions options)
    {
        _source = Path.GetFullPath(source);
        var info = new FileInfo(_source); _bytes = info.Length; _modified = info.LastWriteTimeUtc;
        var canonical = options with { DetectorIds = options.SelectedDetectors.Order(StringComparer.Ordinal).ToArray(),
            ExcludedRanges = PersonClipExclusions.Normalize(options.ExcludedRanges) };
        var identity = JsonSerializer.Serialize(new { Source = _source, Bytes = _bytes, Modified = _modified,
            Code = typeof(PersonClipAnalysis).Module.ModuleVersionId, Options = canonical });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "person-clip-cache", key + ".json");
    }

    private bool Unchanged()
    {
        var info = new FileInfo(_source);
        return info.Exists && info.Length == _bytes && info.LastWriteTimeUtc == _modified;
    }

    public async Task<PersonClipResult?> ReadAsync(MediaInfo info, PersonClipOptions options, CancellationToken ct)
    {
        try
        {
            if (!Unchanged() || !File.Exists(_file) || new FileInfo(_file).Length > 8 * 1024 * 1024) return null;
            await using var stream = File.OpenRead(_file);
            var result = await JsonSerializer.DeserializeAsync<PersonClipResult>(stream, cancellationToken: ct).ConfigureAwait(false);
            if (result is null || result.Path != _source || result.Info is null || result.Segments is null
                || result.Info.Duration != info.Duration || result.Segments.Count > 16384) return null;
            var end = 0d;
            foreach (var range in result.Segments)
            {
                if (range is null || !double.IsFinite(range.Start) || !double.IsFinite(range.End)
                    || range.Start < end || range.End <= range.Start || range.End > info.Duration
                    || (options.ExcludedRanges ?? []).Any(mask => range.Start < mask.End && range.End > mask.Start)) return null;
                end = range.End;
            }
            return Unchanged() ? result with { Info = info, FromCache = true } : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task WriteAsync(PersonClipResult result, CancellationToken ct)
    {
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (!Unchanged()) return;
            var directory = Path.GetDirectoryName(_file)!;
            Directory.CreateDirectory(directory);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, result with { Path = _source, FromCache = false }, cancellationToken: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!Unchanged()) return;
            File.Move(temporary, _file, true);
            // Bound completed-result storage; no raw video frames or unfinished detections are persisted.
            foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(64))
                old.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
