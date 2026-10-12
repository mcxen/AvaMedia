using AvaMedia.Core;
using Avalonia.Media.Imaging;

namespace AvaMedia.Desktop.Classification;

public sealed partial class ClassificationRunWindow
{
    // Built on the worker alongside the shared insights projection. No file scans on the dispatcher.
    private sealed record RunProjection(AiStageNodeDto[] Stages, AiStageNodeDto[] Steps,
        FolderFileVisual? Current, IReadOnlyList<FolderFileVisual> Files, FolderGroupView[] Groups,
        int Ordinal, int Done, int Total, bool Outfit, bool Preparing, AiActivity? Activity,
        int? Frame, int? FrameTotal, bool FrameOwnership, int SelectedFrame, Bitmap? Preview);

    private RunProjection? _runProjection;
    private string? _lastWorkingPath;
    private AiActivity? _lastRunningActivity;
    private double? _preparationSeconds;
    private DateTime? _runStarted;
    private readonly Dictionary<string, (MediaTagResult Media, bool Complete)> _appearanceCache = new(BatchRename.PathComparer);
    private byte[]? _previewBytes;
    private Bitmap? _decodedPreview;
    private readonly List<Bitmap> _retiredPreviews = [];
    private bool AppearanceComplete(MediaTagResult media)
    {
        if (_appearanceCache.TryGetValue(media.Path, out var cached) && ReferenceEquals(cached.Media, media)) return cached.Complete;
        var complete = OutfitAppearanceService.IsComplete(media);
        _appearanceCache[media.Path] = (media, complete);
        return complete;
    }

