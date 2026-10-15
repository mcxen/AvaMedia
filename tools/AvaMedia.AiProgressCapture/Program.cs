using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;

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
        var paths = args.Where(arg => arg is not "--headless" and not "--gallery" and not "--gallery-view").ToArray();
        if (paths.Length > 1 || paths.Any(arg => arg.StartsWith("--", StringComparison.Ordinal))
            || args.Count(arg => arg == "--headless") > 1 || args.Count(arg => arg is "--gallery" or "--gallery-view") > 1)
        {
            Console.Error.WriteLine("用法：AvaMedia.AiProgressCapture [截图目录] [--headless] [--gallery | --gallery-view]");
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
            var builder = AppBuilder.Configure<GalleryApp>();
            if (_headless) builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
            else builder.UsePlatformDetect();
            builder.LogToTrace().SetupWithoutStarting();
            Localization.Apply("zh-CN");
            Skin.Apply(args.Contains("--gallery-view") ? "Light" : "Dark");
            Motion.SetReducedMotion(true);
            Dispatcher.UIThread.Post(async () =>
            {
                try { if (args.Contains("--gallery-view")) await ShowGalleryAsync(); else await CaptureGalleryAsync(); }
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
        foreach (var theme in new[] { "Dark" })
        {
            Skin.Apply(theme);
            for (var i = 0; i < 12; i++)
            {
                var window = ComponentGallery.Create(i);
                await CaptureAsync(window, $"s{i + 1:00}-{theme.ToLowerInvariant()}", (int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height));
            }
        }
    }

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
