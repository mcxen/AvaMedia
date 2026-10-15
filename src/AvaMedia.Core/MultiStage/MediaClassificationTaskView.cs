using ImageMagick;

namespace AvaMedia.Core;

/// <summary>Pure result projection; optional filesystem observations are made only by the session worker.</summary>
internal static class MediaClassificationTaskViewProjection
{
    internal static byte[]? Thumbnail(byte[]? encoded)
    {
        if (encoded is not { Length: > 0 }) return null;
        try
        {
            using var image = new MagickImage(encoded, new MagickReadSettings { FrameCount = 1 });
            if (image.Width > 640 || image.Height > 640) image.Resize(new MagickGeometry(640, 640));
            return image.ToByteArray(MagickFormat.Png);
        }
        catch (MagickException) { return null; }
    }

    internal static MediaClassificationTaskView Create(Job job, MultiStageTaskSnapshot snapshot,
        FolderClassificationTaskSnapshot? classification, MediaTagResult? tags, byte[]? preview,
        string previewCaption, bool observeOutput)
    {
        var tagging = job.FeatureId == "media-ai";
        var queued = job.State == JobState.Waiting;
        var spec = job.Options.FolderClassification;
        var files = queued ? job.Inputs.Select(path => new FolderClassificationTaskFile(path)).ToArray()
            : classification?.Files ?? job.Inputs.Select(path => new FolderClassificationTaskFile(path)).ToArray();
        if (queued) tags = null;
        var excluded = spec?.ExcludedPaths.ToHashSet(BatchRename.PathComparer) ?? [];
        var items = tagging ? TagItems(job, snapshot, tags, queued) : files.Select(file => Item(job, snapshot, file, excluded.Contains(file.Path), queued)).ToArray();
        var groups = new List<MediaClassificationGroupView>();
        if (tagging && tags is not null)
        {
            var labels = Labels(job, tags);
            if (labels.FirstOrDefault() is { } primary) groups.Add(new(primary.Label, 1));
        }
        else if (!tagging)
        {
            var eligible = files.Where(file => !excluded.Contains(file.Path) && file.Error is null && !file.Pending
                && file.Result is { Media.IsPartial: false }).Select(file => file.Result!).ToArray();
            IReadOnlyDictionary<string, string>? targets = null;
            if (!string.IsNullOrWhiteSpace(spec?.OutputFolder))
            {
                try { targets = FolderOrganization.DestinationFolders(eligible.Where(file => file.Decisions.All(decision => !decision.NeedsReview)), spec.OutputFolder, spec.SplitTypes); }
                catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
                { System.Diagnostics.Trace.TraceWarning("Classification targets: {0}", error.Message); }
            }
            // A category may go to several folders (type split or additional rules). Keep each actual layout separate.
            groups.AddRange(eligible.SelectMany(file => file.Decisions.Where(decision => !decision.NeedsReview)
                    .Select(decision => (decision.RuleId, Name: decision.DisplayName, Target: targets?.GetValueOrDefault(file.Media.Path) ?? "")))
                .GroupBy(value => (value.RuleId, value.Name, value.Target))
                .Select(group => new MediaClassificationGroupView(group.Key.Name, group.Count()) { Target = group.Key.Target }));
        }
        var hasResult = tagging ? tags is not null : !queued && files.Any(file => file.Result is not null);
        var model = queued ? "" : job.Activity?.Model;
        if (string.IsNullOrWhiteSpace(model)) model = queued ? "" : snapshot.Runtime.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            var results = tagging ? (tags is null ? [] : new[] { tags }) : files.Select(file => file.Result?.Media).OfType<MediaTagResult>();
            model = string.Join(" / ", results.SelectMany(media => new[] { media.InferredFrames > 0 ? "JoyTag" : null, media.CaptionModel })
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        }
        var sources = !tagging && spec?.SourceFolders is { Length: > 0 } folders ? folders : job.Inputs;
        var outputPath = !tagging ? FolderClassificationTaskStore.Folder(job)
            : job.Output.Length > 0 ? job.Output : job.Options.MediaTag?.WriteTextReport == false ? AiTaskResults.PathFor(job, "tags") : "";
        var output = Output(outputPath, !tagging, true, observeOutput);
        // Bound ordinary rows while retaining every interrupted, failed or currently working row in input order.
        var visible = items.Where((item, index) => index < 500 || item.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled).ToArray();
        return new()
        {
            SourcePath = string.Join(Environment.NewLine, sources), IsTagging = tagging,
            TotalItems = items.Length, DoneItems = items.Count(item => item.State == StageState.Done),
            FailedItems = items.Count(item => item.State == StageState.Failed), Items = Array.AsReadOnly(visible),
            Groups = groups.AsReadOnly(), ModelName = model ?? "", HasResult = hasResult,
            IsPartial = !hasResult || job.State != JobState.Completed || items.Any(item => item.State is not (StageState.Done or StageState.Skipped)),
            Preview = queued ? null : preview, PreviewCaption = queued ? "" : previewCaption, Output = output
        };
    }

