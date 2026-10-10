using AvaMedia.Core;
using Avalonia.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, AiTaskWindow> _aiTaskWindows = [];
    private readonly HashSet<PersonClipWindow> _personClipWindows = [];
    private readonly HashSet<SubtitleReviewWindow> _subtitleReviewWindows = [];
    internal static bool IsAiFeature(Feature feature) => feature.Operation is Operation.MediaTag or Operation.FolderClassify
        or Operation.PersonClip or Operation.VideoSummary or Operation.Transcribe || feature.Id is "voice-enhance" or "audio-enhance" or "delogo" or "rotate";
    internal bool CanViewAiTask(Job job) => !_closing && _jobs.Contains(job) && IsAiFeature(Catalog.Find(job.FeatureId));
    private Task StartToolWorkflow(Func<Task> workflow)
    {
        if (_closing) return Task.CompletedTask;
        _ = RunAsync(); return Task.CompletedTask;
        async Task RunAsync()
        {
            try { await workflow(); }
            catch (Exception error) { AppDiagnostics.Record("AI tool workflow", error); if (!_closing) await Ui.Message(this, "任务设置", error.Message); }
        }
    }

    private Task ConfigureSpeechAsync(Feature feature, string[]? files) => StartToolWorkflow(async () =>
    {
        var window = new SpeechToolsWindow(Engine, feature, _settings.OutputFolder, files);
        var request = await ToolExecution.ShowAsync<ConversionRequest>(this, window);
        if (request is not null && !_closing) await SubmitSpeechRequestAsync(request);
    });

    private Task ConfigureAiConversionAsync(Feature feature, string[]? files) => StartToolWorkflow(async () =>
    {
        var window = new ConvertWindow(Engine, feature, _settings.OutputFolder, files ?? []);
        var request = await ToolExecution.ShowAsync<ConversionRequest>(this, window);
        if (request is null || _closing) return;
        var reserved = _jobs.Select(job => job.Output).ToArray();
        var jobs = ConversionBatch.CreateJobs(request.Feature, request.Files, request.OutputFolder, request.Options, request.InputOptions, reserved);
        OutputPreferences.Apply(jobs, _settings, reserved, request.OutputToSource, request.SettingName);
        AddToolJobs(jobs, request.StartImmediately);
    });
    internal void SubmitOrientationJob(Job job) => AddToolJobs([job], true);
    internal void StopOrientationJob(Job job) { _queue.Stop(job); Save(); Refresh(); }

    private BatchRotateWindow CreateRotateWindow(string[]? files, string? output = null) =>
        new(Engine, output ?? _settings.OutputFolder, files, submitDetection: AddToolJobs, stopDetection: job => _queue.Stop(job));

    private Task ConfigureRotateAsync(string[]? files) => StartToolWorkflow(async () =>
    {
        var window = CreateRotateWindow(files);
        await SubmitRotationAsync(window);
    });

    private async Task SubmitRotationAsync(BatchRotateWindow window)
    {
        var request = await ToolExecution.ShowAsync<BatchRotateRequest>(this, window);
        if (request is null || _closing) return;
        var jobs = BatchRotate.CreateJobs(request, _jobs.Select(job => job.Output));
        OutputPreferences.Apply(jobs, _settings, _jobs.Select(job => job.Output), request.OutputToSource, request.SettingName);
        AddToolJobs(jobs, ToolExecution.StartImmediately(window));
    }

    private async Task ShowOrientationResultAsync(Job job)
    {
        var result = job.OrientationResult ?? await AiTaskResults.LoadAsync<OrientationTaskResult>(job.Output);
        if (result is null) throw new IOException("方向检测结果缺失，请重新检测。");
        var source = new FileInfo(job.Inputs[0]);
        if (!source.Exists || source.Length != result.SourceLength || source.LastWriteTimeUtc != result.SourceWriteUtc)
            throw new IOException("源视频发生变化，请重新检测方向。");
        var spec = job.Options.Orientation!;
        var window = CreateRotateWindow(job.Inputs, spec.OutputFolder);
        window.FindControl<Avalonia.Controls.ComboBox>("FormatCombo")!.SelectedItem = spec.Format;
        window.FindControl<Avalonia.Controls.CheckBox>("SourceOutputInput")!.IsChecked = spec.OutputToSource;
        window.FindControl<Avalonia.Controls.CheckBox>("SettingNameInput")!.IsChecked = spec.SettingName;
        window.ApplyDetection(job.Inputs[0], result);
        await SubmitRotationAsync(window);
    }

    private static bool HasAiResult(Job job) => job.FeatureId switch
    {
        "rotate" when job.Options.Orientation is not null => job.OrientationResult is not null || !Active(job) && File.Exists(job.Output),
        "media-ai" => job.MediaTagResult is not null || !Active(job) && File.Exists(job.HasInternalOutput ? job.Output : AiTaskResults.PathFor(job, "tags")),
        "person-clip" => job.PersonDetectionResult is not null || !Active(job) && File.Exists(job.HasInternalOutput ? job.Output : AiTaskResults.PathFor(job, "people")),
        "auto-subtitle" => job.SubtitleResult is not null || !Active(job) && File.Exists(AiTaskResults.PathFor(job, "subtitles")),
        _ => job.State == JobState.Completed && (File.Exists(job.Output) || Directory.Exists(job.Output))
    };
    private static bool Active(Job job) => job.State is JobState.Waiting or JobState.Paused or JobState.Running or JobState.Stopping;

    internal Task ShowAiTaskAsync(Job job)
    {
        if (!CanViewAiTask(job)) return Task.CompletedTask;
        if (CanViewClassificationTask(job)) return ShowClassificationTaskAsync(job);
        if (_aiTaskWindows.TryGetValue(job.Id, out var existing)) { existing.Show(); existing.Activate(); return Task.CompletedTask; }
        var window = new AiTaskWindow(job, () => HasAiResult(job), () => ShowAiResultAsync(job),
            () => { _queue.Stop(job); Save(); Refresh(); }, async () =>
            {
                if (job.State == JobState.Waiting) await StartQueueAsync([job]);
                else if (job.State == JobState.Paused) { job.State = JobState.Waiting; Save(); await StartQueueAsync([job]); }
                else if (CanRequeueTask(job)) await RestartTasksAsync([job]);
            }, () => job.State == JobState.Waiting ? CanStartTask(job) : CanRequeueTask(job), () => { _ = Configure(Catalog.Find(job.FeatureId)); });
        _aiTaskWindows[job.Id] = window;
        window.Closed += (_, _) => _aiTaskWindows.Remove(job.Id);
        window.Show(this);
        return Task.CompletedTask;
    }

    private async void ViewAiTaskClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { if (JobList.SelectedItem is Job job) await ShowAiTaskAsync(job); }

    private async Task ShowAiResultAsync(Job job)
    {
        try
        {
            if (!_jobs.Contains(job) || !HasAiResult(job)) return;
            if (job.Options.Orientation is not null) { await ShowOrientationResultAsync(job); return; }
            switch (Catalog.Find(job.FeatureId).Operation)
            {
                case Operation.MediaTag: await ShowMediaTagResultAsync(job); break;
                case Operation.PersonClip: await ShowPersonClipResultAsync(job); break;
                case Operation.Transcribe: await ShowSubtitleTaskResultAsync(job); break;
                case Operation.VideoSummary: await ShowSummaryResultAsync(job); break;
                default: if (File.Exists(job.Output) || Directory.Exists(job.Output)) Open(job.Output); break;
            }
        }
        catch (Exception error) { if (!_closing) await Ui.Message(this, "任务结果", error.Message); }
    }

    private async Task SubmitSpeechRequestAsync(ConversionRequest request)
    {
        var reserved = _jobs.Select(job => job.Output).ToArray();
        var jobs = ConversionBatch.CreateJobs(request.Feature, request.Files, request.OutputFolder, request.Options, request.InputOptions, reserved);
        OutputPreferences.Apply(jobs, _settings, reserved, request.OutputToSource, request.SettingName);
        foreach (var job in jobs.Where(job => job.Options.Transcription?.RecognitionOnly == true))
        {
            var speech = job.Options.Transcription!;
            speech.ReviewOutputFolder = request.OutputFolder; speech.ReviewOutputToSource = request.OutputToSource;
            job.Output = AiTaskResults.PathFor(job, "draft", "srt");
        }
        AddToolJobs(jobs, request.StartImmediately);
        await Task.CompletedTask;
    }

    private async Task ShowSubtitleTaskResultAsync(Job job)
    {
        var transcript = job.SubtitleResult ?? await AiTaskResults.LoadAsync<SubtitleTaskResult>(AiTaskResults.PathFor(job, "subtitles"));
        if (transcript is null) throw new IOException("字幕识别结果缺失，请重新运行任务。");
        job.SubtitleResult = transcript;
        var options = job.Options.Clone(); options.Format = job.HasInternalOutput ? "srt" : options.Format; options.Transcription ??= new();
        options.Transcription.RecognitionOnly = false; options.Transcription.ReviewedCues = transcript.Cues;
        options.Transcription.ReviewedSourceLength = transcript.SourceLength; options.Transcription.ReviewedSourceWriteUtc = transcript.SourceWriteUtc;
        var folder = job.Options.Transcription?.ReviewOutputFolder ?? Path.GetDirectoryName(job.Output)!;
        var request = new ConversionRequest(Catalog.Find("auto-subtitle"), job.Inputs, folder, options,
            OutputToSource: job.Options.Transcription?.ReviewOutputToSource ?? false);
        var window = new SubtitleReviewWindow(Engine, request, false, SubmitSpeechRequestAsync, async draft =>
        {
            if (Active(job) || !ReferenceEquals(job.SubtitleResult, transcript)) return;
            await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "subtitles"), draft, CancellationToken.None);
            transcript = draft; job.SubtitleResult = draft;
        });
        _subtitleReviewWindows.Add(window); window.Closed += (_, _) => _subtitleReviewWindows.Remove(window);
        var export = await ToolExecution.ShowAsync<ConversionRequest>(this, window);
        if (export is not null && !_closing) await SubmitSpeechRequestAsync(export);
    }
}
