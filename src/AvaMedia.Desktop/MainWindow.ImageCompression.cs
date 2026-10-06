using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigureImageCompressionAsync(string[]? files, ImageCompressionOptions? options = null, Job? original = null)
    {
        var request = await new ImageCompressionWindow(Engine, original is null ? _settings.OutputFolder : Path.GetDirectoryName(original.Output)!,
            files, options, canStart: !_queue.IsRunning).ShowDialog<ImageCompressionRequest?>(this);
        if (request is null) return;
        try
        {
            var jobs = ImageCompression.CreateJobs(request, _jobs.Select(job => job.Output));
            if (original is not null) _jobs.Remove(original);
            foreach (var job in jobs) _jobs.Add(job);
            Save(); Refresh();
            if (request.StartImmediately) await StartQueueAsync();
        }
        catch (Exception ex) { await Ui.Message(this, "图片压缩失败", ex.Message); }
    }
}
