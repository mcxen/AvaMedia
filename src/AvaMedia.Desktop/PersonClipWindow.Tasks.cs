using System.ComponentModel;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class PersonClipWindow
{
    private readonly Action<IReadOnlyList<Job>, bool>? _enqueue;
    private readonly Action<Job>? _stopTask, _pauseTask, _resumeTask;
    private readonly Avalonia.Controls.Button _pause = new() { Content = "临时停止", IsVisible = false };
    private int _detectionRefreshPosted;
    private static bool DetectionTaskActive(Job job) => job.State is JobState.Waiting or JobState.Paused or JobState.Running or JobState.Stopping;
    private static bool DetectionTaskUpdating(Job job) => job.State is JobState.Waiting or JobState.Running or JobState.Stopping;
    private bool SelectedTaskActive => Selected?.Task is { } job && DetectionTaskUpdating(job);

    public async Task ObserveTaskAsync(Job job)
    {
        if (_closed) return;
        AddPaths(job.Inputs);
        var entry = _entries.FirstOrDefault(entry => VideoFolderScanner.PathComparer.Equals(entry.Path, job.Inputs[0]));
        if (entry is null) return;
        if (entry.Task is { } previous) previous.PropertyChanged -= DetectionTaskChanged;
        entry.Task = job; entry.Observed = null;
        job.PropertyChanged += DetectionTaskChanged;
        entry.Excluded = PersonClipExclusions.Normalize(job.Options.PersonClip?.Detection.ExcludedRanges);
        if (!DetectionTaskUpdating(job) && job.PersonDetectionResult is null)
            job.PersonDetectionResult = await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(job.Options.PersonClip?.AnalysisOnly == true
                ? job.Output : AiTaskResults.PathFor(job, "people"), _lifetime.Token);
        RefreshDetectionTasks();
    }

    private async Task AnalyzeAsync()
    {
        if (_busy || _closed || !_settings.EnableBetaFeatures || _enqueue is null) return;
        var entries = _entries.Where(entry => entry.Task is null || !DetectionTaskActive(entry.Task)).ToArray();
        if (entries.Length == 0) return;
        _busy = true; UpdateDetectorSelection();
        try
        {
            var detection = ReadDetection(); detection.Validate();
            new Storage().SaveToolOptions("person-clip", detection with { ExcludedRanges = null });
            if (detection.UseEmbedding && !await SemanticModelConsent.IsInstalledAsync(_lifetime.Token)
                && !await SemanticModelConsent.ConfirmAsync(this, "语义辅助")) detection = detection with { UseEmbedding = false };
            _lifetime.Token.ThrowIfCancellationRequested();
            var options = entries.Select(entry => new ConversionOptions
            {
                Format = "json", PersonClip = new() { Detection = detection with { ExcludedRanges = entry.Excluded.ToArray() },
                    AnalysisOnly = true, ExportPreset = _format.SelectedItem as string ?? QuickClipBatch.DefaultPreset }
            }).ToArray();
            var jobs = ConversionBatch.CreateJobs(Catalog.Find("person-clip"), entries.Select(entry => entry.Path).ToArray(),
                Path.GetDirectoryName(entries[0].Path)!, options[0], options);
            foreach (var job in jobs)
            {
                job.Output = AiTaskResults.PathFor(job, "people");
                var entry = entries.First(entry => VideoFolderScanner.PathComparer.Equals(entry.Path, job.Inputs[0]));
                entry.Result = null; entry.Error = "";
                await ObserveTaskAsync(job);
            }
            _enqueue(jobs, true);
            _status.Text = Localization.Format($"已创建 {jobs.Count} 个检测任务 · 关闭窗口后继续运行");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) _status.Text = error.Message; }
        finally { _busy = false; if (!_closed) RefreshDetectionTasks(); }
    }

    private void DetectionTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed || Interlocked.Exchange(ref _detectionRefreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _detectionRefreshPosted, 0); if (!_closed) RefreshDetectionTasks(); });
    }
    private void RefreshDetectionTasks()
    {
        if (_closed) return;
        foreach (var entry in _entries)
        {
            if (entry.Task is not { } job) continue;
            if (job.PersonDetectionResult is { } result && !ReferenceEquals(entry.Observed, result))
            {
                entry.Observed = result; entry.Length = result.SourceLength; entry.WriteUtc = result.SourceWriteUtc;
                entry.Result = new(entry.Path, result.Result.Info, result.Result.Segments);
            }
            entry.Status = job.Status; entry.Error = job.Error;
        }
        var selected = Selected?.Task;
        if (selected is not null) { _activity.Update(selected.Activity); _status.Text = selected.Status; }
        var jobs = _entries.Select(entry => entry.Task).OfType<Job>().Where(job => job.State is JobState.Waiting or JobState.Running or JobState.Paused).ToArray();
        _pause.IsVisible = _pauseTask is not null && _resumeTask is not null && jobs.Length > 0;
        _pause.Content = Localization.Text(jobs.Length > 0 && jobs.All(job => job.State == JobState.Paused) ? "继续任务" : "临时停止");
        _stop.IsVisible = _entries.Any(entry => entry.Task is { } job && DetectionTaskActive(job));
        RefreshFiles();
    }
    private void StopDetectionTasks()
    {
        foreach (var job in _entries.Select(entry => entry.Task).OfType<Job>().Where(DetectionTaskActive)) _stopTask?.Invoke(job);
    }
    private void DetachDetectionTasks()
    { foreach (var job in _entries.Select(entry => entry.Task).OfType<Job>()) job.PropertyChanged -= DetectionTaskChanged; }

    public async Task FlushTaskEditsAsync()
    {
        foreach (var entry in _entries.Where(entry => entry.Task is not null && entry.Result is not null && entry.Observed is not null
            && !DetectionTaskActive(entry.Task!)))
        {
            var job = entry.Task!;
            var revised = entry.Observed! with { Result = entry.Observed!.Result with { Segments = entry.Result!.Segments.ToArray() } };
            await AiTaskResults.SaveAsync(job.Options.PersonClip?.AnalysisOnly == true ? job.Output : AiTaskResults.PathFor(job, "people"), revised, CancellationToken.None);
            entry.Observed = revised; job.PersonDetectionResult = revised;
        }
    }
}
