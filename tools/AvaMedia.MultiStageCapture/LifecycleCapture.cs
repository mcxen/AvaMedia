using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.MultiStage;

/// <summary>Dark, zh-CN lifecycle fixtures using production page views; no queue, model inference or user state.</summary>
internal static class LifecycleCapture
{
    private static readonly string[] States = ["process", "adjust", "merge", "done", "subtitle"];

    public static int Run(string folder, string[] states)
    {
        if (states.Length == 0) states = States;
        if (states.Any(state => !States.Contains(state))) { Console.Error.WriteLine("lifecycle state: process | adjust | merge | done | subtitle"); return 2; }
        folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
        var temporary = Directory.CreateTempSubdirectory("avamedia-lifecycle-capture-").FullName;
        var working = Environment.CurrentDirectory;
        var environment = new Dictionary<string, string?>();
        using var done = new CancellationTokenSource();
        var code = 0;
        try
        {
            var ffmpeg = Tool("AVAMEDIA_CAPTURE_FFMPEG", "ffmpeg");
            var ffprobe = Tool("AVAMEDIA_CAPTURE_FFPROBE", "ffprobe");
            foreach (var (key, name) in new[] { ("XDG_DATA_HOME", "data"), ("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"), ("AVAMEDIA_STATE_DIRECTORY", "state") })
            {
                environment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, Directory.CreateDirectory(Path.Combine(temporary, name)).FullName);
            }
            Environment.CurrentDirectory = temporary;
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .With(PlatformAppearance.FontOptions()).SetupWithoutStarting();
            Localization.Apply("zh-CN"); Skin.Apply("Dark"); Motion.SetReducedMotion(true);
            Dispatcher.UIThread.Post(async () =>
            {
                try { await RenderAsync(temporary, folder, states, ffmpeg, ffprobe); }
                catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
                finally { done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
        }
        catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
        finally
        {
            Environment.CurrentDirectory = working;
            foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
            try { Directory.Delete(temporary, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(error.Message); code = 1; }
        }
        return code;
    }

    private static string Tool(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Contains(Path.DirectorySeparatorChar) ? Path.GetFullPath(value) : value;
    }

    private static async Task RenderAsync(string temporary, string output, string[] states, string ffmpeg, string ffprobe)
    {
        var source = Path.Combine(temporary, "多页任务 · 截图夹具.mp4");
        using (var process = new Process { StartInfo = new(ffmpeg) { UseShellExecute = false, RedirectStandardError = true } })
        {
            foreach (var arg in new[] { "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15:duration=80", "-an", "-c:v", "libx264", "-threads", "1", "-preset", "ultrafast", "-crf", "32", source }) process.StartInfo.ArgumentList.Add(arg);
            process.Start(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var engine = new MediaEngine(new() { FFmpegPath = ffmpeg, FFprobePath = ffprobe, Language = "zh-CN", Theme = "Dark", CheckForUpdates = false, AutoUpdate = false, AutoDetectGpu = false });
        new Storage().SaveSettings(engine.Settings);
        var info = await engine.Probe(source);
        var frame = await engine.Thumbnail(source, 10, 640, 360, pad: false);
        foreach (var state in states)
        {
            var window = state == "subtitle" ? Subtitle(source, info, engine, temporary) : await PersonAsync(state, source, info, frame, engine, temporary);
            try
            {
                window.Title += " · 截图夹具";
                window.Show();
                // Allow asynchronous preview decoding and embedded editor initialization; no interaction or output assertion.
                await Task.Delay(1800);
                var size = new Size(1240, 800);
                window.Measure(size); window.Arrange(new Rect(size)); window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 800), new Vector(96, 96));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, $"lifecycle-{state}-dark.png"));
                Console.WriteLine($"Captured lifecycle/{state} (illustrative fixture)");
            }
            finally { var closed = WindowChrome.WhenClosed(window); window.Close(); await closed.WaitAsync(TimeSpan.FromSeconds(15)); }
        }
    }

    private static async Task<Window> PersonAsync(string state, string source, MediaInfo info, byte[] frame, IMediaEngine engine, string folder)
    {
        var job = new Job { FeatureId = "person-clip", Inputs = [source], Output = Path.Combine(folder, "保留片段 · 截图夹具.mp4"),
            Options = new() { Format = "mp4", PersonClip = new() { ExportPreset = QuickClipBatch.DefaultPreset, Detection = new(DetectorIds: [ModelCatalog.NanoDetId]) } },
            State = state == "process" ? JobState.Running : state == "done" ? JobState.Completed : JobState.AwaitingUser };
        var design = new DesignPersonClipTask(state == "process" ? DesignPersonClipTask.Scenario.Running : DesignPersonClipTask.Scenario.Completed, source, info, frame, folder);
        var segments = design.View.Segments.Select(segment => new ConversionOptions { Start = segment.Start, End = segment.End }).ToArray();
        var samples = Enumerable.Range(0, 120).Select(i => new PersonClipSample(i * info.Duration / 120, i % 4 == 0 ? .2f : .8f, i % 4 == 0 ? PersonClipSampleFlags.None : PersonClipSampleFlags.Person)).ToArray();
        var detected = new PersonClipResult(source, info, segments, samples.Length, 0, samples.Length, 0, "截图夹具") { Samples = samples, AnalyzedSeconds = info.Duration };
        var file = new FileInfo(source); job.PersonDetectionResult = new(detected, file.Length, file.LastWriteTimeUtc);
        await AiTaskResults.SaveAsync(AiTaskResults.PathFor(job, "people"), job.PersonDetectionResult, CancellationToken.None);
        using (var pages = new TaskLifecycle(job, PersonClipLifecycle.Pages))
        {
            pages.StartProcessing("process");
            if (state != "process") pages.CompleteProcessing("process", $"截图夹具 · {segments.Length} 个片段");
            if (state is "merge" or "done") pages.SubmitInteraction("adjust", new ClipEditResult(source, info, segments));
            if (state == "done")
            {
                File.Copy(source, job.Output, true); // Preview-only fixture, never represented as a verified merge output.
                pages.QueueInteractionWork("merge", new PersonClipMergeChoice(SourceOutput.Original, false));
                pages.StartProcessing("merge"); pages.CompleteProcessing("merge", "截图夹具 · 无转场");
            }
        }
        job.Lifecycle = job.Lifecycle! with { Pages = job.Lifecycle!.Pages.Select(page => page.State == PageState.Completed
            ? page with { Elapsed = TimeSpan.FromSeconds(page.Page.Id == "process" ? 18.4 : page.Page.Id == "merge" ? 4.1 : 0) } : page).ToArray() };
        var task = new PersonFixture(job, design);
        return new PersonClipLifecycleWindow(job, task, new PersonClipStageContent(task, engine, new()), engine, folder,
            () => Task.CompletedTask, () => Task.CompletedTask);
    }

    private static Window Subtitle(string source, MediaInfo info, IMediaEngine engine, string folder)
    {
        // Source has no audio: subtitle text is explicitly illustrative and no recognizer is run.
        var design = new DesignSubtitleTask(DesignStage.Completed, source, info, folder);
        var job = new Job { FeatureId = "auto-subtitle", Inputs = [source], State = JobState.AwaitingUser,
            Output = Path.Combine(folder, "字幕 · 截图夹具.srt"), Options = new() { Format = "srt", Transcription = new() } };
        using (var pages = new TaskLifecycle(job, SubtitleLifecycle.Pages))
        { pages.StartProcessing("process"); pages.CompleteProcessing("process", $"截图夹具 · {design.View.Lines.Count} 条字幕"); }
        var task = new SubtitleFixture(design);
        return new SubtitleLifecycleWindow(job, task, new SubtitleStageContent(task, engine, new()), engine, folder,
            () => Task.CompletedTask, () => Task.CompletedTask);
    }

    private sealed class SubtitleFixture(DesignSubtitleTask design) : ISubtitleTask
    {
        public string FeatureId => design.FeatureId;
        public IReadOnlyList<TaskStageDescriptor> Stages => design.Stages;
        public SubtitleTaskView View => design.View;
        public MultiStageTaskSnapshot Snapshot => design.Snapshot with { State = MultiStageTaskState.AwaitingUser, Commands = new() { CanStop = true } };
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default) => design.ReadLogAsync(ct);
        public Task StartAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RerunAsync() => Task.CompletedTask;
        public Task RetryFromStageAsync(string stageId) => Task.CompletedTask;
        public Task ChangeOutputFolderAsync(string folder) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class PersonFixture(Job job, DesignPersonClipTask design) : IPersonClipTask
    {
        public string FeatureId => design.FeatureId;
        public IReadOnlyList<TaskStageDescriptor> Stages => design.Stages;
        public PersonClipTaskView View => design.View with { OutputPath = job.Output, OutputExists = job.State == JobState.Completed, OutputBytes = File.Exists(job.Output) ? new FileInfo(job.Output).Length : null };
        public MultiStageTaskSnapshot Snapshot => design.Snapshot with { // Overall lifecycle state comes from the job, not the completed analysis fixture.
            State = job.State switch { JobState.AwaitingUser => MultiStageTaskState.AwaitingUser, JobState.Completed => MultiStageTaskState.Completed, _ => MultiStageTaskState.Running },
            Progress = job.State == JobState.AwaitingUser ? 0 : design.Snapshot.Progress,
            IsClockRunning = job.State == JobState.Running,
            CompletedUtc = job.State == JobState.Completed ? design.Snapshot.CompletedUtc : null,
            Commands = new() { CanStop = job.State is JobState.Running or JobState.AwaitingUser, CanRerun = job.State == JobState.Completed } };
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<TaskLogEntry>> ReadLogAsync(CancellationToken ct = default) => design.ReadLogAsync(ct);
        public Task StartAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RerunAsync() => Task.CompletedTask;
        public Task RetryFromStageAsync(string stageId) => Task.CompletedTask;
        public Task ChangeOutputFolderAsync(string folder) => Task.CompletedTask;
        public void Dispose() { }
    }
}
