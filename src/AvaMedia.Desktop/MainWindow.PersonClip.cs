using Avalonia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.PersonClipAdjust;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, PersonClipAdjustWindow> _personClipAdjustWindows = [];

    private PersonClipWindow CreatePersonClipWindow(string[]? files, Job? editing = null, bool review = false, PersonClipOptions? redetect = null)
    {
        async Task ManageModels(Window owner, string? modelId)
        {
            await ShowSettingsAsync(owner, settings => settings.OpenModelManagement(modelId));
        }
        var folder = editing is null || editing.HasInternalOutput ? _settings.OutputFolder : Path.GetDirectoryName(editing.Output)!;
        var window = new PersonClipWindow(Engine, _settings, files, ManageModels, folder,
            redetect is null ? editing?.Options.PersonClip : new PersonClipTaskOptions { Detection = redetect, ExportPreset = editing?.Options.PersonClip?.ExportPreset ?? PersonClipTaskOptions.DefaultExportPreset }, editing is not null && !editing.HasInternalOutput && !review,
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

    private async Task<bool> SubmitPersonClipsAsync(PersonClipRequest request, Job? editing = null)
    {
        if (_closing || !_settings.EnableBetaFeatures) return false;
        try
        {
            var reserved = (editing is null ? _jobs.Select(job => job.Output) : EditingReservations(editing)).ToArray();
            var jobs = QuickClipWorkflow.PrepareJoinedJobs(request.Edits, request.Preset, new(), request.OutputFolder,
                request.OutputToSource, "People", reserved);
            if (editing is not null) ApplyEditedJobs(editing, jobs);
            else AddToolJobs(jobs, request.StartImmediately);
            return true;
        }
        catch (Exception error) { await Ui.Message(this, "人物检测参数错误", error.Message); }
        return false;
    }

    private async Task ShowPersonClipResultAsync(Job job)
    {
        if (_closing) return;
        if (_personClipAdjustWindows.TryGetValue(job.Id, out var existing)) { WindowChrome.Present(existing); return; }
        if (!_jobs.Contains(job) || _queue.IsExecuting(job) || job.State is JobState.Running or JobState.Stopping
            || !_editingJobs.Add(job)) return;
        Refresh();
        try
        {
            var session = await PersonClipEditing.OpenAsync(job);
            if (_closing) return;
            var folder = job.HasInternalOutput ? _settings.OutputFolder : Path.GetDirectoryName(job.Output) ?? _settings.OutputFolder;
            var host = new PersonClipAdjustHost(Engine, folder, _settings.OutputToSource,
                job.Options.PersonClip?.ExportPreset ?? PersonClipTaskOptions.DefaultExportPreset,
                Export: request => SubmitPersonClipsAsync(request),
                Redetect: async options =>
                {
                    var form = CreatePersonClipWindow(job.Inputs, job, review: true, redetect: options);
                    var request = await ToolExecution.ShowAsync<PersonClipRequest>(this, form);
                    if (request is not null && !_closing) await SubmitPersonClipsAsync(request);
                });
            var window = new PersonClipAdjustWindow(session, host);
            _personClipAdjustWindows[job.Id] = window;
            window.Closed += (_, _) =>
            {
                _personClipAdjustWindows.Remove(job.Id);
                _editingJobs.Remove(job); Refresh();
            };
            try { window.ShowIndependent(); }
            catch { _personClipAdjustWindows.Remove(job.Id); window.Close(); throw; }
        }
        catch (Exception error) { if (!_closing) await Ui.Message(this, "调整片段", error.Message); }
        finally
        {
            if (!_personClipAdjustWindows.ContainsKey(job.Id)) { _editingJobs.Remove(job); Refresh(); }
        }
    }
}
