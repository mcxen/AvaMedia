using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void AddToolJobs(IEnumerable<Job> jobs, bool startImmediately)
    {
        var added = jobs.ToArray();
        foreach (var job in added) _jobs.Add(job);
        Save(); Refresh();
        if (startImmediately) _ = StartToolJobsAsync(added);
    }
    private async Task StartToolJobsAsync(Job[] jobs)
    {
        try { await StartQueueAsync(jobs); }
        catch(Exception error) { if(!_closing)await Ui.Message(this,"任务启动失败",error.Message); }
    }
}
