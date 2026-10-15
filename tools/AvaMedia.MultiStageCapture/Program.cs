using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.MultiStage;

// Renders the multi-stage 保留有人片段 window from DesignPersonClipTask fixtures (illustrative data, not detection
// output) and grabs each window from the X display. Usage: AvaMedia.MultiStageCapture <output> <video> [Dark|Light] [state...]
internal static class Program
{
    private static readonly CancellationTokenSource Lifetime = new();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "seed") return Seed(args[1], args[2..]);
        if (args.Length >= 3 && args[0] == "real") return Real(args[1..]);
        if (args.Length < 2) { Console.Error.WriteLine("Usage: AvaMedia.MultiStageCapture <output> <video> [Dark|Light] [state...] | seed <output-folder> <video...>"); return 2; }
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Localization.Apply("zh-CN");
        Skin.Apply(args.ElementAtOrDefault(2) ?? "Dark");
        var states = args.Length > 3 ? args[3..].Select(Enum.Parse<DesignPersonClipTask.Scenario>).ToArray() : Enum.GetValues<DesignPersonClipTask.Scenario>();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(output, Path.GetFullPath(args[1]), (args.ElementAtOrDefault(2) ?? "Dark").ToLowerInvariant(), states); }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            finally { Lifetime.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(Lifetime.Token); return Environment.ExitCode;
    }

    // Writes waiting 保留有人片段 jobs into the queue of the current XDG data home so the real app can open them
    // in the multi-stage window. Run only against a disposable data home.
    private static int Seed(string folder, string[] videos)
    {
        Directory.CreateDirectory(folder);
        var storage = new Storage();
        var jobs = storage.LoadJobs();
        foreach (var video in videos.Select(Path.GetFullPath))
            jobs.Add(new Job
            {
                FeatureId = "person-clip", Inputs = [video], Output = Path.Combine(Path.GetFullPath(folder), Path.GetFileNameWithoutExtension(video) + ".mp4"),
                Options = new() { Format = "mp4", PersonClip = new() { Detection = new(FramesPerSecond: 1.5, DetectorIds: [ModelCatalog.NanoDetId]) } }
            });
        storage.SaveJobs(jobs);
        Console.WriteLine($"Seeded {videos.Length} job(s)");
        return 0;
    }

    // Real backend: PersonClipTaskSession over a QueueService running NanoDet on the given video. States are reached by
    // issuing the window's own commands (start, pause, resume, stop); an optional truncated copy produces a real failure.
    private static int Real(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Localization.Apply("zh-CN"); Skin.Apply("Dark");
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RealAsync(output, Path.GetFullPath(args[1]), args.Length > 2 ? Path.GetFullPath(args[2]) : null); }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            finally { Lifetime.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(Lifetime.Token); return Environment.ExitCode;
    }

    private static async Task RealAsync(string output, string video, string? broken)
    {
        var engine = new MediaEngine(new AppSettings { Language = "zh-CN", CheckForUpdates = false, AutoUpdate = false, FFmpegPath = Environment.GetEnvironmentVariable("AVAMEDIA_CAPTURE_FFMPEG") ?? "" });
        var results = Path.Combine(output, "real-output"); Directory.CreateDirectory(results);
        Job NewJob(string source, string name) => new()
        {
            FeatureId = "person-clip", Inputs = [source], Output = Path.Combine(results, name + ".mp4"),
            Options = new() { Format = "mp4", PersonClip = new() { Detection = new(FramesPerSecond: 1.5, DetectorIds: [ModelCatalog.NanoDetId]) } }
        };
        // 1. configuring → running → paused → completed on one job.
        var (window, session, host) = Open(NewJob(video, "real-full"), engine);
        await Task.Delay(3000); await Grab(window, Path.Combine(output, "real-configuring-dark.png"));
        await session.StartAsync();
        await Until(() => session.View.AnalyzedSeconds > (session.View.Info?.Duration ?? 60) * .45 && session.Snapshot.State == MultiStageTaskState.Running, 600);
        await Task.Delay(600); await Grab(window, Path.Combine(output, "real-running-dark.png"));
        await session.PauseAsync();
        await Until(() => session.Snapshot.State == MultiStageTaskState.Paused, 30);
        await Task.Delay(1200); await Grab(window, Path.Combine(output, "real-paused-dark.png"));
        await session.ResumeAsync();
        await Until(() => session.Snapshot.State is MultiStageTaskState.Completed or MultiStageTaskState.Failed or MultiStageTaskState.Cancelled, 900);
        await Task.Delay(4000); await Grab(window, Path.Combine(output, "real-completed-dark.png"));
        window.Close(); await host.Finished;
        // 2. cancelled mid-detection.
        (window, session, host) = Open(NewJob(video, "real-cancel"), engine);
        await session.StartAsync();
        await Until(() => session.View.AnalyzedSeconds > (session.View.Info?.Duration ?? 60) * .4, 600);
        await session.StopAsync();
        await Until(() => session.Snapshot.State is MultiStageTaskState.Cancelled or MultiStageTaskState.Failed or MultiStageTaskState.Completed, 120);
        await Task.Delay(3000); await Grab(window, Path.Combine(output, "real-cancelled-dark.png"));
        window.Close(); await host.Finished;
        if (broken is null) return;
        // 3. truncated source: decoding stops partway through.
        (window, session, host) = Open(NewJob(broken, "real-broken"), engine);
        await session.StartAsync();
        await Until(() => session.Snapshot.State is MultiStageTaskState.Failed or MultiStageTaskState.Completed or MultiStageTaskState.Cancelled, 900);
        await Task.Delay(3000); await Grab(window, Path.Combine(output, $"real-{session.Snapshot.State.ToString().ToLowerInvariant()}-broken-dark.png"));
        window.Close(); await host.Finished;
    }

    private static (MultiStageTaskWindow, PersonClipTaskSession, CaptureHost) Open(Job job, MediaEngine engine)
    {
        var host = new CaptureHost(new QueueService(engine));
        var session = new PersonClipTaskSession(job, host);
        var actions = new PersonClipTaskActions(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new MultiStageTaskWindow(session, new PersonClipStageContent(session, engine, actions), "保留有人片段");
        window.Show(); window.Activate();
        return (window, session, host);
    }

    private static async Task Until(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Capture state not reached"); await Task.Delay(200); }
    }

    private static async Task Grab(Window window, string path)
    {
        window.UpdateLayout(); await Task.Delay(300);
        var scale = window.RenderScaling; var position = window.Position; var size = window.FrameSize ?? window.ClientSize;
        await CommandAsync("python3", "-c", "from PIL import ImageGrab; import sys; x,y,w,h=map(int,sys.argv[1:5]); ImageGrab.grab(xdisplay=':11').crop((x,y,x+w,y+h)).save(sys.argv[5])",
            position.X.ToString(), position.Y.ToString(), ((int)Math.Round(size.Width * scale)).ToString(), ((int)Math.Round(size.Height * scale)).ToString(), path);
        Console.WriteLine("Captured " + path);
    }

    /// <summary>Minimal queue host for captures: one QueueService session per job; re-running is left to the app.</summary>
    private sealed class CaptureHost(QueueService queue) : IMultiStageQueueHost
    {
        private Task _run = Task.CompletedTask;
        public Task Finished => _run;
        public bool CanStart(Job job) => job.State == JobState.Waiting && !queue.IsRunning;
        public Task StartAsync(Job job) { if (CanStart(job)) _run = Task.Run(() => queue.Run([job])); return Task.CompletedTask; }
        public bool CanPause(Job job) => job.State == JobState.Running && queue.IsExecuting(job);
        public void Pause(Job job) => queue.Pause(job);
        public bool CanResume(Job job) => job.State == JobState.Paused && queue.IsExecuting(job);
        public Task ResumeAsync(Job job) { queue.Resume(job); return Task.CompletedTask; }
        public bool CanStop(Job job) => job.State is JobState.Running or JobState.Paused or JobState.Waiting;
        public void Stop(Job job) => queue.Stop(job);
        public bool CanRerun(Job job) => job.State is JobState.Completed or JobState.Failed or JobState.Cancelled;
        public Task RerunAsync(Job job, string? fromStageId) => throw new NotSupportedException("Capture host does not re-run tasks.");
        public bool CanChangeOutput(Job job) => job.State == JobState.Waiting;
        public Task ChangeOutputFolderAsync(Job job, string folder) => Task.CompletedTask;
    }

    private static async Task RunAsync(string output, string video, string theme, DesignPersonClipTask.Scenario[] states)
    {
        var engine = new MediaEngine(new AppSettings { Language = "zh-CN", CheckForUpdates = false, AutoUpdate = false, FFmpegPath = Environment.GetEnvironmentVariable("AVAMEDIA_CAPTURE_FFMPEG") ?? "" });
        var info = await engine.Probe(video, CancellationToken.None);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Movies", "AvaMedia", "保留片段");
        foreach (var state in states)
        {
            var at = info.Duration * state switch { DesignPersonClipTask.Scenario.Running or DesignPersonClipTask.Scenario.Paused => .62, DesignPersonClipTask.Scenario.Failed => .406, DesignPersonClipTask.Scenario.Cancelled => .455, _ => 0 };
            var frame = await engine.Thumbnail(video, at, 640, 360, CancellationToken.None, pad: false);
            var task = new DesignPersonClipTask(state, video, info, frame, folder);
            var actions = new PersonClipTaskActions(() => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
            var window = new MultiStageTaskWindow(task, new PersonClipStageContent(task, engine, actions), "保留有人片段");
            await CaptureAsync(window, Path.Combine(output, $"{state.ToString().ToLowerInvariant()}-{theme}.png"));
        }
    }

    private static async Task CaptureAsync(Window window, string path)
    {
        window.Show(); window.Activate(); await Task.Delay(2500); window.UpdateLayout(); await Task.Delay(300);
        // Crop the native frame Avalonia reports; xdotool geometry of reparented windows can be frame-relative.
        var scale = window.RenderScaling; var position = window.Position;
        var size = window.FrameSize ?? window.ClientSize;
        var (w, h) = ((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
        await CommandAsync("python3", "-c", "from PIL import ImageGrab; import sys; x,y,w,h=map(int,sys.argv[1:5]); ImageGrab.grab(xdisplay=':11').crop((x,y,x+w,y+h)).save(sys.argv[5])",
            position.X.ToString(), position.Y.ToString(), w.ToString(), h.ToString(), path);
        Console.WriteLine("Captured " + path);
        window.Close();
    }

    private static async Task<string> CommandAsync(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var text = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException(error); return text;
    }
}
