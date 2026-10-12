using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Classification;

internal static class Program
{
    private static readonly CancellationTokenSource Lifetime = new();
    private static readonly List<object> Captures = [];
    private static string _output = "";
    private static string _temporary = "";
    private static bool _headless;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--headless")
        {
            Console.Error.WriteLine("Usage: AvaMedia.AiProgressCapture <output-directory> [--headless]");
            return 2;
        }
        try
        {
            _output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(_output);
            _temporary = Directory.CreateTempSubdirectory("avamedia-ai-progress-").FullName;
            // Isolate application settings and thumbnail caches from the user's installation.
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_temporary, "data"));
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_temporary, "config"));
            Environment.SetEnvironmentVariable("DISPLAY", ":11");
            _headless = args.Contains("--headless");
            var builder = AppBuilder.Configure<App>();
            if (_headless) builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
            else builder.UsePlatformDetect();
            builder.LogToTrace().SetupWithoutStarting();
            Localization.Apply("zh-CN");
            Skin.Apply("Light");
            Motion.SetReducedMotion(true);
            Dispatcher.UIThread.Post(async () =>
            {
                try { await RunAsync(); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { Lifetime.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(Lifetime.Token);
            return Environment.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (_temporary.Length > 0)
                try { Directory.Delete(_temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task RunAsync()
    {
        var fixtures = new Fixtures(_temporary);
        var settings = new AppSettings { Theme = "Light", Language = "zh-CN", ReduceMotion = true,
            CheckForUpdates = false, AutoUpdate = false, AutoDownloadRepairModel = false };
        var engine = new FixtureEngine(settings, fixtures);
        var submission = new SpeechToolsWindow(engine, Catalog.Find("auto-subtitle"), "~/Movies/AvaMedia",
            fixtures.SpeechFiles, new ConversionOptions { Format = "srt", Transcription = new() { Model = SpeechModel.Base, Language = "zh" } },
            storage: new Storage(Path.Combine(_temporary, "settings")));
        await CaptureAsync(submission, "subtitle-submit-desktop", 752, 448);

        var job = fixtures.SpeechJob();
        Job[] queue = [job, new() { FeatureId = "auto-subtitle", Inputs = [fixtures.SpeechFiles[1]], State = JobState.Waiting },
            new() { FeatureId = "auto-subtitle", Inputs = [fixtures.SpeechFiles[2]], State = JobState.Waiting }];
        var progress = new AiTaskWindow(job, () => false, () => Task.CompletedTask, () => { },
            () => Task.CompletedTask, () => false, () => { }, pause: () => { }, queueSummary: () => AiQueueInfo.Summarize(queue));
        await CaptureAsync(progress, "subtitle-progress-desktop", 848, 560, async () =>
        {
            // The window's asynchronous source lookup is finished before injecting the synthetic envelope.
            // Reflection is confined to this capture harness; the production API stays unchanged.
            await (Task)Method(progress, "LoadSpeechMediaAsync").Invoke(progress, null)!;
            Field(progress, "_envelope").SetValue(progress, fixtures.Envelope);
            Method(progress, "RefreshSpeechProgress").Invoke(progress, null);
        });

        var classification = new ClassificationRunWindow(fixtures.ClassificationJob(), _ => { }, _ => { }, (_, _) => { });
        await CaptureAsync(classification, "progress-from-tasklist-desktop", 832, 552, async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Field(classification, "_runProjection").GetValue(classification) is null)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Classification projection did not become ready.");
                await Task.Delay(50);
            }
            await SettleAsync(classification);
        });
        await File.WriteAllTextAsync(Path.Combine(_output, "captures.json"), JsonSerializer.Serialize(new
        {
            scale = 1, language = "zh-CN", display = _headless ? "headless-skia" : ":11", fixtureOnly = true,
            subtitleSegment = 3, subtitleSegments = 6, waitingSubtitleJobs = 2,
            classifiedFiles = 412, classificationFiles = 1284, captures = Captures
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(target.GetType().Name, name);
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(target.GetType().Name, name);

    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(500);
        window.UpdateLayout();
    }

    private static async Task CaptureAsync(Window window, string name, int width, int height, Func<Task>? prepare = null)
    {
        // Capture the requested logical client size, even where production minimums are larger.
        window.MinWidth = 0; window.MinHeight = 0;
        window.Width = width; window.Height = height;
        window.SizeToContent = SizeToContent.Manual;
        window.Show();
        try
        {
            await SettleAsync(window);
            if (prepare is not null) await prepare();
            await SettleAsync(window);
            var size = window.ClientSize;
            if (Math.Abs(size.Width - width) > .1 || Math.Abs(size.Height - height) > .1)
                throw new InvalidOperationException($"Unexpected client size for {name}: {size}");
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            var path = Path.Combine(_output, name + ".png");
            bitmap.Save(path);
            Captures.Add(new { file = name + ".png", width, height });
            Console.WriteLine($"Captured {path} ({width} × {height}, scale 1)");
        }
        finally { window.Close(); }
    }
}

internal sealed class FixtureEngine(AppSettings settings, Fixtures fixtures) : IMediaEngine
{
    public AppSettings Settings => settings;
    public string FFmpeg => "ffmpeg";
    public string FFprobe => "ffprobe";
    public Task Execute(Job job, Action<double> progress, CancellationToken ct) =>
        throw new InvalidOperationException("Capture fixtures cannot execute media jobs.");
    public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
    {
        ct.ThrowIfCancellationRequested();
        var index = Array.IndexOf(fixtures.SpeechFiles, path);
        return Task.FromResult(new MediaInfo(index switch { 1 => 3492, 2 => 1692, _ => 1600 },
            1920, 1080, true, true, "{}", "h264", "aac", 48000, 2, FrameRate: 25));
    }
    public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360,
        CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(fixtures.Covers[Math.Max(0, Array.IndexOf(fixtures.SpeechFiles, input)) % fixtures.Covers.Length]);
    }
    public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) =>
        Task.FromResult(Math.Max(0, seconds + direction / 25d));
}
