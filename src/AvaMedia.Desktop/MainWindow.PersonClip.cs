using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private PersonClipWindow CreatePersonClipWindow(string[]? files, Job? editing = null, bool review = false)
    {
        async Task ManageModels(Window owner, string? modelId)
        {
            var settings = new SettingsWindow(_settings, _optionServices);
            settings.OpenModelManagement(modelId); settings.Applied += (_, _) => ApplyOptions();
            await settings.ShowDialog<bool>(owner);
        }
        var folder = editing is null || editing.HasInternalOutput ? _settings.OutputFolder : Path.GetDirectoryName(editing.Output)!;
        var window = new PersonClipWindow(Engine, _settings, files, ManageModels, folder,
            editing?.Options.PersonClip, editing is not null && !editing.HasInternalOutput && !review,
            enqueue: (jobs, start) => AddToolJobs(jobs, start), stopTask: job => { _queue.Stop(job); Save(); Refresh(); },
            newTask: () => { _ = ConfigurePersonClipAsync(null); }, pauseTask: PauseTask, resumeTask: job => { _ = RequestTaskRunAsync(job); });
        _personClipWindows.Add(window); window.Closed += (_, _) => _personClipWindows.Remove(window);
        return window;
    }

    private Task ConfigurePersonClipAsync(string[]? files, Job? editing = null) => editing is null
        ? StartToolWorkflow(() => ConfigurePersonClipWindowAsync(files)) : ConfigurePersonClipWindowAsync(files, editing);

    private async Task ConfigurePersonClipWindowAsync(string[]? files, Job? editing = null)
    {
        var window = CreatePersonClipWindow(files, editing);
        var request = await ToolExecution.ShowAsync<PersonClipRequest>(this, window);
        if (request is not null && !_closing) await SubmitPersonClipsAsync(request, editing);
    }

    private async Task SubmitPersonClipsAsync(PersonClipRequest request, Job? editing = null)
    {
        if (!_settings.EnableBetaFeatures) return;
        try
        {
            var reserved = (editing is null ? _jobs.Select(job => job.Output) : EditingReservations(editing)).ToArray();
            var jobs = QuickClipWorkflow.PrepareJoinedJobs(request.Edits, request.Preset, new(), request.OutputFolder,
                request.OutputToSource, "People", reserved);
            if (editing is not null) ApplyEditedJobs(editing, jobs);
            else AddToolJobs(jobs, request.StartImmediately);
        }
        catch (Exception error) { await Ui.Message(this, "人物检测参数错误", error.Message); }
    }

    private async Task ShowPersonClipResultAsync(Job job)
    {
        var window = CreatePersonClipWindow(job.Inputs, job, review: true);
        var completion = ToolExecution.ShowAsync<PersonClipRequest>(this, window);
        try { await window.ObserveTaskAsync(job); }
        catch { window.Close(); throw; }
        var request = await completion;
        if (request is not null && !_closing) await SubmitPersonClipsAsync(request);
    }
}
