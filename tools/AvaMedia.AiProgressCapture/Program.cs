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
        var paths = args.Where(arg => arg is not "--headless" and not "--classification" and not "--gallery" and not "--gallery-view").ToArray();
        if (paths.Length > 1 || paths.Any(arg => arg.StartsWith("--", StringComparison.Ordinal))
            || args.Count(arg => arg == "--headless") > 1 || args.Count(arg => arg is "--classification" or "--gallery" or "--gallery-view") > 1)
        {
            Console.Error.WriteLine("用法：AvaMedia.AiProgressCapture [截图目录] [--headless] [--classification | --gallery | --gallery-view]");
            return 2;
        }
        var workingDirectory = Environment.CurrentDirectory;
        try
        {
            _output = Path.GetFullPath(paths.FirstOrDefault() ?? "/tmp/avamedia-ai-progress-v2/impl");
            Directory.CreateDirectory(_output);
            _temporary = Directory.CreateTempSubdirectory("avamedia-ai-progress-").FullName;
            // Isolate application settings and thumbnail caches from the user's installation.
            // GetFolderPath can return an empty string for a directory that does not exist,
            // making Storage.DefaultRoot (and thumb-cache) relative to the working directory.
            foreach (var (variable, folder) in new[] { ("XDG_DATA_HOME", "data"), ("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache") })
                Environment.SetEnvironmentVariable(variable, Directory.CreateDirectory(Environment.GetEnvironmentVariable(variable) ?? Path.Combine(_temporary, folder)).FullName);
            Environment.CurrentDirectory = _temporary;
            if (OperatingSystem.IsLinux() && Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) != Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
                throw new InvalidOperationException("截图数据目录必须与 XDG_DATA_HOME 一致。");
            Environment.SetEnvironmentVariable("DISPLAY", Environment.GetEnvironmentVariable("DISPLAY") ?? ":11");
            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", "1");
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", "1");
            _headless = args.Contains("--headless");
            var builder = (args.Contains("--gallery") || args.Contains("--gallery-view")) ? AppBuilder.Configure<GalleryApp>() : AppBuilder.Configure<App>();
            if (_headless) builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
            else builder.UsePlatformDetect();
            builder.LogToTrace().SetupWithoutStarting();
            Localization.Apply("zh-CN");
            Skin.Apply("Light");
            Motion.SetReducedMotion(true);
            Dispatcher.UIThread.Post(async () =>
            {
                try { if (args.Contains("--gallery-view")) await ShowGalleryAsync(); else if (args.Contains("--gallery")) await CaptureGalleryAsync(); else await RunAsync(args.Contains("--classification")); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { Lifetime.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(Lifetime.Token);
            return Environment.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            Environment.CurrentDirectory = workingDirectory;
            if (_temporary.Length > 0)
                try { Directory.Delete(_temporary, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task ShowGalleryAsync()
    {
        var window = ComponentGallery.CreateOverview();
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.SetResult();
        window.Show(); await closed.Task;
    }

    private static async Task CaptureGalleryAsync()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            Skin.Apply(theme);
            for (var i = 0; i < 12; i++)
            {
                var window = ComponentGallery.Create(i);
                await CaptureAsync(window, $"s{i + 1:00}-{theme.ToLowerInvariant()}", (int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height));
            }
        }
    }

    private static async Task RunAsync(bool classificationOnly)
    {
        var fixtures = new Fixtures(_temporary);
        var settings = new AppSettings { Theme = "Light", Language = "zh-CN", ReduceMotion = true,
            CheckForUpdates = false, AutoUpdate = false, AutoDownloadRepairModel = false };
        var engine = new FixtureEngine(settings, fixtures);
        if (!classificationOnly)
        {
        var submission = new SpeechToolsWindow(engine, Catalog.Find("auto-subtitle"), "~/Movies/AvaMedia",
            fixtures.SpeechFiles, new ConversionOptions { Format = "srt", Transcription = new() { Model = SpeechModel.Base, Language = "zh" } },
            storage: new Storage(Path.Combine(_temporary, "settings")));
        // Logical client dimensions from the .fw windows in the v2 HTML demos.
        await CaptureAsync(submission, "subtitle-submit-desktop", 940, 560);

        var job = fixtures.SpeechJob();
        Job[] queue = [job, new() { FeatureId = "auto-subtitle", Inputs = [fixtures.SpeechFiles[1]], State = JobState.Waiting },
            new() { FeatureId = "auto-subtitle", Inputs = [fixtures.SpeechFiles[2]], State = JobState.Waiting }];
        var progress = new AiTaskWindow(job, () => false, () => Task.CompletedTask, () => { },
            () => Task.CompletedTask, () => false, () => { }, pause: () => { }, queueSummary: () => AiQueueInfo.Summarize(queue));
        await CaptureAsync(progress, "subtitle-progress-desktop", 1060, 712, async () =>
        {
            // The window's asynchronous source lookup is finished before injecting the synthetic envelope.
            // Reflection is confined to this capture harness; the production API stays unchanged.
            await (Task)Method(progress, "LoadSpeechMediaAsync").Invoke(progress, null)!;
            Field(progress, "_envelope").SetValue(progress, fixtures.Envelope);
            Method(progress, "RefreshSpeechProgress").Invoke(progress, null);
        });
        }

        var classification = new ClassificationRunWindow(await fixtures.ClassificationJobAsync(), _ => { }, _ => { }, (_, _) => { });
        await CaptureAsync(classification, "progress-from-tasklist-desktop", 1040, 690, async () =>
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
            scale = 1, dpi = 96, language = "zh-CN", display = _headless ? "headless-skia" : Environment.GetEnvironmentVariable("DISPLAY"), fixtureOnly = true,
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
        try
        {
            window.Show();
            await SettleAsync(window);
            if (prepare is not null) await prepare();
            await SettleAsync(window);
            var size = window.ClientSize;
            if (Math.Abs(size.Width - width) > .1 || Math.Abs(size.Height - height) > .1)
                throw new InvalidOperationException($"Unexpected client size for {name}: {size}");
            // Lay out the entire client at its logical size before rendering. Never crop a
            // larger layout into a smaller bitmap or multiply dimensions by display scaling.
            window.InvalidateMeasure();
            window.Measure(size);
            window.Arrange(new Rect(size));
            window.UpdateLayout();
            if (window.Bounds.Size != size)
                throw new InvalidOperationException($"Unexpected layout size for {name}: {window.Bounds.Size}, client {size}");
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height), new Vector(96, 96));
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
