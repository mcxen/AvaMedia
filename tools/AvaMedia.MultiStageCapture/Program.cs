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
        if (args.Length < 2) { Console.Error.WriteLine("Usage: AvaMedia.MultiStageCapture <output> <video> [Dark|Light] [state...]"); return 2; }
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
