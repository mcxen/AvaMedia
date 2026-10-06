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

    private async Task ConfigureVideoCompressionAsync(string[]? files)
    {
        var request = await new VideoCompressionWindow(Engine, _settings.OutputFolder, files ?? [])
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
        var request = await new VideoCompressionWindow(Engine, Path.GetDirectoryName(job.Output)!, job.Inputs, job.Options.VideoCompression)
            .ShowDialog<VideoCompressionRequest?>(this);
        if (request is null || job.State == JobState.Running) return;
        try
        {
            var replacements = CreateCompressionJobs(request, _jobs.Select(item => item.Output));
            var replacement = replacements[0];
            job.Options = replacement.Options; job.Inputs = replacement.Inputs; job.Output = replacement.Output;
            job.State = JobState.Waiting; job.Progress = 0; job.ProgressDetail = ""; job.Error = job.Log = "";
            foreach (var additional in replacements.Skip(1)) _jobs.Add(additional);
            Save(); Refresh();
        }
        catch (Exception exception) { await Ui.Message(this, "压缩参数错误", exception.Message); }
    }
}
