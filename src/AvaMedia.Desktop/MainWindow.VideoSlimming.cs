using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigureVideoSlimmingAsync(string[]? files, Job? editing = null)
    {
        var window = new VideoSlimmingWindow(Engine, editing is null ? _settings.OutputFolder : Path.GetDirectoryName(editing.Output)!,
            files ?? [], editing?.Options.VideoSlimming, editing is not null);
        var request = await ToolExecution.ShowAsync<VideoSlimmingRequest>(this, window);
        if (request is null) return;
        try
        {
            var reserved = editing is null ? _jobs.Select(job => job.Output) : EditingReservations(editing);
            var jobs = VideoSlimming.CreateJobs(request.Files, request.OutputFolder, request.Options, reserved);
            foreach (var job in jobs)
                if (request.Analyses.TryGetValue(job.Inputs[0], out var analysis))
                    job.Options.VideoSlimming = request.Options with { Analysis = analysis };
            OutputPreferences.Apply(jobs, _settings, reserved, request.OutputToSource, "Slim " + request.Options.Codec.ToUpperInvariant());
            if (editing is not null) ApplyEditedJobs(editing, jobs);
            else
            {
                AddToolJobs(jobs, ToolExecution.StartImmediately(window));
            }
        }
        catch (Exception exception) { await Ui.Message(this, "瘦身参数错误", exception.Message); }
    }
}
