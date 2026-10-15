using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private Task ConfigureLifecycleEntryAsync(Feature feature, string[]? files) => StartToolWorkflow(async () =>
    {
        var paths = files ?? (await ToolFilePicker.OpenFilesAsync(this, MultiStageEntry.Picker(feature)))
            .Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        if (_closing) return;
        paths = paths.Where(File.Exists).Where(path => MultiStageEntry.Accepts(feature.Id, path))
            .Select(Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray();
        if (paths.Length == 0) return;
        var jobs = MultiStageEntry.CreateJobs(feature, paths, _settings, _jobs.Select(job => job.Output));
        AddToolJobs(jobs, startImmediately: false);
        SelectTaskRows(jobs);
        foreach (var job in jobs) await ShowAiTaskAsync(job);
    });
}
