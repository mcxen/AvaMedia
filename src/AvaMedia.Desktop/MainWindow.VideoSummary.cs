using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task ConfigureVideoSummaryAsync(string[]? files, Job? editing = null)
    {
        var window = new VideoSummaryWindow(Engine, editing is null ? _settings.OutputFolder : Path.GetDirectoryName(editing.Output)!,
            files, editing?.Options.VideoSummary, editing?.Output, async owner =>
            {
                var settings = new SettingsWindow(_settings, _optionServices);
                settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
                await settings.ShowDialog<bool>(owner);
            });
        var request = await window.ShowDialog<ConversionRequest?>(this);
        if (request is null) return;
        try
        {
            var reserved = editing is null ? _jobs.Select(job => job.Output) : EditingReservations(editing);
            var jobs = ConversionBatch.CreateJobs(request.Feature, request.Files, request.OutputFolder, request.Options, reserved: reserved);
            OutputPreferences.Apply(jobs, _settings, reserved, request.OutputToSource, "总结");
            if (editing is not null) ApplyEditedJobs(editing, jobs);
            else { foreach (var job in jobs) _jobs.Add(job); Save(); Refresh(); }
        }
        catch (Exception exception) { await Ui.Message(this, "视频总结", exception.Message); }
    }
}
