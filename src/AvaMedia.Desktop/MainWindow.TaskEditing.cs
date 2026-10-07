using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private Job? _editingJob;

    internal bool CanEditTask(Job job) => !_closing && !_queue.IsRunning && _editingJob is null
        && job.State != JobState.Running && _jobs.Contains(job);

    private void UpdateTaskEditingActions()
    {
        var enabled = JobList.SelectedItems?.Count == 1 && JobList.SelectedItem is Job job && CanEditTask(job);
        EditTaskButton.IsEnabled = EditTaskMenuItem.IsEnabled = EditTaskContextMenu.IsEnabled = enabled;
        RetryTaskMenu.IsEnabled = !_queue.IsRunning && _editingJob is null
            && (JobList.SelectedItems?.OfType<Job>().Any(item => item.CanRetry) ?? false);
    }

    private IEnumerable<string> EditingReservations(Job original) => _jobs.Where(job => job != original).Select(job => job.Output);

    private static void ResetTask(Job job)
    {
        job.Progress = 0; job.ProgressDetail = ""; job.Estimate = null; job.Error = job.Log = "";
        job.State = JobState.Waiting;
    }

    private void ApplyEditedJobs(Job original, IReadOnlyList<Job> replacements, bool preserveOutputName = true)
    {
        if (_closing || _queue.IsRunning || original.State == JobState.Running || !_jobs.Contains(original)) return;
        var replacement = replacements[0];
        if (preserveOutputName && Catalog.Find(replacement.FeatureId).Operation != Operation.Download)
        {
            var directory = Catalog.Find(replacement.FeatureId).Operation is Operation.Frames or Operation.PdfSplit or Operation.Unzip;
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
        var index = _jobs.IndexOf(original);
        foreach (var additional in replacements.Skip(1)) _jobs.Insert(++index, additional);
        Save(); Refresh();
    }

    internal async Task EditJob(Job job)
    {
        if (!CanEditTask(job)) return;
        _editingJob = job; Refresh();
        try
        {
            var feature = Catalog.Find(job.FeatureId);
            if (feature.Operation == Operation.Download) { await EditDownloadAsync(job); return; }
            if (feature.Operation == Operation.ImageCompress)
            { await ConfigureImageCompressionAsync(job.Inputs, job.Options.ImageCompression, job); return; }
            if (feature.Operation == Operation.VideoCompress) { await EditVideoCompressionAsync(job); return; }
            if (PdfTools.Supports(feature.Operation))
            {
                var request = await new PdfWorkspaceWindow(feature, Path.GetDirectoryName(job.Output)!, job.Inputs,
                    job.Options, Engine, editing: true).ShowDialog<PdfWorkspaceRequest?>(this);
                if (request is null) return;
                var replacements = ConversionBatch.CreateJobs(feature, request.Files, request.OutputFolder,
                    request.Options, reserved: EditingReservations(job));
                ApplyEditedJobs(job, replacements); return;
            }
            var result = await new ConvertWindow(Engine, feature, Path.GetDirectoryName(job.Output)!, job.Inputs,
                job.Options, job.InputOptions, editing: true).ShowDialog<ConversionRequest?>(this);
            if (result is null) return;
            var jobs = ConversionBatch.CreateJobs(result.Feature, result.Files, result.OutputFolder, result.Options,
                result.InputOptions, EditingReservations(job));
            OutputPreferences.Apply(jobs, _settings, EditingReservations(job), result.OutputToSource, result.SettingName);
            ApplyEditedJobs(job, jobs, preserveOutputName: result.SettingName.Length == 0);
        }
        catch (Exception exception) { await Ui.Message(this, "任务编辑失败", exception.Message); }
        finally { _editingJob = null; Refresh(); }
    }
}
