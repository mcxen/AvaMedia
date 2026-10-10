using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigureImageCompressionAsync(string[]? files, ImageCompressionOptions? options = null, Job? original = null)
    {
        var window = new ImageCompressionWindow(Engine, original is null ? _settings.OutputFolder : Path.GetDirectoryName(original.Output)!,
            files, options, editing: original is not null);
        var request = await ToolExecution.ShowAsync<ImageCompressionRequest>(this, window);
        if (request is null) return;
        try
        {
            var jobs = ImageCompression.CreateJobs(request, original is null ? _jobs.Select(job => job.Output) : EditingReservations(original));
            if (original is not null) ApplyEditedJobs(original, jobs);
            else AddToolJobs(jobs, request.StartImmediately);
        }
        catch (Exception ex) { await Ui.Message(this, "图片压缩失败", ex.Message); }
    }
}