    private static IReadOnlyList<MediaTagTextLabel> Labels(Job job, MediaTagResult tags)
    {
        var spec = job.Options.MediaTag;
        return MediaTagText.QualifyingLabels(tags, spec?.Threshold ?? .4, spec?.SceneThreshold ?? .55,
            spec?.SceneMargin ?? .03, spec?.OnlyLibrary ?? false, spec?.LibraryCandidates);
    }

    private static MediaClassificationItemView[] TagItems(Job job, MultiStageTaskSnapshot snapshot, MediaTagResult? tags, bool queued)
    {
        var labels = tags is null ? [] : Labels(job, tags);
        return job.Inputs.Select(path =>
        {
            var result = tags is not null && BatchRename.PathComparer.Equals(tags.Path, path) ? tags : null;
            var state = result is { IsPartial: false } ? StageState.Done : queued ? StageState.Pending
                : job.State == JobState.Failed ? StageState.Failed : job.State == JobState.Cancelled ? StageState.Cancelled
                : Working(job, snapshot, path, result?.IsPartial == true || snapshot.Stages.Any(stage => stage.Stage.Id == MediaClassificationStageIds.AnalyzeItems && stage.State is StageState.Active or StageState.Paused));
            return new MediaClassificationItemView(path, state)
            {
                Result = result is null ? "" : string.Join(" · ", labels.Take(5).Select(label => label.Label)),
                Detail = Join(result?.Caption, result?.SceneError, result?.CaptionError, state == StageState.Failed ? job.Error : null),
                Confidence = result is null ? null : labels.FirstOrDefault()?.Score
            };
        }).ToArray();
    }

    private static MediaClassificationItemView Item(Job job, MultiStageTaskSnapshot snapshot, FolderClassificationTaskFile file, bool excluded, bool queued)
    {
        var result = file.Result;
        var state = excluded ? StageState.Skipped : file.Error is not null ? StageState.Failed
            : result is not null && !file.Pending && !result.Media.IsPartial ? StageState.Done
            : queued ? StageState.Pending : Working(job, snapshot, file.Path, file.Pending && result is not null);
        var decision = result?.Decisions.FirstOrDefault(value => !value.NeedsReview);
        var confidence = decision?.Scores.FirstOrDefault(score => score.CategoryId == decision.CategoryId)?.Similarity;
        return new(file.Path, state)
        {
            Result = result is null ? "" : result.Decisions.Count > 0 ? string.Join(" · ", result.Decisions.Select(value => value.DisplayName)) : string.Join(" · ", result.Tags.Take(5)),
            Detail = Join(file.Error, result?.Media.Caption, result is null ? null : string.Join("\n", result.Decisions.Select(value => value.Evidence)), result?.Media.SceneError, result?.Media.CaptionError),
            Confidence = confidence is { } value && double.IsFinite(value) ? value : null
        };
    }

    private static StageState Working(Job job, MultiStageTaskSnapshot snapshot, string path, bool partial)
    {
        var stage = snapshot.Stages.FirstOrDefault(value => value.Stage.Id == MediaClassificationStageIds.AnalyzeItems);
        var activity = job.Activity;
        var detail = activity?.Detail ?? "";
        var name = Path.GetFileName(path);
        var matches = detail == path || detail == name || detail.StartsWith(name + " · ", StringComparison.Ordinal)
            || stage?.Detail.EndsWith(" · " + name, StringComparison.Ordinal) == true;
        var analyzing = stage?.State is StageState.Active or StageState.Paused or StageState.Failed or StageState.Cancelled
            || matches && activity?.State is AiActivityState.Running or AiActivityState.Paused;
        if ((!matches && !(partial && string.IsNullOrEmpty(detail))) || !analyzing) return StageState.Pending;
        return job.State switch { JobState.Paused => StageState.Paused, JobState.Cancelled => StageState.Cancelled,
            JobState.Failed => StageState.Failed, JobState.Running or JobState.Stopping => StageState.Active, _ => StageState.Pending };
    }

    private static string Join(params string?[] parts) => string.Join("\n", parts.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static TaskOutputView Output(string path, bool directory, bool fixedLocation, bool observe)
    {
        var exists = false; long? bytes = null;
        if (observe && path.Length > 0)
        {
            try
            {
                if (directory) exists = Directory.Exists(path);
                else { var file = new FileInfo(path); exists = file.Exists; if (exists) bytes = file.Length; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return new() { Path = path, Folder = directory ? path : System.IO.Path.GetDirectoryName(path) ?? "", Exists = exists, Bytes = bytes, IsFixedLocation = fixedLocation };
    }

    internal static bool Equivalent(MediaClassificationTaskView left, MediaClassificationTaskView right) =>
        left.Items.SequenceEqual(right.Items) && left.Groups.SequenceEqual(right.Groups)
        && left with { Items = right.Items, Groups = right.Groups } == right;
}
