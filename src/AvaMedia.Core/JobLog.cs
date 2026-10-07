using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed partial class Job
{
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private JobLogStore? _logs;
    private string _logSummary = "";

    public string LogSummary { get => _logSummary; set => _logSummary = JobLogStore.Summarize(value); }

    // Full text is file-backed. Ordinary queue serialization never invokes this getter.
    [JsonIgnore]
    public string Log
    {
        get
        {
            _logGate.Wait();
            try { return (_logs ?? JobLogStore.Default).Read(Id) ?? LogSummary; }
            finally { _logGate.Release(); }
        }
        set
        {
            _logGate.Wait();
            try
            {
                (_logs ??= JobLogStore.Default).Write(Id, value);
                LogSummary = value;
            }
            finally { _logGate.Release(); }
        }
    }

    public async Task<string> ReadLogAsync(CancellationToken ct = default)
    {
        await _logGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await (_logs ?? JobLogStore.Default).ReadAsync(Id, ct).ConfigureAwait(false) ?? LogSummary; }
        finally { _logGate.Release(); }
    }

    public void AppendLog(string text)
    {
        if (text.Length == 0) return;
        _logGate.Wait();
        try
        {
            (_logs ??= JobLogStore.Default).Append(Id, text);
            LogSummary = text;
        }
        finally { _logGate.Release(); }
    }

    internal void AttachLogs(JobLogStore logs)
    {
        _logGate.Wait();
        try
        {
            if (_logs is { } current && !current.SameDirectory(logs)) logs.CopyFrom(Id, current);
            _logs = logs;
        }
        finally { _logGate.Release(); }
    }
}

internal sealed class JobLogStore(string root)
{
    internal static JobLogStore Default { get; } = new(Storage.DefaultRoot);
    private readonly string _directory = Path.GetFullPath(Path.Combine(root, "logs"));
    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".log");
    internal bool SameDirectory(JobLogStore other) => string.Equals(_directory, other._directory,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal string? Read(Guid id)
    {
        try { return File.ReadAllText(PathFor(id)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal async Task<string?> ReadAsync(Guid id, CancellationToken ct)
    {
        try { return await File.ReadAllTextAsync(PathFor(id), ct).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void Write(Guid id, string text)
    {
        var path = PathFor(id);
        if (text.Length == 0)
        {
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { }
            return;
        }
        Directory.CreateDirectory(_directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void CopyFrom(Guid id, JobLogStore source)
    {
        var sourcePath = source.PathFor(id);
        if (!File.Exists(sourcePath)) return;
        Directory.CreateDirectory(_directory);
        var path = PathFor(id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(sourcePath, temporary); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void Append(Guid id, string text)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path)) File.Copy(path, temporary);
            File.AppendAllText(temporary, "\n" + text);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Summarize(string text)
    {
        var line = text.AsSpan().Trim();
        line = line[(line.LastIndexOf('\n') + 1)..].Trim();
        return line.Length <= 512 ? line.ToString() : string.Concat(line[..511], "…");
    }
}
