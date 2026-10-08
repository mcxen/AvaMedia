namespace AvaMedia.Core;

/// <summary>Shared defaults for all queue entry points; allocated paths never overwrite sources.</summary>
public static class OutputPreferences
{
    public static void Apply(IEnumerable<Job> jobs, AppSettings settings, IEnumerable<string>? reserved = null,
        bool? outputToSource = null, string? settingName = null)
    {
        var items = jobs.ToArray();
        var used = new HashSet<string>(reserved ?? [], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var input in items.SelectMany(j => j.Inputs).Where(File.Exists)) used.Add(Path.GetFullPath(input));
        foreach (var job in items)
        {
            var feature = Catalog.Find(job.FeatureId);
            var folder = Path.GetDirectoryName(Path.GetFullPath(job.Output))!;
            if ((outputToSource ?? settings.OutputToSource) && job.Inputs.FirstOrDefault() is {} source && File.Exists(source))
                folder = Path.GetDirectoryName(Path.GetFullPath(source))!;
            var directory = Catalog.DirectoryOutput(feature.Operation);
            var name = directory ? Path.GetFileName(job.Output) : Path.GetFileNameWithoutExtension(job.Output);
            var label = settingName ?? (settings.AddSettingName ? SettingLabel(job) : "");
            if (!string.IsNullOrWhiteSpace(label)) name += " [" + Clean(label) + "]";
            job.Output = MediaEngine.UniqueOutput(folder, name, job.Options.Format, used, directory);
            used.Add(job.Output);
        }
    }

    public static string SettingLabel(Job job) => job.Options.CopyStreams ? "FastCopy" :
        job.Options.Format.ToUpperInvariant() + (job.Options.VideoCodec is "自动" or "copy" ? "" : " " + job.Options.VideoCodec);

    private static string Clean(string text) => string.Concat(text.Trim().Select(c =>
        c is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c));
}

public sealed record QueueCompletion(int Completed, int Failed, int Cancelled, IReadOnlyList<string> OutputFolders)
{
    public bool Finished => Completed + Failed > 0 && Cancelled == 0;
    public bool AllSucceeded => Finished && Failed == 0;
    public static QueueCompletion From(IEnumerable<Job> jobs)
    {
        var batch = jobs.ToArray();
        var folders = batch.Where(j => j.State == JobState.Completed)
            .Select(j => Directory.Exists(j.Output) ? j.Output : Path.GetDirectoryName(Path.GetFullPath(j.Output))!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        // An interrupted queue also includes jobs that never acquired a concurrency slot.
        return new(batch.Count(j => j.State == JobState.Completed), batch.Count(j => j.State == JobState.Failed),
            batch.Count(j => j.State is JobState.Cancelled or JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping), folders);
    }
}
