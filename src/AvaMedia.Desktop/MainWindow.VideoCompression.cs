using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<Job> CreateCompressionJobs(VideoCompressionRequest request, IEnumerable<string> reserved)
    {
        var used = reserved.ToArray();
        var jobs = VideoCompression.CreateJobs(request.Files, request.OutputFolder, request.Options, used);
        var name = request.AddSettingName ? "Compress " + request.Options.Codec.ToUpperInvariant() : "";
        OutputPreferences.Apply(jobs, _settings, used, request.OutputToSource, name);
        return jobs;
    }

    private async Task ConfigureVideoCompressionAsync(string[]? files, VideoCompressionOptions? options = null)
    {
        var request = await new VideoCompressionWindow(Engine, _settings.OutputFolder, files ?? [], options)
            .ShowDialog<VideoCompressionRequest?>(this);
        if (request is null) return;
        try
        {
            var jobs = CreateCompressionJobs(request, _jobs.Select(job => job.Output));
            foreach (var job in jobs) _jobs.Add(job);
            Save(); Refresh();
        }
        catch (Exception exception) { await Ui.Message(this, "压缩参数错误", exception.Message); }
    }

    private async Task EditVideoCompressionAsync(Job job)
    {
        var request = await new VideoCompressionWindow(Engine, Path.GetDirectoryName(job.Output)!, job.Inputs, job.Options.VideoCompression, editing: true)
            .ShowDialog<VideoCompressionRequest?>(this);
        if (request is null || job.State == JobState.Running) return;
        try
        {
            ApplyEditedJobs(job, CreateCompressionJobs(request, EditingReservations(job)), preserveOutputName: !request.AddSettingName);
        }
        catch (Exception exception) { await Ui.Message(this, "压缩参数错误", exception.Message); }
    }
}
