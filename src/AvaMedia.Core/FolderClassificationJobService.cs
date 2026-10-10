namespace AvaMedia.Core;

/// <summary>One queue job owns a complete classification session, independent of its windows.</summary>
public sealed class FolderClassificationJobService(IMediaEngine engine)
{
    public static void Validate(Job job)
    {
        if (job.Inputs.Length == 0 || job.Inputs.Any(path => !MediaTagService.Supports(path)))
            throw new ArgumentException("分类任务须包含图片或视频。");
        if (job.InputOptions is not null) throw new ArgumentException("分类任务不支持逐文件编辑参数。");
        var spec = job.Options.FolderClassification ?? throw new ArgumentException("缺少分类任务参数。");
        FolderClassification.ValidateRules(spec.Rules); spec.Analysis.Validate();
        if (!double.IsFinite(spec.TagThreshold) || spec.TagThreshold is < 0 or > 1)
            throw new ArgumentException("标签阈值须为 0–1。");
        if (!BatchRename.PathComparer.Equals(Path.GetFullPath(job.Output), FolderClassificationTaskStore.Folder(job)))
            throw new ArgumentException("分类任务结果目录无效。");
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job);
        var spec = job.Options.FolderClassification!.Clone();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void PrivacyChanged(object? sender, EventArgs args)
        {
            if (engine.Settings.EnableNsfwContent) return;
            try { operation.Cancel(); } catch (ObjectDisposedException) { }
        }
        engine.Settings.NsfwContentChanged += PrivacyChanged;
        try
        {
            ct = operation.Token;
            bool PrivateEnabled() => spec.IncludeNsfw && engine.Settings.EnableNsfwContent;
            var rules = spec.Rules.Where(rule => PrivateEnabled() || !MediaPrivacy.IsSensitiveRule(rule)).ToArray();
            var byOutfit = rules.Any(rule => rule.ByOutfit);
            var state = job.ClassificationSnapshot ?? await FolderClassificationTaskStore.LoadAsync(job, ct).ConfigureAwait(false);
            var files = state.Files.ToDictionary(file => file.Path, BatchRename.PathComparer);
            var pending = new List<string>();
            var excluded = spec.ExcludedPaths.ToHashSet(BatchRename.PathComparer);
            var processed = new HashSet<string>(BatchRename.PathComparer);
            foreach (var path in job.Inputs)
            {
                var file = files.GetValueOrDefault(path) ?? new(path);
                if (excluded.Contains(path)) { files[path] = file with { Pending = false }; continue; }
                if (file.Result is { } completed && !file.Pending && file.Error is null)
                {
                    try { MediaTagService.ValidateSource(completed.Media); files[path] = file; continue; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                files[path] = file with { Error = null, Pending = true }; pending.Add(path);
            }
            void Publish()
            {
                job.ClassificationSnapshot = new(job.Inputs.Select(path => files[path]).ToArray());
                var total = job.Inputs.Count(path => !excluded.Contains(path));
                var done = total - pending.Count + processed.Count;
                job.ProgressDetail = $"{done} / {total}";
                var appearance = byOutfit ? files.Values.Count(file => !excluded.Contains(file.Path)
                    && !file.Pending && file.Error is null && file.Result is { } result && OutfitAppearanceService.IsComplete(result.Media)) : 0;
                progress(total == 0 ? 100 : Math.Clamp(byOutfit
                    ? (80d * done + 15d * appearance) / total : 100d * done / total, 0, 100));
            }
            Publish();
            var sinceGrouping = 0; var groupedOnce = false;
            async Task RegroupAsync(bool force = false)
            {
                if (!byOutfit || groupedOnce && (sinceGrouping == 0 || !force && sinceGrouping < 8)) return;
                var ready = files.Values.Where(file => !excluded.Contains(file.Path) && !file.Pending && file.Error is null
                    && file.Result is { } result && OutfitAppearanceService.IsComplete(result.Media)).ToArray();
                if (ready.Length == 0) return;
                ct.ThrowIfCancellationRequested();
                job.Activity = new("按相似服装分组", "JoyTag / DINOv2", DateTime.UtcNow, DateTime.UtcNow);
                job.ProgressDetail = "按相似服装分组"; progress(job.Progress);
                var grouped = FolderOutfitClassification.Apply(ready.Select(file => file.Result!), rules, PrivateEnabled(), ct);
                foreach (var result in grouped)
                {
                    var file = files[result.Media.Path] with { Result = result };
                    await FolderClassificationTaskStore.SaveFileAsync(job, file, ct).ConfigureAwait(false);
                    files[file.Path] = file;
                }
                sinceGrouping = 0; groupedOnce = true; Publish();
            }
            // Recover cached tag results before waiting for JoyTag, which another queue job may be using.
            var missingAppearance = byOutfit ? files.Values.Where(file => !excluded.Contains(file.Path) && !file.Pending
                && file.Error is null && file.Result is { } result && !OutfitAppearanceService.IsComplete(result.Media)).ToArray() : [];
            async Task<OutfitAppearanceService> PrepareAppearanceAsync()
            {
                var store = new ModelStore();
                foreach (var model in new[] { ModelCatalog.OutfitId, ModelCatalog.OutfitFeaturesId })
                    if (!await store.IsInstalledAsync(model, ct: ct).ConfigureAwait(false))
                        await store.DownloadAsync(model, new InlineProgress<ModelDownloadProgress>(update =>
                        {
                            job.ProgressDetail = update.Stage;
                            job.Activity = new("下载服装模型", "", DateTime.UtcNow, DateTime.UtcNow)
                            { Current = update.Received, Total = update.Total, Unit = "字节", Detail = update.SourceName };
                            progress(job.Progress);
                        }), ct).ConfigureAwait(false);
                job.Activity = new("加载服装模型", "MediaPipe / DINOv2", DateTime.UtcNow, DateTime.UtcNow);
                job.ProgressDetail = "加载服装模型"; progress(job.Progress);
                return await OutfitAppearanceService.CreateAsync(engine, store, ct).ConfigureAwait(false);
            }
            using var appearance = byOutfit && (missingAppearance.Length > 0 || pending.Count > 0)
                ? await PrepareAppearanceAsync().ConfigureAwait(false) : null;
            async Task CompleteAppearanceAsync(FolderClassificationTaskFile file)
            {
                if (!byOutfit || file.Error is not null || file.Result is not { } result || OutfitAppearanceService.IsComplete(result.Media)) return;
                await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                job.Activity = new("识别服装区域", "MediaPipe / DINOv2", DateTime.UtcNow, DateTime.UtcNow)
                { Detail = Path.GetFileName(file.Path) };
                job.ProgressDetail = "识别服装区域 · " + Path.GetFileName(file.Path); progress(job.Progress);
                FolderClassificationTaskFile updated;
                try
                {
                    var media = await appearance!.AnalyzeAsync(result.Media, (current, total) =>
                    {
                        job.ProgressDetail = $"识别服装区域 · {current} / {total} · {Path.GetFileName(file.Path)}"; progress(job.Progress);
                    }, ct).ConfigureAwait(false);
                    updated = file with { Result = result with { Media = media } };
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { updated = file with { Error = error.Message }; }
                await FolderClassificationTaskStore.SaveFileAsync(job, updated, ct).ConfigureAwait(false);
                files[file.Path] = updated; sinceGrouping++; Publish();
                await RegroupAsync().ConfigureAwait(false);
            }
            await RegroupAsync().ConfigureAwait(false);
            foreach (var file in missingAppearance) await CompleteAppearanceAsync(file).ConfigureAwait(false);
            await RegroupAsync(force: true).ConfigureAwait(false);
            var metadataOnly = rules.Length > 0 && rules.All(rule => rule.ByDuration);
            var options = spec.Analysis with { RecognizeNsfw = false, SemanticCandidates = rules.SelectMany(rule => rule.Candidates()).ToArray() };
            if (pending.Count > 0 && !metadataOnly)
            {
                var store = new ModelStore();
                var download = new InlineProgress<ModelDownloadProgress>(update =>
                {
                    job.ProgressDetail = update.Stage;
                    job.Activity = new("下载分类模型", "", DateTime.UtcNow, DateTime.UtcNow)
                    { Current = update.Received, Total = update.Total, Unit = "字节", Detail = update.SourceName };
                    progress(job.Progress);
                });
                if (options.NeedsSemanticModel && !await store.IsInstalledAsync(ModelCatalog.EmbeddingId, ct: ct).ConfigureAwait(false))
                {
                    if (!spec.AllowSemanticDownload) throw new InvalidOperationException("请在模型管理中安装语义模型后重试。");
                    await store.DownloadAsync(ModelCatalog.EmbeddingId, download, ct).ConfigureAwait(false);
                }
                if (!await store.IsInstalledAsync(ModelCatalog.JoyTagId, ct: ct).ConfigureAwait(false))
                    await store.DownloadAsync(ModelCatalog.JoyTagId, download, ct).ConfigureAwait(false);
            }
            async Task RecordAsync(string path, MediaTagResult? media, string? error)
            {
                ct.ThrowIfCancellationRequested();
                var previous = files[path].Result;
                var classified = media is null ? previous : FolderClassification.KeepManual(
                    FolderClassification.Classify(media, rules, spec.TagThreshold, PrivateEnabled()), previous);
                var file = new FolderClassificationTaskFile(path, classified, error, Pending: false);
                await FolderClassificationTaskStore.SaveFileAsync(job, file, ct).ConfigureAwait(false);
                files[path] = file; processed.Add(path); Publish();
                await CompleteAppearanceAsync(file).ConfigureAwait(false);
            }
            if (metadataOnly)
            {
                foreach (var path in pending)
                {
                    await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                    MediaTagResult? media = null; string? error = null;
                    try
                    {
                        var source = new FileInfo(path); var length = source.Length; var modified = source.LastWriteTimeUtc;
                        var seconds = VideoFormats.IsVideo(path) ? (await engine.Probe(path, ct).ConfigureAwait(false)).Duration : 0;
                        media = new(path, [], 0, 0, "文件信息", length, modified) { DurationSeconds = seconds };
                        MediaTagService.ValidateSource(media);
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException) { error = failure.Message; }
                    await RecordAsync(path, media, error).ConfigureAwait(false);
                }
            }
            else if (pending.Count > 0)
            {
                var service = new MediaTagService(engine);
                foreach (var path in pending)
                {
                    await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                    MediaTagResult? result = null; string? error = null;
                    var report = new InlineProgress<MediaTagProgress>(update =>
                    {
                        if (update.Activity is { } activity)
                        {
                            job.Activity = MediaPrivacy.Filter(activity, PrivateEnabled());
                            job.ProgressDetail = activity.Stage; progress(job.Progress);
                        }
                        if (update.Result is not null) result = update.Result;
                        if (update.Error is not null) error = update.Error;
                    });
                    await service.AnalyzeAsync([path], options, report, ct).ConfigureAwait(false);
                    await RecordAsync(path, result, error).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            await RegroupAsync(force: true).ConfigureAwait(false);
            var selected = files.Values.Where(file => !excluded.Contains(file.Path)).ToArray();
            var failures = selected.Count(file => file.Error is not null || file.Result is null);
            var review = selected.Count(file => file.Result?.Decisions.Any(decision => decision.NeedsReview) == true);
            job.ProgressDetail = $"完成 {selected.Count(file => file.Result is not null && file.Error is null)} 个，待确认 {review} 个，失败 {failures} 个";
            progress(job.Progress);
            if (failures > 0) throw new InvalidOperationException(job.ProgressDetail);
        }
        finally { engine.Settings.NsfwContentChanged -= PrivacyChanged; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    { public void Report(T value) => report(value); }
}
