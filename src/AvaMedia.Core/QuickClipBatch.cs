namespace AvaMedia.Core;

public sealed record QuickClipInput(string Path, ConversionOptions Options);

/// <summary>Per-file drafts for the quick clipping dialog. Creating jobs never modifies input media.</summary>
public static class QuickClipBatch
{
    public const string DefaultPreset = "Fast Copy";
    public static string[] Presets { get; } = ["Fast Copy", "MP4", "MKV", "TS"];
    public static string PresetLabel(string preset) => preset == "Fast Copy" ? "原格式 / 原码率" : preset;
    public static IReadOnlySet<string> VideoExtensions => VideoFormats.InputExtensions;

    public static ConversionOptions ResolveOptions(string path, string preset, ConversionOptions draft)
    {
        if (!Presets.Contains(preset)) throw new ArgumentException("请选择 Fast Copy、MP4、MKV 或 TS。");
        var options = draft.Clone();
        options.CopyStreams = preset == "Fast Copy";
        options.Format = options.CopyStreams ? System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant() : preset.ToLowerInvariant();
        if (options.CopyStreams && !VideoFormats.OriginalOutputExtensions.Contains(options.Format)) throw new ArgumentException("Fast Copy 不支持此文件的容器，请选择 MP4 或 MKV。");
        return options;
    }

    public static IReadOnlyList<Job> CreateJobs(IEnumerable<QuickClipInput> inputs, string outputFolder,
        bool outputToSource = false, string settingName = "", IEnumerable<string>? reserved = null)
    {
        var items = inputs.ToArray();
        if (items.Length == 0) throw new ArgumentException("请添加文件。");
        var jobs = items.Select(item => new Job { FeatureId = "clip", Inputs = [item.Path], Options = item.Options.Clone(),
            Output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AvaMedia-validation-" + Guid.NewGuid() + "." + item.Options.Format) }).ToArray();
        // Validate the cloned drafts once, before allocating any output directories.
        foreach (var job in jobs) MediaEngine.Validate(job);
        var used = new HashSet<string>(reserved ?? [], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in items) used.Add(System.IO.Path.GetFullPath(item.Path));
        foreach (var job in jobs)
        {
            var folder = outputToSource ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(job.Inputs[0]))! : System.IO.Path.GetFullPath(outputFolder);
            var name = System.IO.Path.GetFileNameWithoutExtension(job.Inputs[0]) + (string.IsNullOrWhiteSpace(settingName) ? "" : " [" + settingName.Trim() + "]");
            job.Output = MediaEngine.UniqueOutput(folder, name, job.Options.Format, used);
            used.Add(job.Output);
        }
        return jobs;
    }
}
