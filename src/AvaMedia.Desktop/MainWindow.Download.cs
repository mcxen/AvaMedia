using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    public async Task ConfigureDownloadAsync(IEnumerable<string>? links=null,IVideoDownloadService? service=null)
    {
        var window=new DownloadWindow(_settings,_settings.OutputFolder,links,service);
        var request=await ToolExecution.ShowAsync<VideoDownloadRequest>(this, window);
        if(request is null)return;
        try
        {
            var jobs=DownloadBatch.CreateJobs(request,_jobs.Select(j=>j.Output));
            AddToolJobs(jobs,ToolExecution.StartImmediately(window));
        }
        catch(Exception ex){await Ui.Message(this,"下载设置错误",ex.Message);}
    }

    private async Task EditDownloadAsync(Job job)
    {
        var window = new DownloadWindow(_settings,Path.GetDirectoryName(job.Output)!,editingJob:job);
        var request=await ToolExecution.ShowAsync<VideoDownloadRequest>(this, window);
        if(request is null)return;
        ApplyEditedJobs(job,DownloadBatch.CreateJobs(request,EditingReservations(job)));
    }
}
