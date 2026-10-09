using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    internal bool CanViewSummaryResult(Job job) => !_closing && job.State == JobState.Completed
        && Catalog.Find(job.FeatureId).Operation == Operation.VideoSummary;

    internal async Task ShowSummaryResultAsync(Job job)
    {
        if (!CanViewSummaryResult(job)) return;
        try { new VideoSummaryResultWindow(job.Output, Engine, job.Inputs.FirstOrDefault()).Show(this); }
        catch (Exception error) { await Ui.Message(this, "视频总结结果", error.Message); }
    }

    private async void ViewSummaryResultClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (JobList.SelectedItem is Job job) await ShowSummaryResultAsync(job); }

    private async Task ConfigureVideoSummaryAsync(string[]? files, Job? editing = null)
    {
        var window = new VideoSummaryWindow(Engine, editing is null ? _settings.OutputFolder : Path.GetDirectoryName(editing.Output)!,
            files, editing?.Options.VideoSummary, editing?.Output, async owner =>
            {
                var settings = new SettingsWindow(_settings, _optionServices);
                settings.OpenModelManagement(); settings.Applied += (_, _) => ApplyOptions();
                await settings.ShowDialog<bool>(owner);
            }, async owner =>
            {
                var settings = new SettingsWindow(_settings, _optionServices);
                settings.OpenOnlineAiSettings(); settings.Applied += (_, _) => ApplyOptions();
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
            else AddToolJobs(jobs, request.StartImmediately);
        }
        catch (Exception exception) { await Ui.Message(this, "视频总结", exception.Message); }
    }
}
