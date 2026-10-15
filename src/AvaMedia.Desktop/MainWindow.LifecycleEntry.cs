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

    private async Task<bool> PrepareLifecycleEntryAsync(Job job)
    {
        if (_mcpBackgroundMode || !MultiStageEntry.Supports(job.FeatureId) || !UsesMultiStageWindow(job)) return true;
        MediaEngine.Validate(job);
        var owner = _aiTaskWindows.GetValueOrDefault(job.Id) ?? this;
        var semantic = job.Options.PersonClip?.Detection.UseEmbedding == true
            || job.Options.MediaTag?.Analysis.NeedsSemanticModel == true
            || job.Options.FolderClassification?.Analysis.NeedsSemanticModel == true;
        if (semantic && !await SemanticModelConsent.IsInstalledAsync())
        {
            if (!await SemanticModelConsent.ConfirmAsync(owner, FeatureNavigation.Label(Catalog.Find(job.FeatureId)))) return false;
            if (job.Options.MediaTag is { } tags) tags.AllowSemanticDownload = true;
            if (job.Options.FolderClassification is { } classification) classification.AllowSemanticDownload = true;
        }
        if (job.Options.PersonClip is { } person) new Storage().SaveToolOptions(job.FeatureId, person.Detection with { ExcludedRanges = null });
        if (job.Options.VideoSummary is { } summary) new Storage().SaveToolOptions(job.FeatureId, summary.Clone());
        if (job.Options.Transcription is { } speech) new Storage().SaveToolOptions(job.FeatureId, speech.Clone());
        Save(); return true;
    }
}
