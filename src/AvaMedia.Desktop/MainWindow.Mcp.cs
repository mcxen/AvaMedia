using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private McpService? _mcp;
    private bool _mcpBackgroundMode, _serverMode;
    private long _mcpBackgroundQuietSince;

    internal async Task StartMcpBackgroundAsync(int? port, bool serverMode = false, bool allowLan = false)
    {
        _mcpBackgroundMode = true; _serverMode = serverMode; _startupOptionsInitialized = true;
        _settings.Mcp.Enabled = true;
        if (allowLan) _settings.Mcp.AllowLan = true;
        if (port is { } number) _settings.Mcp.Port = number;
        _settings.Mcp.Validate();
        await (_mcp ?? throw new InvalidOperationException("MCP 服务未初始化。")).ConfigureAsync(_settings.Mcp, _optionLifetime.Token);
        _mcpBackgroundQuietSince = System.Diagnostics.Stopwatch.GetTimestamp();
        _timer.Start();
        Console.WriteLine(_mcp.Endpoint);
        Console.WriteLine($"网页控制台：http://127.0.0.1:{_settings.Mcp.Port}/ · {(_serverMode ? "常驻服务" : "空闲两分钟后退出")}");
    }

    private bool McpBackgroundShouldExit()
    {
        if (_serverMode || !_mcpBackgroundMode || _mcp is not { } mcp || _exitRequested) return false;
        var busy = mcp.HasActiveRequests || _queueSessionActive || _editingJobs.Count > 0 || _jobs.Any(Active);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (busy) { _mcpBackgroundQuietSince = now; return false; }
        var requestQuietSince = now - (long)(mcp.QuietFor.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        return System.Diagnostics.Stopwatch.GetElapsedTime(Math.Max(_mcpBackgroundQuietSince, requestQuietSince)) >= TimeSpan.FromMinutes(2);
    }

    private void InitializeMcp()
    {
        if (IsCaptureSession) return;
        _mcp = new(new DesktopMcpWorkspace(this));
        Opened += async (_, _) => await ConfigureMcpAsync();
    }
    private async Task ConfigureMcpAsync()
    {
        if (_mcp is null || _closing) return;
        try { await _mcp.ConfigureAsync(_settings.Mcp, _optionLifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing) SummaryText.Text = "MCP 服务：" + error.Message; }
    }

    private sealed partial class DesktopMcpWorkspace(MainWindow owner) : IMcpWorkspace
    {
        public IMediaEngine Engine => owner.Engine;
        private Task<T> Ui<T>(Func<T> action, CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (owner._closing) throw new InvalidOperationException("应用正在退出。");
            return action();
        }).GetTask();

        private Job Find(Guid id) => owner._jobs.FirstOrDefault(job => job.Id == id) ?? throw new ArgumentException("任务不存在：" + id);
        public Task<ConversionOptions> OptionsAsync(Guid id, CancellationToken ct) => Ui(() => Find(id).Options.Clone(), ct);
        private McpTaskView View(Job job)
        {
            var activity = job.Activity;
            var privateEnabled = Engine.Settings.EnableNsfwContent;
            return new(job.Id, job.FeatureId, job.Name, job.State.ToString(), job.Progress,
                privateEnabled || !MediaPrivacy.IsSensitiveText(job.ProgressDetail) ? job.ProgressDetail : "分析中",
                job.Error, job.Inputs.ToArray(), job.UserOutput, job.SubmittedBy, job.SubmissionId)
            {
                OutputIsPartial = job.OutputIsPartial || job.State != JobState.Completed,
                CompletionKind = job.CompletionKind.ToString(),
                CompletionMessage = privateEnabled || !MediaPrivacy.IsSensitiveText(job.CompletionMessage) ? job.CompletionMessage : "",
                Activity = activity is null ? null : new(activity.Stage, activity.Model, activity.Backend,
                    privateEnabled || !MediaPrivacy.IsSensitiveText(activity.Detail) ? activity.Detail : "",
                    activity.Current, activity.Total, activity.Unit, activity.UpdatedUtc)
            };
        }

        public Task<McpRuntimeView> RuntimeAsync(McpResourceUpdate? update, CancellationToken ct) => Ui(() =>
        {
            if (update is not null)
            {
                if (update.CpuReservePercent is < 0 or > 80 || update.CpuThreads is < 1 or > 16)
                    throw new ArgumentException("CPU 预留须为 0–80%，线程数为 1–16。");
                var settings = owner._settings;
                settings.CpuReservePercent = update.CpuReservePercent ?? settings.CpuReservePercent;
                settings.CpuThreads = update.CpuThreads ?? settings.CpuThreads;
                settings.MultiThread = update.MultiThread ?? settings.MultiThread;
                owner._storage.SaveSettings(settings); owner._appliedSettings = settings.Clone();
                owner.Multithread.IsChecked = settings.MultiThread;
            }
            return new McpRuntimeView(typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "", Environment.OSVersion.ToString(),
                owner._mcpBackgroundMode, owner._settings.OutputFolder, AiResourceSettings.From(owner._settings),
                owner._settings.EnableNsfwContent, owner._settings.EnableBetaFeatures);
        }, ct);

        public async Task<string> LogAsync(Guid id, CancellationToken ct)
        {
            var job = await Ui(() => Find(id), ct).ConfigureAwait(false);
            return await job.ReadLogAsync(ct).ConfigureAwait(false);
        }

        public async Task<AiTaskProgressDto> AiProgressAsync(Guid id, CancellationToken ct)
        {
            var captured = await Ui(() =>
            {
                var job = Find(id);
                return (job.Id, job.State, job.Progress, job.ProgressDetail, Activity: job.Activity,
                    Queue: AiQueueInfo.Summarize(owner._jobs), Private: Engine.Settings.EnableNsfwContent);
            }, ct).ConfigureAwait(false);
            // Only capture shared collection state on the dispatcher; project arrays on the request worker.
            var activity = captured.Activity;
            string Visible(string value) => captured.Private || !MediaPrivacy.IsSensitiveText(value) ? value : "";
            string[] VisibleLines(string[] values) => values.Select(Visible).Where(value => value.Length > 0).ToArray();
            var now = DateTime.UtcNow;
            var nodes = activity?.Nodes.Select((node, index) =>
            {
                var snapshot = node.Snapshot;
                var current = index == activity.CurrentNode;
                var state = snapshot is null ? "pending" : snapshot.State == AiActivityState.Completed ? "done"
                    : current && captured.State == JobState.Failed ? "failed"
                    : current && captured.State == JobState.Completed ? "done" : "running";
                var end = current && captured.State == JobState.Running ? now : snapshot?.UpdatedUtc;
                double? elapsed = snapshot is null || end is null ? null : Math.Max(0, (end.Value - snapshot.StartedUtc).TotalSeconds);
                return new AiStageNodeDto(index, node.Title, state, elapsed,
                    VisibleLines(snapshot?.RecentStages ?? []), Visible(snapshot?.Detail ?? ""));
            }).ToArray() ?? [];
            var observations = activity?.Nodes.Select(node => node.Snapshot).OfType<AiActivity>().ToArray() ?? [];
            if (observations.Length == 0 && activity is not null) observations = [activity];
            return new(captured.Id.ToString(), Visible(activity?.Stage ?? ""), Visible(activity?.Detail ?? captured.ProgressDetail),
                captured.Progress, observations.LastOrDefault(value => value.Backend.Length > 0)?.Backend ?? "", activity?.CurrentNode ?? -1, nodes,
                VisibleLines(observations.SelectMany(value => value.RecentResults).TakeLast(30).ToArray()),
                VisibleLines(observations.SelectMany(value => value.RecentStages).TakeLast(12).ToArray()),
                Preview(activity) is { Length: > 0 }, AiProgressStore.GetSpeech(captured.Id.ToString()), captured.Queue)
            { SummarySegments = SummaryProgressStore.Get(captured.Id.ToString())?.Select(s => s with { Summary = Visible(s.Summary) }).ToArray() };
        }

        public async Task<SummaryEvidence?> SummaryEvidenceAsync(Guid id, string evidenceId, CancellationToken ct)
        {
            var captured = await Ui(() => { var job = Find(id); return (Folder: job.State == JobState.Completed ? job.Output : AiPartialResults.SummaryFolder(job), Feature: job.FeatureId); }, ct);
            if (captured.Feature != "video-summary") return null;
            return await new EvidenceStore(captured.Folder).LoadAsync(evidenceId, ct);
        }

        public Task<byte[]?> AiPreviewAsync(Guid id, CancellationToken ct) => Ui(() => Preview(Find(id).Activity), ct);

        private static byte[]? Preview(AiActivity? activity) => activity?.Preview
            ?? activity?.Nodes.LastOrDefault(node => node.Snapshot?.Preview is { Length: > 0 })?.Snapshot?.Preview;

        public async Task<FolderClassifiedFile[]> ClassificationAsync(Guid id, CancellationToken ct)
        {
            var (job, hidden) = await Ui(() =>
            {
                var job = Find(id);
                if (job.FeatureId != "folder-classification" || job.State is JobState.Waiting or JobState.Running or JobState.Paused or JobState.Stopping
                    || owner._editingJobs.Contains(job)) throw new InvalidOperationException("请先完成分类任务并结束编辑，再预览整理。");
                var hidden = job.Options.FolderClassification?.Rules.Where(MediaPrivacy.IsSensitiveRule).Select(rule => rule.Id).ToHashSet() ?? [];
                return (job, hidden);
            }, ct).ConfigureAwait(false);
            var snapshot = job.ClassificationSnapshot ?? await FolderClassificationTaskStore.LoadAsync(job, ct).ConfigureAwait(false);
            var excluded = job.Options.FolderClassification?.ExcludedPaths.ToHashSet(BatchRename.PathComparer) ?? [];
            var privateEnabled = Engine.Settings.EnableNsfwContent;
            return snapshot.Files.Where(file => !file.Pending && file.Error is null && file.Result is not null && !excluded.Contains(file.Path))
                .Select(file => file.Result!).Select(file => privateEnabled ? file : file with
                {
                    Media = MediaPrivacy.Filter(file.Media, false),
                    Decisions = file.Decisions.Where(decision => !hidden.Contains(decision.RuleId) && !decision.OutfitIncludesNsfw).ToArray(),
                    Tags = file.Tags.Where(tag => !MediaPrivacy.IsSensitiveTag(tag)).ToArray()
                }).Where(file => file.Decisions.Count > 0).ToArray();
        }

        public async Task<FolderClassificationLive> ClassificationLiveAsync(Guid id, CancellationToken ct)
        {
            var job = await Ui(() => ClassificationJob(id), ct).ConfigureAwait(false);
            var loaded = job.ClassificationSnapshot ?? await FolderClassificationTaskStore.LoadAsync(job, ct).ConfigureAwait(false);
            return await Ui(() =>
            {
                job.InitializeClassificationSnapshot(loaded);
                var spec = job.Options.FolderClassification!;
                var privateEnabled = Engine.Settings.EnableNsfwContent;
                var rules = spec.Rules.Where(rule => privateEnabled || !MediaPrivacy.IsSensitiveRule(rule)).ToArray();
                var allowed = rules.Select(rule => rule.Id).ToHashSet();
                var excluded = spec.ExcludedPaths.ToHashSet(BatchRename.PathComparer);
                var snapshot = new FolderClassificationTaskSnapshot(job.ClassificationSnapshot!.Files.Where(file => !excluded.Contains(file.Path))
                    .Select(file => FilterClassification(file, privateEnabled, allowed)).ToArray());
                var regroup = job.ClassificationRegroup ??= FolderClassificationTaskStore.LoadRegroup(job);
                if (regroup is not null && !privateEnabled)
                {
                    var visible = FolderClassificationInsights.Groups(snapshot.Files, null).Select(group => group.RuleId + "/" + group.CategoryId).ToHashSet();
                    regroup = regroup with { Added = regroup.Added.Where(pair => visible.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value) };
                }
                return new FolderClassificationLive(snapshot, rules, rules.FirstOrDefault(rule => rule.ByOutfit)?.OutfitSimilarity ?? spec.TagThreshold,
                    spec.SourceFolders.Length > 0 ? spec.SourceFolders : job.Inputs.Select(Path.GetDirectoryName).OfType<string>().Distinct(BatchRename.PathComparer).ToArray(),
                    regroup, job.State == JobState.Running ? job.Activity?.Stage : null, job.Progress, job.ClassificationElapsed);
            }, ct).ConfigureAwait(false);
        }

        public async Task<FolderClassificationTaskFile> ResolveClassificationAsync(Guid id, string path, string ruleId, string categoryId, CancellationToken ct)
        {
            var live = await ClassificationLiveAsync(id, ct).ConfigureAwait(false);
            var visibleRule = live.Rules.FirstOrDefault(rule => rule.Id == ruleId) ?? throw new ArgumentException("分类规则不可用。");
            if (!live.Snapshot.Files.Any(file => BatchRename.PathComparer.Equals(file.Path, path))
                || !FolderOutfitClassification.Categories(visibleRule, live.Snapshot.Files.Select(file => file.Result).OfType<FolderClassifiedFile>())
                    .Any(category => category.Id == categoryId)) throw new ArgumentException("文件或分类类别不可用。");
            var job = await Ui(() =>
            {
                var job = ClassificationJob(id);
                var spec = job.Options.FolderClassification!;
                if (spec.ExcludedPaths.Contains(path, BatchRename.PathComparer)) throw new ArgumentException("文件已从任务中排除。");
                var rule = spec.Rules.FirstOrDefault(rule => rule.Id == ruleId) ?? throw new ArgumentException("分类规则不存在。");
                if (!Engine.Settings.EnableNsfwContent && MediaPrivacy.IsSensitiveRule(rule)) throw new ArgumentException("分类规则不可用。");
                if (!Engine.Settings.EnableNsfwContent && job.ClassificationSnapshot?.Files.SelectMany(file => file.Result?.Decisions ?? [])
                    .Any(decision => decision.RuleId == ruleId && decision.CategoryId == categoryId && decision.OutfitIncludesNsfw) == true)
                    throw new ArgumentException("分类类别不可用。");
                return job;
            }, ct).ConfigureAwait(false);
            var resolving = await Ui(() => FolderClassificationTaskStore.ResolveAsync(job, path, ruleId, categoryId, ct), ct).ConfigureAwait(false);
            var resolved = await resolving.ConfigureAwait(false);
            return await Ui(() => FilterClassification(resolved, Engine.Settings.EnableNsfwContent,
                job.Options.FolderClassification!.Rules.Where(rule => Engine.Settings.EnableNsfwContent || !MediaPrivacy.IsSensitiveRule(rule)).Select(rule => rule.Id).ToHashSet()), ct).ConfigureAwait(false);
        }

        private Job ClassificationJob(Guid id)
        {
            var job = Find(id);
            if (job.FeatureId != "folder-classification" || job.Options.FolderClassification is null) throw new ArgumentException("不是分类任务。");
            return job;
        }

        private static FolderClassificationTaskFile FilterClassification(FolderClassificationTaskFile file, bool privateEnabled, HashSet<string> allowed)
            => privateEnabled || file.Result is not { } result ? file : file with { Result = result with
            {
                Media = MediaPrivacy.Filter(result.Media, false),
                Decisions = result.Decisions.Where(decision => allowed.Contains(decision.RuleId) && !decision.OutfitIncludesNsfw).ToArray(),
                Tags = result.Tags.Where(tag => !MediaPrivacy.IsSensitiveTag(tag)).ToArray()
            } };

        public Task<McpTaskPage> ListAsync(int offset, int limit, string? state, CancellationToken ct) => Ui(() =>
        {
            JobState? filter = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<JobState>(state, true, out var parsed) || !Enum.IsDefined(parsed)) throw new ArgumentException("任务状态无效。");
                filter = parsed;
            }
            var jobs = owner._jobs.Where(job => filter is null || job.State == filter).ToArray();
            return new McpTaskPage(jobs.Skip(offset).Take(limit).Select(View).ToArray(), jobs.Length, offset, offset + limit < jobs.Length ? offset + limit : null)
            { Counts = McpTaskCounts.From(owner._jobs.Select(job => job.State)) };
        }, ct);

        public async Task<McpSubmission> SubmitAsync(string requestId, string hash, Func<string[], Job[]> create, bool start, CancellationToken ct)
        {
            var submitted = await Ui(() =>
            {
                var previous = owner._jobs.Where(job => job.SubmissionId == requestId).ToArray();
                if (previous.Length > 0)
                {
                    if (previous.Any(job => job.SubmissionHash != hash)) throw new ArgumentException("requestId 已用于不同参数，请使用新的 UUID。");
                    return new McpSubmission(requestId, previous.Select(View).ToArray());
                }
                if (!owner.CanManageTasks || start && owner._queue.IsStopping) throw new InvalidOperationException("任务队列正忙，请稍后提交。");
                var jobs = create(owner._jobs.Select(job => job.Output).ToArray());
                if (jobs.Length == 0) throw new ArgumentException("没有可创建的任务。");
                foreach (var job in jobs)
                {
                    job.SubmittedBy = "MCP"; job.SubmissionId = requestId; job.SubmissionHash = hash;
                }
                owner.AddToolJobs(jobs, start);
                return new McpSubmission(requestId, jobs.Select(View).ToArray());
            }, ct).ConfigureAwait(false);
            // Accepted work belongs to the queue, so disconnecting the HTTP caller must not cancel it.
            await owner.PersistenceReady.ConfigureAwait(false);
            return submitted;
        }

        public Task<McpSubmission?> FindSubmissionAsync(string requestId, string hash, CancellationToken ct) => Ui<McpSubmission?>(() =>
        {
            var jobs = owner._jobs.Where(job => job.SubmissionId == requestId).ToArray();
            if (jobs.Length == 0) return null;
            if (jobs.Any(job => job.SubmissionHash != hash)) throw new ArgumentException("requestId 已用于不同参数，请使用新的 UUID。");
            return new(requestId, jobs.Select(View).ToArray());
        }, ct);

        public async Task<McpTaskResult> GetAsync(Guid id, bool includeResult, int offset, int limit, CancellationToken ct)
        {
            var (job, view) = await Ui(() => { var found = Find(id); return (found, View(found)); }, ct).ConfigureAwait(false);
            if (!includeResult) return new(view, null);
            var privateEnabled = Engine.Settings.EnableNsfwContent;
            object? result = null; int? next = null;
            if (job.FeatureId == "media-ai")
            {
                var media = job.MediaTagResult ?? await AiTaskResults.LoadAsync<MediaTagResult>(AiTaskResults.PathFor(job, "tags"), ct).ConfigureAwait(false);
                if (media is not null)
                {
                    media = MediaPrivacy.Filter(media, privateEnabled);
                    var spec = job.Options.MediaTag ?? new();
                    result = new { media.Path, media.Caption, media.CaptionModel, media.CaptionBackend, media.CaptionObservations,
                        media.SampledFrames, media.InferredFrames, media.Backend, media.FallbackReason, media.SceneError, media.CaptionError,
                        media.Nsfw, media.DurationSeconds, media.IsPartial,
                        Labels = MediaTagText.QualifyingLabels(media, spec.Threshold, spec.SceneThreshold, spec.SceneMargin, spec.OnlyLibrary, spec.LibraryCandidates)
                            .Where(label => privateEnabled || !MediaPrivacy.IsSensitiveLabel(label.Label, label.Category, label.Tags)).ToArray() };
                }
            }
            else if (job.FeatureId == "folder-classification")
            {
                var snapshot = job.ClassificationSnapshot ?? await FolderClassificationTaskStore.LoadAsync(job, ct).ConfigureAwait(false);
                var hidden = job.Options.FolderClassification?.Rules.Where(MediaPrivacy.IsSensitiveRule).Select(rule => rule.Id).ToHashSet() ?? [];
                result = new { snapshot.Completed, snapshot.Failed, Total = snapshot.Files.Length,
                    Files = snapshot.Files.Skip(offset).Take(limit).Select(file => new
                    {
                        file.Path, file.Error, file.Pending,
                        SampledFrames = file.Result?.Media.SampledFrames, InferredFrames = file.Result?.Media.InferredFrames,
                        Backend = file.Result?.Media.Backend, OutfitBackend = file.Result?.Media.OutfitBackend,
                        OutfitFallbackReason = file.Result?.Media.OutfitFallbackReason,
                        Decisions = file.Result?.Decisions.Where(decision => privateEnabled || !hidden.Contains(decision.RuleId) && !decision.OutfitIncludesNsfw).ToArray(),
                        Tags = file.Result?.Tags.Where(tag => privateEnabled || !MediaPrivacy.IsSensitiveTag(tag)).ToArray()
                    }).ToArray() };
                next = offset + limit < snapshot.Files.Length ? offset + limit : null;
            }
            else if (job.FeatureId == "person-clip")
                result = job.PersonDetectionResult ?? await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(AiTaskResults.PathFor(job, "people"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "auto-subtitle")
                result = job.SubtitleResult ?? await AiTaskResults.LoadAsync<SubtitleTaskResult>(AiTaskResults.PathFor(job, "subtitles"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "video-summary")
            {
                var folder = job.State == JobState.Completed ? job.Output : AiPartialResults.SummaryFolder(job);
                var snapshot = job.LongSummaryReport is { } report
                    ? new LongSummaryTaskSnapshot(report, job.SummarySegments, job.SummarySegments.Length)
                    : await LongVideoSummaryOrchestrator.LoadAsync(folder, ct).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    result = snapshot with { Segments = snapshot.Segments.Skip(offset).Take(limit).ToArray() };
                    next = offset + limit < snapshot.TotalSegments ? offset + limit : null;
                }
                else result = job.SummaryReport ?? await AiTaskResults.LoadAsync<VideoSummaryReport>(Path.Combine(folder, "report.json"), ct).ConfigureAwait(false);
            }
            else if (job.FeatureId == "folder-organize")
                result = await AiTaskResults.LoadAsync<FolderOrganizationResult>(AiTaskResults.PathFor(job, "organization-result"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "model-download")
                result = await AiTaskResults.LoadAsync<System.Text.Json.JsonElement?>(job.Output, ct).ConfigureAwait(false);
            else if (job.FeatureId == "batch-rename")
            {
                var journal = await AiTaskResults.LoadAsync<BatchRename.RenameJournal>(job.Output, ct).ConfigureAwait(false);
                result = new { Plan = job.Options.Rename?.Plan, Journal = job.Output, JournalState = journal?.State,
                    Applied = job.State == JobState.Completed && journal?.State == "completed" };
            }
            else if (view.State == nameof(JobState.Completed) && view.CompletionKind == nameof(JobCompletionKind.Unchanged))
                result = new { CompletionKind = view.CompletionKind, Message = view.CompletionMessage, PreservedInputs = view.Inputs };
            else if (job.State == JobState.Completed) result = new { Output = job.Output };
            return new(view, result, offset, next) { OutputIsDirectory = Directory.Exists(view.Output) };
        }

        public async Task<McpTaskView> ControlAsync(Guid id, string action, CancellationToken ct)
        {
            var (job, operation) = await Ui(() =>
            {
                var job = Find(id);
                if (!owner.CanManageTasks) throw new InvalidOperationException("应用正在退出，请稍后操作。");
                if (owner._editingJobs.Contains(job)) throw new InvalidOperationException("正在编辑此任务，请关闭其编辑窗口后再操作。");
                Task operation = Task.CompletedTask;
                switch (action)
                {
                    case "start" when owner.CanStartTask(job):
                        McpTools.ValidatePaths(job, new()); _ = owner.StartToolJobsAsync([job]); break;
                    case "pause" when owner._queue.Pause(job): break;
                    case "resume" when job.State == JobState.Paused:
                        McpTools.ValidatePaths(job, new());
                        if (!owner._queue.Resume(job)) { job.State = JobState.Waiting; _ = owner.StartToolJobsAsync([job]); }
                        break;
                    case "stop" when owner._queue.Stop(job): break;
                    case "retry" when job.State is JobState.Failed or JobState.Cancelled && owner.CanRequeueTask(job):
                        if (job.FeatureId is "batch-rename" or "folder-organize") throw new ArgumentException("请重新预览并提交新的文件操作计划。");
                        McpTools.ValidatePaths(job, new()); _ = owner.RestartTasksAsync([job]); break;
                    case "remove" when owner.CanRemoveTask(job): operation = owner.RemoveTasksAsync([job]); break;
                    default: throw new ArgumentException("当前任务状态不支持此操作：" + action);
                }
                owner.Save(); owner.Refresh(); return (job, operation);
            }, ct).ConfigureAwait(false);
            await operation.ConfigureAwait(false); await owner.PersistenceReady.ConfigureAwait(false);
            return await Ui(() => View(job), ct).ConfigureAwait(false);
        }
    }
}
