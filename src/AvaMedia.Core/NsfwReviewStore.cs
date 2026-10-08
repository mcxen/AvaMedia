using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record NsfwReviewNote(string Path, long Length, DateTime LastWriteUtc, NsfwReviewDecision Decision, DateTime UpdatedUtc);

/// <summary>Human decisions only; a changed source identity does not inherit an old decision.</summary>
public sealed class NsfwReviewStore
{
    private readonly object _gate = new();
    private readonly string _path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AvaMedia", "nsfw-reviews.json");
    private Dictionary<string, NsfwReviewNote>? _notes;

    public NsfwReviewNote? Find(MediaTagResult result)
    {
        lock (_gate) return Notes().GetValueOrDefault(Key(result.Path, result.Length, result.LastWriteUtc));
    }

    public void Save(MediaTagResult result, NsfwReviewDecision decision, DateTime updatedUtc)
    {
        if (!Enum.IsDefined(decision)) throw new ArgumentException("审核结论无效。");
        MediaTagService.ValidateSource(result);
        lock (_gate)
        {
            var notes = Notes(); var key = Key(result.Path, result.Length, result.LastWriteUtc);
            if (notes.TryGetValue(key, out var current) && current.UpdatedUtc > updatedUtc) return;
            var updated = new Dictionary<string, NsfwReviewNote>(notes)
            { [key] = new(result.Path, result.Length, result.LastWriteUtc, decision, updatedUtc) };
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, updated); stream.Flush(flushToDisk: true); }
                File.Move(temporary, _path, true); _notes = updated;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private Dictionary<string, NsfwReviewNote> Notes()
    {
        if (_notes is not null) return _notes;
        if (!File.Exists(_path)) return _notes = new();
        var notes = JsonSerializer.Deserialize<Dictionary<string, NsfwReviewNote>>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("审核记录无效。");
        if (notes.Any(pair => pair.Value is null || !Enum.IsDefined(pair.Value.Decision)
            || pair.Key != Key(pair.Value.Path, pair.Value.Length, pair.Value.LastWriteUtc)))
            throw new InvalidDataException("审核记录无效。");
        return _notes = notes;
    }

    private static string Key(string path, long length, DateTime modified) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path) + "\0" + length + "\0" + modified.Ticks)));
}
