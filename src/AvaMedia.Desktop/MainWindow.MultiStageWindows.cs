using AvaMedia.Core;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    /// <summary>Tasks whose every state (waiting, running, paused, finished, failed, cancelled) opens the multi-stage window (UISPEC 10.16).</summary>
    internal static bool UsesMultiStageWindow(Job job) => job.FeatureId switch
    {
        "person-clip" => job.Inputs.Length == 1 && job.Options.PersonClip is not null,
        "rotate" => job.Inputs.Length == 1 && job.Options.Orientation is not null,
        "auto-subtitle" => job.Inputs.Length == 1,
        "video-summary" => job.Inputs.Length == 1 && job.Options.VideoSummary is not null,
        "media-ai" => true,
        "folder-classification" => job.Options.FolderClassification is not null,
        _ => false
    };

    internal void ShowCurrentTaskPage(Job job)
    {
        if (_aiTaskWindows.TryGetValue(job.Id, out var window) && window is TaskLifecycleWindow lifecycle) lifecycle.ShowCurrentPage();
    }

    private void ShowMultiStageWindow(Job job)
    {
        var (session, content, label) = CreateMultiStage(job);
        MultiStageTaskWindow window = job.Lifecycle is not null && job.FeatureId == "person-clip"
            ? new PersonClipLifecycleWindow(job, session, content, Engine, job.HasInternalOutput ? _settings.OutputFolder : Path.GetDirectoryName(job.Output)!,
                () => { Save(); Refresh(); return Task.CompletedTask; },
                async () => { job.State = JobState.Waiting; Save(); Refresh(); await RequestTaskRunAsync(job); })
            : job.Lifecycle is not null && session is ISubtitleTask subtitle
                ? new SubtitleLifecycleWindow(job, subtitle, content, Engine,
                    job.Options.Transcription?.ReviewOutputToSource == true ? Path.GetDirectoryName(job.Inputs[0])! : job.Options.Transcription?.ReviewOutputFolder ?? _settings.OutputFolder,
                    () => { Save(); Refresh(); return Task.CompletedTask; },
                    async () => { job.State = JobState.Waiting; Save(); Refresh(); await RequestTaskRunAsync(job); })
                : new MultiStageTaskWindow(session, content, label, Engine);
        _aiTaskWindows[job.Id] = window;
        window.Closed += (_, _) => _aiTaskWindows.Remove(job.Id);
        Controls.WindowChrome.ShowIndependent(window);
    }

    private (IMultiStageTask Session, IMultiStageToolContent Content, string Label) CreateMultiStage(Job job)
    {
        Task Guard(Func<Task> action) => _closing || !_jobs.Contains(job) ? Task.CompletedTask : action();
        Task OpenFile() { if (File.Exists(job.Output)) Open(job.Output); return Task.CompletedTask; }
        switch (job.FeatureId)
        {
            case "person-clip":
            {
                var session = new PersonClipTaskSession(job, MultiStageQueueHost);
                var actions = new PersonClipTaskActions(OpenOutput: OpenFile, AdjustSegments: () => Guard(() => ShowPersonClipResultAsync(job)),
                    EditParameters: () => CanEditTask(job) ? EditJob(job) : Task.CompletedTask);
                return (session, new PersonClipStageContent(session, Engine, actions), "保留有人片段");
            }
            case "rotate":
            {
                var session = new OrientationTaskSession(job, MultiStageQueueHost);
                var actions = new OrientationTaskActions(Confirm: () => Guard(() => ShowOrientationResultAsync(job)));
                return (session, new OrientationStageContent(session, Engine, actions), "自动旋转");
            }
            case "auto-subtitle":
            {
                var session = new SubtitleTaskSession(job, MultiStageQueueHost);
                var actions = new SubtitleTaskActions(Review: () => Guard(() => ShowSubtitleTaskResultAsync(job)));
                return (session, new SubtitleStageContent(session, Engine, actions), "自动字幕");
            }
            case "video-summary":
            {
                var session = new VideoSummaryTaskSession(job, MultiStageQueueHost);
                var actions = new VideoSummaryTaskActions(OpenReport: () => Guard(() => ShowSummaryResultAsync(job)));
                return (session, new VideoSummaryStageContent(session, Engine, actions), "视频总结");
            }
            default:
            {
                var session = new MediaClassificationTaskSession(job, MultiStageQueueHost);
                var tagging = job.FeatureId == "media-ai";
                var actions = new MediaClassificationTaskActions(OpenResult: tagging
                    ? () => Guard(() => ShowMediaTagResultAsync(job))
                    : () => Guard(() => { OpenClassificationRun(job); return Task.CompletedTask; }));
                return (session, new MediaClassificationStageContent(session, actions), tagging ? "自动标签分类" : "自动媒体整理");
            }
        }
    }
}
