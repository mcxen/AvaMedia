namespace AvaMedia.Core;

public sealed record QuickClipInput(string Path, ConversionOptions Options);

/// <summary>Per-file drafts for the quick clipping dialog. Creating jobs never modifies input media.</summary>
public static class QuickClipBatch
{
    public static string[] Presets { get; } = ["Fast Copy", "MP4", "MKV"];
    public static IReadOnlySet<string> VideoExtensions => VideoFormats.InputExtensions;

    public static ConversionOptions ResolveOptions(string path, string preset, ConversionOptions draft)
    {
        if (!Presets.Contains(preset)) throw new ArgumentException("请选择 Fast Copy、MP4 或 MKV。");
        var options = draft.Clone();
        options.CopyStreams = preset == "Fast Copy";
        options.Format = options.CopyStreams ? System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant() : preset.ToLowerInvariant();
        if (options.CopyStreams && !VideoFormats.OriginalOutputExtensions.Contains(options.Format)) throw new ArgumentException("Fast Copy 不支持此文件的容器，请选择 MP4 或 MKV。");
        return options;
    }

    public static IReadOnlyList<ConversionOptions> Split(ConversionOptions draft, double duration, int parts)
        => ClipSplit.Create(draft, duration, new(Parts: parts));

    public static IReadOnlyList<Job> CreateJobs(IEnumerable<QuickClipInput> inputs, string outputFolder,
        bool outputToSource = false, string settingName = "", IEnumerable<string>? reserved = null)
    {
        var items = inputs.ToArray();
        if (items.Length == 0) throw new ArgumentException("请添加文件。");
        // Validate the complete draft before allocating any output directories.
        foreach (var item in items)
            MediaEngine.Validate(new Job { FeatureId = "clip", Inputs = [item.Path], Options = item.Options,
                Output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-validation-" + Guid.NewGuid() + "." + item.Options.Format) });
        var used = new HashSet<string>(reserved ?? [], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var jobs = new List<Job>();
        foreach (var item in items)
        {
            var folder = outputToSource ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(item.Path))! : System.IO.Path.GetFullPath(outputFolder);
            var name = System.IO.Path.GetFileNameWithoutExtension(item.Path) + (string.IsNullOrWhiteSpace(settingName) ? "" : " [" + settingName.Trim() + "]");
            var job = new Job { FeatureId = "clip", Inputs = [item.Path], Options = item.Options.Clone(),
                Output = MediaEngine.UniqueOutput(folder, name, item.Options.Format, used) };
            MediaEngine.Validate(job);
            used.Add(job.Output);
            jobs.Add(job);
        }
        return jobs;
    }
}
