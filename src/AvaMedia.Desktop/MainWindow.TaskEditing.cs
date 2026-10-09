using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private Job? _editingJob;

    internal bool CanEditTask(Job job) => CanManageTasks && !_queue.IsExecuting(job)
        && job.State is not (JobState.Running or JobState.Stopping) && _jobs.Contains(job);

    private void UpdateTaskEditingActions()
    {
        var enabled = JobList.SelectedItems?.Count == 1 && JobList.SelectedItem is Job job && CanEditTask(job);
        EditTaskButton.IsEnabled = EditTaskMenuItem.IsEnabled = EditTaskContextMenu.IsEnabled = enabled;
        ViewSummaryResultMenu.IsEnabled = JobList.SelectedItems?.Count == 1 && JobList.SelectedItem is Job result && CanViewSummaryResult(result);
        ViewMediaTagResultMenu.IsEnabled = JobList.SelectedItems?.Count == 1 && JobList.SelectedItem is Job tag && CanViewMediaTagResult(tag);
        RetryTaskMenu.IsEnabled = SelectedJobs().Any(item => CanRequeueTask(item) && item.State == JobState.Failed);
        UpdateTaskManagementActions();
    }

    private IEnumerable<string> EditingReservations(Job original) => _jobs.Where(job => job != original).Select(job => job.Output);

    private static void ResetTask(Job job)
    {
        job.Log = "";
        job.Progress = 0; job.ProgressDetail = ""; job.Estimate = null; job.DownloadSpeed = null; job.Error = ""; job.Activity = null;
        job.State = JobState.Waiting;
    }

    private void ApplyEditedJobs(Job original, IReadOnlyList<Job> replacements, bool preserveOutputName = true)
    {
        if (_closing || _queue.IsExecuting(original) || original.State is JobState.Running or JobState.Stopping || !_jobs.Contains(original)) return;
        var replacement = replacements[0];
        var paused = original.State == JobState.Paused;
        if (preserveOutputName && Catalog.Find(replacement.FeatureId).Operation != Operation.Download)
        {
            var directory = Catalog.DirectoryOutput(Catalog.Find(replacement.FeatureId).Operation);
            var name = directory ? Path.GetFileName(original.Output) : Path.GetFileNameWithoutExtension(original.Output);
            var reserved = EditingReservations(original).Concat(replacements.Skip(1).Select(job => job.Output))
                .Concat(replacements.SelectMany(job => job.Inputs));
            replacement.Output = MediaEngine.UniqueOutput(Path.GetDirectoryName(replacement.Output)!, name, replacement.Options.Format, reserved, directory);
            MediaEngine.Validate(replacement);
        }
        original.FeatureId = replacement.FeatureId; original.Inputs = replacement.Inputs;
        original.Options = replacement.Options; original.InputOptions = replacement.InputOptions;
        original.Output = replacement.Output; original.DownloadTitle = replacement.DownloadTitle;
        original.Duration = replacement.Duration;
        ResetTask(original);
        if (paused) original.State = JobState.Paused;
        var index = _jobs.IndexOf(original);
        foreach (var additional in replacements.Skip(1))
        { if (paused) additional.State = JobState.Paused; _jobs.Insert(++index, additional); }
        Save(); Refresh();
    }

    internal async Task EditJob(Job job)
    {
        if (!CanEditTask(job)) return;
        var scheduled = _queue.IsScheduled(job);
        if (!_queue.Withdraw(job)) return;
        _editingJob = job; Refresh();
        try
        {
            var feature = Catalog.Find(job.FeatureId);
            if (feature.Operation == Operation.Download) { await EditDownloadAsync(job); return; }
            if (feature.Operation == Operation.ImageCompress)
            { await ConfigureImageCompressionAsync(job.Inputs, job.Options.ImageCompression, job); return; }
            if (feature.Operation == Operation.VideoCompress) { await EditVideoCompressionAsync(job); return; }
            if (feature.Operation == Operation.VideoSlim) { await ConfigureVideoSlimmingAsync(job.Inputs, job); return; }
            if (feature.Operation == Operation.VideoSummary) { await ConfigureVideoSummaryAsync(job.Inputs, job); return; }
            if (feature.Operation == Operation.PersonClip) { await ConfigurePersonClipAsync(job.Inputs, job); return; }
            if (feature.Operation == Operation.MediaTag)
            {
                await ConfigureMediaAiAsync(job.Inputs);
                return;
            }
            if (PdfTools.Supports(feature.Operation))
            {
                var request = await new PdfWorkspaceWindow(feature, Path.GetDirectoryName(job.Output)!, job.Inputs,
                    job.Options, Engine, editing: true).ShowDialog<PdfWorkspaceRequest?>(this);
                if (request is null) return;
                var replacements = ConversionBatch.CreateJobs(feature, request.Files, request.OutputFolder,
                    request.Options, reserved: EditingReservations(job));
                ApplyEditedJobs(job, replacements); return;
            }
            var dialog = feature.Operation == Operation.Transcribe || feature.Id is "voice-enhance" or "audio-enhance"
                ? (Avalonia.Controls.Window)new SpeechToolsWindow(Engine, feature, Path.GetDirectoryName(job.Output)!, job.Inputs, job.Options, editing: true)
                : new ConvertWindow(Engine, feature, Path.GetDirectoryName(job.Output)!, job.Inputs, job.Options, job.InputOptions, editing: true);
            var result = await dialog.ShowDialog<ConversionRequest?>(this);
            if (result is null) return;
            var jobs = ConversionBatch.CreateJobs(result.Feature, result.Files, result.OutputFolder, result.Options,
                result.InputOptions, EditingReservations(job));
            OutputPreferences.Apply(jobs, _settings, EditingReservations(job), result.OutputToSource, result.SettingName);
            ApplyEditedJobs(job, jobs, preserveOutputName: result.SettingName.Length == 0);
        }
        catch (Exception exception) { await Ui.Message(this, "任务编辑失败", exception.Message); }
        finally
        {
            _editingJob = null;
            if (scheduled && _jobs.Contains(job) && job.State == JobState.Waiting)
            { _queue.Enqueue([job]); _queue.ReorderPending(_jobs); }
            Save(); Refresh();
        }
    }
}