    private void ProjectRun()
    {
        var snapshot = _job.ClassificationSnapshot;
        var spec = _job.Options.FolderClassification;
        var rules = spec?.Rules ?? [];
        var metadata = rules.All(rule => rule.ByDuration);
        var outfit = rules.Any(rule => rule.ByOutfit);
        var scene = !metadata && (spec?.Analysis.RecognizeScenes == true || rules.Any(rule => rule.Candidates().Length > 0));
        var activity = _job.Activity;
        if (_runStarted != _job.ClassificationStartedUtc)
        {
            _runStarted = _job.ClassificationStartedUtc;
            _appearanceCache.Clear(); _preparationSeconds = null; _lastWorkingPath = null; _lastRunningActivity = null;
        }
        if (activity is { State: AiActivityState.Running }) _lastRunningActivity = activity;
        if (_job.State == JobState.Paused && activity is not null)
            activity = _lastRunningActivity ?? activity with
            { Stage = activity.Nodes.ElementAtOrDefault(activity.CurrentNode)?.Snapshot?.Stage ?? _job.ProgressDetail.Split(" · ")[0] };
        var stage = activity?.Stage ?? "";
        var preparing = IsPreparation(stage);
        var grouping = stage == "按相似服装分组";
        var excluded = (spec?.ExcludedPaths ?? []).ToHashSet(BatchRename.PathComparer);
        var filesByPath = _progress.Files.ToDictionary(file => file.Path, BatchRename.PathComparer);
        var files = _job.Inputs.Where(path => !excluded.Contains(path) && filesByPath.ContainsKey(path))
            .Select(path => filesByPath[path]).ToArray();
        var taskFiles = snapshot?.Files.ToDictionary(file => file.Path, BatchRename.PathComparer) ?? [];
        var matches = files.Where(file => activity is not null &&
            (activity.Detail == file.Path || activity.Detail == file.Name || activity.Detail.StartsWith(file.Name + " · ", StringComparison.Ordinal))).ToArray();
        var current = matches.FirstOrDefault(file => activity!.Detail == file.Path);
        if (current is null && matches.Length == 1) current = matches[0];
        if (current is null && matches.Length > 1)
        {
            // The backend sometimes reports only a basename. An unambiguous partial checkpoint identifies
            // duplicate names; otherwise leave the current file unknown rather than label the wrong source.
            var partial = matches.Where(item => taskFiles.GetValueOrDefault(item.Path) is { Pending: true, Result.Media.IsPartial: true }).ToArray();
            if (partial.Length == 1) current = partial[0];
        }
        if (current is not null && !preparing) _lastWorkingPath = current.Path;
        if (current is null && (grouping || _job.State == JobState.Paused) && _lastWorkingPath is not null)
            current = files.FirstOrDefault(file => BatchRename.PathComparer.Equals(file.Path, _lastWorkingPath));
        for (var i = 0; i < files.Length; i++)
            if (files[i].State == FolderFileState.Processing)
            {
                var saved = taskFiles.GetValueOrDefault(files[i].Path);
                files[i] = files[i] with { State = saved?.Error is not null ? FolderFileState.Failed : saved is { Pending: false, Result: not null }
                    ? files[i].PendingReview ? FolderFileState.Pending : FolderFileState.Done : FolderFileState.Queued };
            }
        var file = current is null ? null : taskFiles.GetValueOrDefault(current.Path);
        var media = file?.Result?.Media;
        var appearanceDone = media is not null && outfit && AppearanceComplete(media);
        var complete = file is { Pending: false, Error: null, Result: not null } && (!outfit || appearanceDone);
        var currentIsWorking = current is not null && !preparing && !grouping && !complete && file?.Error is null;
        if (currentIsWorking)
        {
            current = current! with { State = FolderFileState.Processing };
            for (var i = 0; i < files.Length; i++) if (BatchRename.PathComparer.Equals(files[i].Path, current.Path)) files[i] = current;
        }
        var done = files.Count(item => taskFiles.TryGetValue(item.Path, out var saved) &&
            (saved.Error is not null || saved is { Pending: false, Result: not null } &&
                (!outfit || AppearanceComplete(saved.Result.Media))));
        var modelNodes = activity?.Nodes.Where(node => IsPreparation(node.Title) && node.Snapshot is not null).ToArray() ?? [];
        var measured = modelNodes.Sum(node => Math.Max(0, (node.Snapshot!.UpdatedUtc - node.Snapshot.StartedUtc).TotalSeconds));
        if (modelNodes.Length > 0) _preparationSeconds = measured;
        // Standalone model loading exposes its own start time. Do not invent a persisted preparation duration.
        var preparationTime = preparing && modelNodes.Length == 0 && activity is not null
            ? Math.Max(0, (activity.UpdatedUtc - activity.StartedUtc).TotalSeconds) : _preparationSeconds;
        var chain = new List<string>();
        if (outfit) chain.Add("MediaPipe → DINOv2");
        if (scene) chain.Add("语义模型");
        if (!metadata) chain.Add("JoyTag");
        var stages = new[]
        {
            new AiStageNodeDto(0, "准备模型", preparing ? "running" : current is not null || done > 0 || grouping ? "done" : "pending",
                preparationTime, chain.ToArray(), metadata ? "仅按文件信息判定" : ""),
            new AiStageNodeDto(1, "逐个文件分析", done == files.Length && files.Length > 0 ? "done" : currentIsWorking ? "running" : "pending",
                null, [], "按添加顺序 · 每个文件依次完成下列步骤"),
            new AiStageNodeDto(2, "相似归组", grouping ? "running" : "pending",
                null, [], outfit ? "首批服装区域完成后开始 · 之后每 8 个文件重新归组" : "未启用服装归组"),
            new AiStageNodeDto(3, "完成汇总", _job.ClassificationFinishedUtc is not null ? "done" : "pending",
                null, [], "完成 / 待确认 / 失败计数，转入结果页")
        };
        var frame = activity is { Unit: "帧", Current: { } frameCurrent, Total: > 0 } ? (int?)frameCurrent : null;
        var frameTotal = frame is not null ? (int?)activity!.Total : null;
        if (stage == "识别服装区域")
        {
            // Outfit callbacks currently report frame counters only in ProgressDetail.
            var parts = _job.ProgressDetail.Split('·', StringSplitOptions.TrimEntries);
            var counters = parts.Length > 1 ? parts[1].Split('/', StringSplitOptions.TrimEntries) : [];
            if (counters.Length == 2 && int.TryParse(counters[0], out var c) && int.TryParse(counters[1], out var t) && t > 0)
            { frame = c; frameTotal = t; }
        }
        var steps = new List<AiStageNodeDto>();
        var reading = stage is "读取图片" or "读取视频" or "采样画面";
        var tagging = stage is "识别图片标签" or "识别视频标签";
        var identifyingScene = stage == "识别语义标签";
        var appearance = stage == "识别服装区域";
        var tagsDone = media is not null && (!media.IsPartial || identifyingScene || appearance || complete ||
            media.SampledFrames > 0 && media.Frames.Count >= media.SampledFrames);
        var sceneDone = media?.Scenes is not null && (!media.IsPartial || appearance || complete);
        var readingDone = media is not null || currentIsWorking && (tagging || identifyingScene || appearance);
        string State(bool finished, bool running, bool failed = false) => failed ? "failed" : finished ? "done" : running && currentIsWorking ? "running" : "pending";
        void Step(string title, string state, string detail = "") => steps.Add(new(steps.Count, title, state, null, [], detail));
        if (!metadata)
        {
            Step("读取画面", State(readingDone, reading), media is { SampledFrames: > 0 } ? media.SampledFrames.ToString() : "");
            Step("JoyTag 标签", State(tagsDone, tagging), media?.Backend ?? (tagging ? activity?.Backend : null) ?? "");
            if (scene) Step("场景识别", State(sceneDone, identifyingScene, media?.SceneError is not null),
                media?.SceneSkipped == true ? "已跳过" : media?.SceneError ?? "语义模型");
        }
        Step("规则判定", State(media is { IsPartial: false }, false), rules.Length.ToString());
        if (outfit) Step("服装区域", State(appearanceDone, appearance, file?.Error is not null && appearance), "MediaPipe · DINOv2");
        Step("保存结果", State(complete, false));
        var selectedFrame = -1;
        if (current is not null && stage == "识别服装区域" && frame is > 0 && frame <= current.Frames.Length)
            selectedFrame = frame.Value - 1;
        else if (Active && current is not null && activity?.PreviewCaption is { Length: > 0 } caption)
        {
            var separator = caption.LastIndexOf(" · ", StringComparison.Ordinal);
            var captionTime = separator >= 0 ? caption[(separator + 3)..] : "";
            if (TimeSpan.TryParse(captionTime, System.Globalization.CultureInfo.InvariantCulture, out var time))
                selectedFrame = Array.FindIndex(current.Frames, sampled => Math.Abs(sampled.Seconds - time.TotalSeconds) < .002);
        }
        Bitmap? preview = null;
        if (Active && current is not null && activity?.Preview is { Length: > 0 } bytes && !appearance && !grouping && !preparing)
        {
            if (!ReferenceEquals(bytes, _previewBytes))
            {
                if (_decodedPreview is not null) _retiredPreviews.Add(_decodedPreview);
                _decodedPreview = null; _previewBytes = bytes;
                try { using var stream = new MemoryStream(bytes, writable: false); _decodedPreview = Bitmap.DecodeToWidth(stream, 840); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException) { _decodedPreview = null; }
            }
            preview = _decodedPreview;
        }
        _runProjection = new(stages, steps.ToArray(), current, files, _progress.Groups,
            current is null ? 0 : Array.FindIndex(files, item => BatchRename.PathComparer.Equals(item.Path, current.Path)) + 1,
            done, files.Length, outfit, preparing, activity, frame, frameTotal,
            file?.Result?.Decisions.Any(decision => decision.Frames.Any(observation => observation.CategoryId is not null)) == true, selectedFrame, preview);
    }

    private void DisposeRetiredPreviews()
    {
        foreach (var bitmap in _retiredPreviews) bitmap.Dispose();
        _retiredPreviews.Clear();
        if (!_closed) return;
        _livePreview.Source = null; _decodedPreview?.Dispose(); _decodedPreview = null;
    }

    private static bool IsPreparation(string stage) => stage.Contains("模型", StringComparison.Ordinal) &&
        (stage.Contains("准备", StringComparison.Ordinal) || stage.Contains("加载", StringComparison.Ordinal) ||
         stage.Contains("下载", StringComparison.Ordinal) || stage.Contains("等待", StringComparison.Ordinal) ||
         stage.Contains("校验", StringComparison.Ordinal) || stage.Contains("预热", StringComparison.Ordinal)) || stage == "准备语义描述";
}
