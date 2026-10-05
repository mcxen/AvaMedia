using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    public async Task ConfigureDownloadAsync(IEnumerable<string>? links=null,IVideoDownloadService? service=null)
    {
        var request=await new DownloadWindow(_settings,_settings.OutputFolder,links,service).ShowDialog<VideoDownloadRequest?>(this);
        if(request is null)return;
        try
        {
            var jobs=DownloadBatch.CreateJobs(request,_jobs.Select(j=>j.Output));
            foreach(var job in jobs)_jobs.Add(job);Save();Refresh();
        }
        catch(Exception ex){await Ui.Message(this,"下载设置错误",ex.Message);}
    }
}
