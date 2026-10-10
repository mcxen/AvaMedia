using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private McpService? _mcp;

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
        private static McpTaskView View(Job job) => new(job.Id, job.FeatureId, job.Name, job.State.ToString(), job.Progress,
            job.ProgressDetail, job.Error, job.Inputs.ToArray(), job.UserOutput, job.SubmittedBy, job.SubmissionId);

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
                    result = new { media.Path, media.Caption, media.SampledFrames, media.InferredFrames, media.SceneError, media.CaptionError,
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
                        Decisions = file.Result?.Decisions.Where(decision => privateEnabled || !hidden.Contains(decision.RuleId)).ToArray(),
                        Tags = file.Result?.Tags.Where(tag => privateEnabled || !MediaPrivacy.IsSensitiveTag(tag)).ToArray()
                    }).ToArray() };
                next = offset + limit < snapshot.Files.Length ? offset + limit : null;
            }
            else if (job.FeatureId == "person-clip")
                result = job.PersonDetectionResult ?? await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(AiTaskResults.PathFor(job, "people"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "auto-subtitle")
                result = job.SubtitleResult ?? await AiTaskResults.LoadAsync<SubtitleTaskResult>(AiTaskResults.PathFor(job, "subtitles"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "video-summary" && Directory.Exists(job.Output))
                result = await AiTaskResults.LoadAsync<VideoSummaryReport>(Path.Combine(job.Output, "report.json"), ct).ConfigureAwait(false);
            else if (job.FeatureId == "batch-rename")
                result = new { Plan = job.Options.Rename?.Plan, Journal = job.Output, Applied = job.State == JobState.Completed };
            else if (job.State == JobState.Completed) result = new { Output = job.Output };
            return new(view, result, offset, next);
        }

        public async Task<McpTaskView> ControlAsync(Guid id, string action, CancellationToken ct)
        {
            var (job, operation) = await Ui(() =>
            {
                var job = Find(id);
                if (!owner.CanManageTasks) throw new InvalidOperationException("正在编辑任务，请稍后操作。");
                Task operation = Task.CompletedTask;
                switch (action)
                {
                    case "start" when owner.CanStartTask(job):
                        McpTools.ValidatePaths(job, new()); _ = owner.StartToolJobsAsync([job]); break;
                    case "pause" when owner._queue.PauseQueued(job): break;
                    case "resume" when job.State == JobState.Paused && !owner._queue.IsStopping:
                        McpTools.ValidatePaths(job, new()); job.State = JobState.Waiting; _ = owner.StartToolJobsAsync([job]); break;
                    case "stop" when owner._queue.Stop(job): break;
                    case "retry" when job.State is JobState.Failed or JobState.Cancelled && owner.CanRequeueTask(job):
                        if (job.FeatureId == "batch-rename") throw new ArgumentException("请重新预览并提交新的重命名计划。");
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
