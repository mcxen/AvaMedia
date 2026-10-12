using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

internal static class Program
{
    private static readonly CancellationTokenSource Lifetime = new();
    private static string Output = "";
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: AvaMedia.PersonTaskCapture <output> <video> [result.json]"); return 2; }
        Output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Output);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Localization.Apply("zh-CN");
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(Path.GetFullPath(args[1]), args.ElementAtOrDefault(2)); }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            finally { Lifetime.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(Lifetime.Token); return Environment.ExitCode;
    }

    private static async Task RunAsync(string video, string? resultFile)
    {
        var engine = new MediaEngine(new AppSettings { Language = "zh-CN", CheckForUpdates = false, AutoUpdate = false, FFmpegPath = Environment.GetEnvironmentVariable("AVAMEDIA_CAPTURE_FFMPEG") ?? "" });
        var source = new FileInfo(video);
        var job = new Job { FeatureId = "person-clip", Inputs = [video], State = JobState.Running,
            Output = Path.Combine(Output, "people.json"), Options = new() { Format = "json", PersonClip = new() { Detection = new(Threshold: .1, ReuseSimilarFrames: false, DetectorIds: [ModelCatalog.NanoDetId]), AnalysisOnly = true } } };
        var clock = Stopwatch.StartNew();
        PersonClipResult result; PersonClipResult? partial = null;
        if (resultFile is not null)
        {
            var saved = await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(resultFile) ?? throw new IOException("Missing task result");
            result = saved.Result;
            if (File.Exists(Path.Combine(Output, "partial.json"))) partial = (await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(Path.Combine(Output, "partial.json")))?.Result;
        }
        else
        {
            result = await new PersonClipAnalysis(engine, new ModelStore(Environment.GetEnvironmentVariable("AVAMEDIA_CAPTURE_MODELS")))
                .AnalyzeAsync(video, job.Options.PersonClip!.Detection, new Observations(value =>
                {
                    if (value.PreviewResult is { IsPartial: true } observed && observed.AnalyzedSeconds < observed.Info.Duration * .8) partial = observed;
                    if (value.Activity is { } activity) job.Activity = activity;
                }));
        }
        clock.Stop();
        double? measuredElapsed = resultFile is null ? clock.Elapsed.TotalSeconds : null;
        if (resultFile is not null && File.Exists(Path.Combine(Output, "capture-source.json")))
        {
            using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Output, "capture-source.json")));
            if (previous.RootElement.TryGetProperty("elapsed", out var storedTime) && storedTime.TryGetDouble(out var time)) measuredElapsed = time;
        }
        job.PersonDetectionResult = new(result, source.Length, source.LastWriteTimeUtc);
        job.State = JobState.Completed; job.Progress = 100;
        if (measuredElapsed is { } elapsed) job.AppendLog($"人物检测耗时：{MediaEngine.Number(elapsed)} 秒");
        job.AppendLog($"检测帧数：{result.SampledFrames}；{result.Backend}");
        foreach (var segment in result.Segments) job.AppendLog($"保留 {MediaTime.Format(segment.Start)} — {MediaTime.Format(segment.End)}");
        await AiTaskResults.SaveAsync(job.Output, job.PersonDetectionResult, CancellationToken.None);
        if (partial is not null) await AiTaskResults.SaveAsync(Path.Combine(Output, "partial.json"), new PersonDetectionTaskResult(partial, source.Length, source.LastWriteTimeUtc), CancellationToken.None);
        foreach (var theme in new[] { "Light", "Dark" })
        {
            Skin.Apply(theme); await CaptureAsync(Create(job, engine), "completed-" + theme.ToLowerInvariant(), theme == "Light" && result.Segments.Count > 0 ? (result.Segments[0].Start + result.Segments[0].End) / 2 / result.Info.Duration : null);
        }
        if (partial is not null)
        {
            Skin.Apply("Light");
            job.PersonDetectionResult = new(partial, source.Length, source.LastWriteTimeUtc);
            job.State = JobState.Running; job.Progress = partial.AnalyzedSeconds / partial.Info.Duration * 80;
            job.ProgressDetail = "扫描视频"; job.Log = "";
            await CaptureAsync(Create(job, engine), "running-light");
            // Display the real partial observation under an explicit failure presentation fixture.
            job.State = JobState.Failed; job.Error = "截图状态演示：视频帧解码中断";
            await CaptureAsync(Create(job, engine), "failed-light");
        }
        await File.WriteAllTextAsync(Path.Combine(Output, "capture-source.json"), JsonSerializer.Serialize(new
        {
            video, resultFile, result.SampledFrames, segments = result.Segments.Count, elapsed = measuredElapsed,
            stateNote = "Completed results and partial coverage are actual NanoDet observations; failure reason is a labeled presentation fixture."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static AiTaskWindow Create(Job job, IMediaEngine engine) => new(job, () => job.PersonDetectionResult is not null,
        () => Task.CompletedTask, () => { }, () => Task.CompletedTask, () => true, () => { }, engine: engine);
    private static async Task CaptureAsync(Window window, string name, double? hoverFraction = null)
    {
        window.Show(); await Task.Delay(1800); window.UpdateLayout();
        var ids = await CommandAsync("xdotool", "search", "--onlyvisible", "--pid", Environment.ProcessId.ToString());
        var id = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        await CommandAsync("xdotool", "windowraise", id);
        await CommandAsync("python3", "-c", "from PIL import ImageGrab; import subprocess,sys; g=dict(l.split('=',1) for l in subprocess.check_output(['xdotool','getwindowgeometry','--shell',sys.argv[1]],text=True).splitlines()); x,y,w,h=[int(g[k]) for k in ['X','Y','WIDTH','HEIGHT']]; ImageGrab.grab(xdisplay=':11').crop((x,y,x+w,y+h)).save(sys.argv[2])", id, Path.Combine(Output, name + ".png"));
        Console.WriteLine("Captured " + name);
        if (hoverFraction is { } fraction)
        {
            var timeline = window.GetVisualDescendants().OfType<Control>().First(control => Avalonia.Automation.AutomationProperties.GetName(control) == "人物出现时间轴");
            var point = timeline.TranslatePoint(new Point(timeline.Bounds.Width * fraction, timeline.Bounds.Height / 2), window)!.Value;
            await CommandAsync("xdotool", "mousemove", "--window", id, ((int)point.X).ToString(), ((int)point.Y).ToString());
            await Task.Delay(350);
            await CommandAsync("python3", "-c", "from PIL import ImageGrab; import subprocess,sys; g=dict(l.split('=',1) for l in subprocess.check_output(['xdotool','getwindowgeometry','--shell',sys.argv[1]],text=True).splitlines()); x,y,w,h=[int(g[k]) for k in ['X','Y','WIDTH','HEIGHT']]; ImageGrab.grab(xdisplay=':11').crop((x,y,x+w,y+h)).save(sys.argv[2])", id, Path.Combine(Output, name + "-hover.png"));
            await CommandAsync("xdotool", "mousemove", "0", "0");
        }
        window.Close();
    }
    private static async Task<string> CommandAsync(string command, params string[] args)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new IOException(error); return output;
    }
    private sealed class Observations(Action<PersonClipProgress> report) : IProgress<PersonClipProgress>
    { public void Report(PersonClipProgress value) => report(value); }
}
