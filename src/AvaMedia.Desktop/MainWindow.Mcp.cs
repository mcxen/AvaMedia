using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private McpService? _mcp;
    private bool _mcpBackgroundMode;
    private long _mcpBackgroundQuietSince;

    internal async Task StartMcpBackgroundAsync(int? port)
    {
        _mcpBackgroundMode = true; _startupOptionsInitialized = true;
        _settings.Mcp.Enabled = true;
        if (port is { } number) _settings.Mcp.Port = number;
        _settings.Mcp.Validate();
        await (_mcp ?? throw new InvalidOperationException("MCP 服务未初始化。")).ConfigureAsync(_settings.Mcp, _optionLifetime.Token);
        _mcpBackgroundQuietSince = System.Diagnostics.Stopwatch.GetTimestamp();
        _timer.Start();
        Console.WriteLine(_mcp.Endpoint);
    }

    private bool McpBackgroundShouldExit()
    {
        if (!_mcpBackgroundMode || _mcp is not { } mcp || _exitRequested) return false;
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

    private sealed class DesktopMcpWorkspace(MainWindow owner) : IMcpWorkspace
    {
        public IMediaEngine Engine => owner.Engine;
        private Task<T> Ui<T>(Func<T> action, CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (owner._closing) throw new InvalidOperationException("应用正在退出。");
            return action();
        }).GetTask();

        private Job Find(Guid id) => owner._jobs.FirstOrDefault(job => job.Id == id) ?? throw new ArgumentException("任务不存在：" + id);
        private McpTaskView View(Job job)
        {
            var activity = job.Activity;
            var privateEnabled = Engine.Settings.EnableNsfwContent;
            return new(job.Id, job.FeatureId, job.Name, job.State.ToString(), job.Progress,
                privateEnabled || !MediaPrivacy.IsSensitiveText(job.ProgressDetail) ? job.ProgressDetail : "分析中",
                job.Error, job.Inputs.ToArray(), job.UserOutput, job.SubmittedBy, job.SubmissionId)
            {
                OutputIsPartial = job.OutputIsPartial || job.State != JobState.Completed,
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

        public Task<McpTaskPage> ListAsync(int offset, int limit, string? state, CancellationToken ct) => Ui(() =>
        {
            JobState? filter = null;
            if (!string.IsNullOrWhiteSpace(state))
            {
                if (!Enum.TryParse<JobState>(state, true, out var parsed) || !Enum.IsDefined(parsed)) throw new ArgumentException("任务状态无效。");
                filter = parsed;
            }
            var jobs = owner._jobs.Where(job => filter is null || job.State == filter).ToArray();
            return new McpTaskPage(jobs.Skip(offset).Take(limit).Select(View).ToArray(), jobs.Length, offset, offset + limit < jobs.Length ? offset + limit : null);
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
                result = job.SummaryReport
                    ?? await AiTaskResults.LoadAsync<VideoSummaryReport>(Path.Combine(
                        job.State == JobState.Completed ? job.Output : AiPartialResults.SummaryFolder(job), "report.json"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "folder-organize")
                result = await AiTaskResults.LoadAsync<FolderOrganizationResult>(AiTaskResults.PathFor(job, "organization-result"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "model-download")
                result = await AiTaskResults.LoadAsync<System.Text.Json.JsonElement?>(job.Output, ct).ConfigureAwait(false);
            else if (job.FeatureId == "batch-rename")
                result = new { Plan = job.Options.Rename?.Plan, Journal = job.Output, Applied = job.State == JobState.Completed };
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
