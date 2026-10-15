using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>The picker and drop routes share media filtering and queue payload creation.</summary>
internal static class MultiStageEntry
{
    private static readonly string[] AudioExtensions = ["wav", "m4a", "mp3", "flac", "aac", "ogg", "opus", "wma", "aiff", "aif", "m4b", "ac3"];

    internal static bool Supports(string featureId) => featureId is "person-clip" or "rotate" or "auto-subtitle"
        or "video-summary" or "media-ai" or "folder-classification";

    internal static bool Accepts(string featureId, string path) => featureId switch
    {
        "media-ai" or "folder-classification" => MediaTagService.Supports(path),
        "auto-subtitle" => VideoFormats.IsVideo(path) || AudioExtensions.Contains(Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase),
        _ => VideoFormats.IsVideo(path)
    };

    internal static FilePickerOpenOptions Picker(Feature feature)
    {
        var extensions = feature.Id switch
        {
            "media-ai" or "folder-classification" => VideoFormats.InputExtensions.Concat(new[] { "jpg", "jpeg", "png", "webp", "bmp", "tif", "tiff", "gif", "ico", "avif", "heic", "heif" }),
            "auto-subtitle" => VideoFormats.InputExtensions.Concat(AudioExtensions),
            _ => VideoFormats.InputExtensions
        };
        return new() { Title = Localization.Text(FeatureNavigation.Label(feature)), AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(Localization.Text(feature.Id is "media-ai" or "folder-classification" ? "图片和视频" : feature.Id == "auto-subtitle" ? "音视频文件" : "视频"))
            { Patterns = extensions.Distinct().Select(extension => "*." + extension.TrimStart('.')).ToArray() }] };
    }

    internal static IReadOnlyList<Job> CreateJobs(Feature feature, string[] files, AppSettings settings, IEnumerable<string> reserved)
    {
        var storage = new Storage();
        if (feature.Id == "folder-classification")
        {
            var preferences = storage.LoadToolOptions<FolderClassificationWindow.Preferences>(feature.Id) ?? new();
            var rules = (preferences.SceneRules.Length > 0 ? preferences.SceneRules : FolderClassificationRule.DefaultRules())
                .Where(rule => settings.EnableNsfwContent || !MediaPrivacy.IsSensitiveRule(rule)).ToArray();
            var job = new Job { FeatureId = feature.Id, Inputs = files, Options = new() { Format = "", FolderClassification = new()
            {
                Rules = rules, Analysis = new(preferences.VideoFrames) { SemanticCandidates = rules.SelectMany(rule => rule.Candidates()).ToArray() },
                TagThreshold = (double)preferences.TagThreshold, IncludeNsfw = settings.EnableNsfwContent,
                OutputFolder = string.IsNullOrWhiteSpace(preferences.OutputFolder) ? Path.Combine(settings.OutputFolder, "分类结果") : preferences.OutputFolder,
                SplitTypes = preferences.SplitTypes, WriteText = preferences.WriteText
            } } };
            job.Output = FolderClassificationTaskStore.Folder(job);
            job.ClassificationSnapshot = new(files.Select(path => new FolderClassificationTaskFile(path)).ToArray());
            return [job];
        }
        var options = feature.Id switch
        {
            "person-clip" => new ConversionOptions { Format = "json", PersonClip = new() { AnalysisOnly = true,
                Detection = storage.LoadToolOptions<PersonClipOptions>(feature.Id) ?? new() } },
            "auto-subtitle" => new() { Format = "srt", Transcription = storage.LoadToolOptions<TranscriptionOptions>(feature.Id) ?? new() },
            "rotate" => new() { Format = "json", Orientation = new(SourceOutput.Original, settings.OutputFolder, settings.OutputToSource, false) },
            "video-summary" => new() { Format = "", VideoSummary = storage.LoadToolOptions<VideoSummaryOptions>(feature.Id)
                ?? new() { ExtractSubtitles = false, AnalyzeContent = false, Speech = new() { Model = SpeechModel.Base } } },
            _ => new() { Format = "json", MediaTag = new() { WriteTextReport = false } }
        };
        if (options.Transcription is { } speech)
        { speech.RecognitionOnly = true; speech.ReviewOutputFolder = settings.OutputFolder; speech.ReviewOutputToSource = settings.OutputToSource; }
        var jobs = ConversionBatch.CreateJobs(feature, files, settings.OutputFolder, options, reserved: reserved);
        foreach (var job in jobs.Where(job => job.HasInternalOutput)) job.Output = AiTaskResults.InternalOutputFor(job);
        return jobs;
    }
}
