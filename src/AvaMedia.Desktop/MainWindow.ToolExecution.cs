using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void AddToolJobs(IEnumerable<Job> jobs, bool startImmediately)
    {
        var added = jobs.ToArray();
        foreach (var job in added)
        {
            if (!_mcpBackgroundMode && job.FeatureId == "person-clip" && job.Options.PersonClip is not null)
                using (new TaskLifecycle(job, PersonClipLifecycle.Pages)) { }
            if (!_mcpBackgroundMode && job.FeatureId == "auto-subtitle")
            {
                SubtitleLifecycle.PrepareRecognition(job);
                using (new TaskLifecycle(job, SubtitleLifecycle.Pages)) { }
            }
            _jobs.Add(job);
        }
        Save(); Refresh();
        if (startImmediately) _ = StartToolJobsAsync(added);
    }
    private async Task StartToolJobsAsync(Job[] jobs)
    {
        try { await StartQueueAsync(jobs); }
        catch(Exception error)
        {
            if (_mcpBackgroundMode)
            {
                foreach (var job in jobs.Where(job => job.State == JobState.Waiting))
                { job.State = JobState.Failed; job.Error = error.Message; job.AppendLog(error.ToString()); }
                Save();
            }
            else if (!_closing) await Ui.Message(this,"任务启动失败",error.Message);
        }
    }
}
