using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop;

/// <summary>Native X11 capture of the actual review window, with illustrative cues and a supplied video.</summary>
internal static class SubtitleOutputCapture
{
    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--subtitle-output-capture");
        if (index + 2 >= args.Length) throw new ArgumentException("Specify output directory and source video.");
        var output = Path.GetFullPath(args[index + 1]);
        var source = Path.GetFullPath(args[index + 2]);
        Directory.CreateDirectory(output);
        var previous = Environment.GetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY");
        var state = Directory.CreateTempSubdirectory("avamedia-subtitle-capture-").FullName;
        Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", state);
        using var done = new CancellationTokenSource();
        var exitCode = 0;
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().With(PlatformAppearance.FontOptions()).SetupWithoutStarting();
            Localization.Apply("zh-CN"); Skin.Apply("Dark"); Motion.SetReducedMotion(true);
            Dispatcher.UIThread.Post(async () =>
            {
                SubtitleReviewWindow? window = null;
                try
                {
                    var file = new FileInfo(source);
                    var options = new ConversionOptions
                    {
                        Format = "srt", SubtitleFontSize = 48, SubtitleMargin = 36,
                        Transcription = new()
                        {
                            ReviewedSourceLength = file.Length, ReviewedSourceWriteUtc = file.LastWriteTimeUtc,
                            ReviewedCues = [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "示意字幕：先选择输出方式"),
                                new(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(6), "字体和位置随输出格式生效"),
                                new(TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(9), "校对后导出视频或字幕文件")]
                        }
                    };
                    var engine = new MediaEngine(new() { Language = "zh-CN", Theme = "Dark", AutoDetectGpu = false, CheckForUpdates = false, AutoUpdate = false });
                    window = new(engine, new(Catalog.All.Single(f => f.Id == "auto-subtitle"), [source], output, options), false)
                    { Title = "字幕校对（截图夹具：示意字幕）", Position = new(0, 0) };
                    window.Show();
                    await Task.Delay(1600);
                    var mode = window.GetVisualDescendants().OfType<SegmentedControl>().Single(c => c.Name == "SubtitleOutputMode");
                    await SaveAsync(window, output, "burn");
                    mode.SelectedIndex = 1;
                    await Task.Delay(200);
                    var format = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "SubtitleOutputFormat");
                    format.SelectedIndex = 0;
                    await SaveAsync(window, output, "srt");
                    format.SelectedIndex = 1;
                    await SaveAsync(window, output, "ass");
                }
                catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
                finally { window?.Close(); done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            return exitCode;
        }
        finally
        {
            Environment.SetEnvironmentVariable("AVAMEDIA_STATE_DIRECTORY", previous);
            Directory.Delete(state, true);
        }
    }

    private static async Task SaveAsync(Window window, string output, string mode)
    {
        await Task.Delay(400);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        var path = Path.Combine(output, $"subtitle-output-{mode}-dark.png");
        bitmap.Save(path); Console.WriteLine(path);
    }
}
