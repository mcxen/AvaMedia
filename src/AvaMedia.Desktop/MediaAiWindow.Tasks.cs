using System.ComponentModel;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly Action<Job>? _stopTask, _pauseTask, _resumeTask;
    private readonly Action? _newTask;
    private readonly Dictionary<string, Job> _taskJobs = new(BatchRename.PathComparer);
    private readonly Dictionary<Guid, MediaTagResult> _seenTaskResults = [];
    private int _taskRefreshPosted;
    private static bool AnalysisTaskActive(Job job) => job.State is JobState.Waiting or JobState.Paused or JobState.Running or JobState.Stopping;
    private bool HasActiveTask(string path) => _taskJobs.TryGetValue(path, out var job) && AnalysisTaskActive(job);

    public async Task ObserveTaskAsync(Job job)
    {
        if (_closed) return;
        AddPaths(job.Inputs);
        ApplyTaskSettings(job);
        foreach (var path in job.Inputs)
        {
            if (_taskJobs.TryGetValue(path, out var previous)) previous.PropertyChanged -= AnalysisTaskChanged;
            _taskJobs[path] = job;
        }
        job.PropertyChanged -= AnalysisTaskChanged;
        job.PropertyChanged += AnalysisTaskChanged;
        if (job.State is not (JobState.Waiting or JobState.Running or JobState.Stopping) && job.MediaTagResult is null)
        {
            try { job.MediaTagResult = await AiTaskResults.LoadAsync<MediaTagResult>(job.HasInternalOutput ? job.Output : AiTaskResults.PathFor(job, "tags"), _lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { if (!_closed) _status.Text = error.Message; }
        }
        RefreshAnalysisTasks();
    }

    private void ApplyTaskSettings(Job job)
    {
        if (job.Options.MediaTag is not { } spec) return;
        _threshold.Value = (decimal)spec.Threshold; _threshold.Text = _threshold.Value?.ToString(_threshold.NumberFormat);
        _sceneThreshold.Value = spec.SceneThreshold; _sceneMargin.Value = (decimal)spec.SceneMargin;
        _frames.Value = spec.Analysis.VideoFrames; _frames.Text = _frames.Value?.ToString(_frames.NumberFormat);
        _gpu.IsChecked = spec.Analysis.PreferGpu; _reuse.IsChecked = spec.Analysis.ReuseSimilarFrames;
        _sceneTags.IsChecked = spec.Analysis.RecognizeScenes; _realPeople.IsChecked = spec.Analysis.RealPeopleOnly;
        _generateCaptions.IsChecked = spec.Analysis.GenerateCaptions; _autoTxt.IsChecked = spec.WriteTextReport;
        _captionLocalModelId = spec.Analysis.CaptionLocalModelId; _captionPrompt = spec.Analysis.CaptionPrompt;
        _captionSystemPrompt = spec.Analysis.CaptionSystemPrompt; _captionUseFrameTools = spec.Analysis.CaptionUseFrameTools;
        _libraryCandidates = spec.LibraryCandidates.Where(entry => _settings.EnableNsfwContent || !MediaPrivacy.IsSensitive(entry)).ToArray();
        _privateLibraryLabels.UnionWith(spec.LibraryCandidates.Where(MediaPrivacy.IsSensitive).Select(entry => entry.Label));
        _onlyLibrary.IsEnabled = _libraryCandidates.Length > 0; _onlyLibrary.IsChecked = spec.OnlyLibrary && _libraryCandidates.Length > 0;
        _librarySummary.Text = Localization.Format($"已选 {_libraryCandidates.Length} 个标签"); _librarySummary.IsVisible = _libraryCandidates.Length > 0;
    }

    private async Task QueueAnalysisAsync(bool startImmediately, string[]? requestedPaths = null)
    {
        if (_closed || _busy || _writingTxt || _enqueue is null) return;
        var paths = _entries.Where(entry => (requestedPaths is null ? entry.Include : requestedPaths.Contains(entry.Path, BatchRename.PathComparer))
            && !HasActiveTask(entry.Path)).Select(entry => entry.Path).ToArray();
        if (paths.Length == 0) return;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = preparation; SetBusy(true);
        try
        {
            var options = ReadTaskOptions();
            if (options.Analysis.NeedsSemanticModel && !await SemanticModelConsent.IsInstalledAsync(preparation.Token))
            {
                options.AllowSemanticDownload = await SemanticModelConsent.ConfirmAsync(this, "场景识别");
                if (!options.AllowSemanticDownload) options.Analysis = options.Analysis with { RecognizeScenes = false, SemanticCandidates = [] };
            }
            preparation.Token.ThrowIfCancellationRequested();
            var jobs = ConversionBatch.CreateJobs(Catalog.Find("media-ai"), paths, Path.GetDirectoryName(paths[0])!,
                new ConversionOptions { Format = options.WriteTextReport ? "txt" : "json", MediaTag = options });
            OutputPreferences.Apply(jobs, _settings, outputToSource: true, settingName: "标签");
            foreach (var job in jobs)
            {
                if (!options.WriteTextReport) job.Output = AiTaskResults.PathFor(job, "tags");
                var path = job.Inputs[0];
                _results.Remove(path); _liveResults.Remove(path); _traces.Remove(path); _positions.Remove(path); _editedTags.Remove(path);
                await ObserveTaskAsync(job);
            }
            if (startImmediately) _captionWarmup.HandOff();
            _enqueue(jobs, startImmediately);
            RefreshAnalysisTasks();
            _status.Text = Localization.Format($"已创建 {jobs.Count} 个任务 · 关闭窗口后继续运行");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "创建标签任务", error.Message); }
        finally { _operation = null; if (!_closed) { SetBusy(false); RenderSelectedResult(); } }
    }

    private void AnalysisTaskChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closed || Interlocked.Exchange(ref _taskRefreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _taskRefreshPosted, 0); if (!_closed) RefreshAnalysisTasks(); });
    }

    private void RefreshAnalysisTasks()
    {
        if (_closed) return;
        foreach (var entry in _entries)
        {
            if (!_taskJobs.TryGetValue(entry.Path, out var job)) continue;
            if (job.MediaTagResult is { } raw && (!_seenTaskResults.TryGetValue(job.Id, out var previous) || !ReferenceEquals(raw, previous)))
            {
                _seenTaskResults[job.Id] = raw;
                var result = MediaPrivacy.Filter(raw, _settings.EnableNsfwContent) with { Path = entry.Path };
                if (job.State is JobState.Completed or JobState.Paused or JobState.Cancelled or JobState.Failed) { _results[entry.Path] = result; _liveResults.Remove(entry.Path); }
                else { _results.Remove(entry.Path); _liveResults[entry.Path] = result; }
                if (_followLive.IsChecked == true) _positions[entry.Path] = (result.Scenes?.Frames.LastOrDefault()?.Seconds ?? result.Frames.LastOrDefault()?.Seconds) ?? 0;
                ShowResult(entry, result);
                if (_list.SelectedItem == entry) { RenderSelectedResult(); if (_followLive.IsChecked == true) _ = RefreshSelectedPreviewAsync(CursorFor(result)); }
            }
            if (job.State is JobState.Completed or JobState.Paused or JobState.Cancelled or JobState.Failed && _liveResults.Remove(entry.Path, out var completed))
            { _results[entry.Path] = completed; ShowResult(entry, completed); if (_list.SelectedItem == entry) RenderSelectedResult(); }
            if (AnalysisTaskActive(job)) entry.Status = job.Status;
            else if (job.State is JobState.Failed or JobState.Cancelled) { entry.Status = job.Status; entry.Details = job.Error; }
        }
        var selected = _list.SelectedItem as MediaFileEntry;
        var active = selected is not null ? _taskJobs.GetValueOrDefault(selected.Path) : null;
        active ??= _taskJobs.Values.FirstOrDefault(job => job.State == JobState.Running);
        if (active is not null) { _activity.Update(active.Activity is { } activity ? MediaPrivacy.Filter(activity, _settings.EnableNsfwContent) : null); _status.Text = active.Status; }
        UpdateActions();
    }

    private void StopAnalysisTasks()
    {
        _operation?.Cancel();
        foreach (var job in _taskJobs.Values.Distinct().Where(AnalysisTaskActive)) _stopTask?.Invoke(job);
    }

    private void DetachTaskObservers()
    { foreach (var job in _taskJobs.Values.Distinct()) job.PropertyChanged -= AnalysisTaskChanged; }
}
