using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.PersonClipAdjust;

/// <summary>Illustrative detection data, real generated media and real IPersonClipEditSession. Never connects to X.</summary>
internal static class AdjustCapture
{
    private static readonly string[] States = ["default", "drag", "hover", "menu", "stale"];
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly double[][] Ranges = [
        [0,1], [1.8125,3.5], [4.4375,30.5], [36.25,41], [44.875,52.125], [58.5,66.25], [71,79.375],
        [85.125,88.5], [94.25,101.875], [107.5,112], [118.625,126.25], [131,135.5], [141.375,149],
        [155.25,159.5], [166,172.25], [178.875,188.766], [192,198.5], [199.25,203.75], [210,214.5], [220.125,226], [238.5,242.25] ];

    public static int Run(string output, string state)
    {
        if (!States.Contains(state)) { Console.Error.WriteLine("adjust state: default | drag | hover | menu | stale"); return 2; }
        output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var working = Environment.CurrentDirectory;
        var temporary = Directory.CreateTempSubdirectory("avamedia-adjust-capture-").FullName;
        var environment = new Dictionary<string, string?>();
        using var done = new CancellationTokenSource();
        try
        {
            var ffmpeg = CaptureTool("AVAMEDIA_CAPTURE_FFMPEG", "ffmpeg");
            var ffprobe = CaptureTool("AVAMEDIA_CAPTURE_FFPROBE", "ffprobe");
            // Set these before App initializes Storage or any static preview cache. The explicit
            // state-directory override takes precedence over XDG in production and must be isolated too.
            foreach (var (key, folder) in new[] { ("XDG_DATA_HOME", "data"), ("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"), ("AVAMEDIA_STATE_DIRECTORY", "state") })
            {
                environment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, Directory.CreateDirectory(Path.Combine(temporary, folder)).FullName);
            }
            Environment.CurrentDirectory = temporary;
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .With(PlatformAppearance.FontOptions()).SetupWithoutStarting();
            Localization.Apply("zh-CN"); Skin.Apply("Dark"); Motion.SetReducedMotion(true);
            Application.Current!.Styles.Add(new Style(selector => selector.OfType<Popup>()) { Setters = { new Setter(Popup.ShouldUseOverlayLayerProperty, true) } });
            Dispatcher.UIThread.Post(async () =>
            {
                try { await RenderAsync(temporary, output, state, ffmpeg, ffprobe); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            return Environment.ExitCode;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            Environment.CurrentDirectory = working;
            foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
            try { Directory.Delete(temporary, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"Unable to remove capture directory {temporary}: {error.Message}"); }
        }
    }

    private static string CaptureTool(string variable, string fallback)
    {
        var command = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(command)) return fallback;
        return command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(command) : command;
    }

    private static async Task RenderAsync(string temporary, string output, string state, string ffmpeg, string ffprobe)
    {
        var video = Path.Combine(temporary, "调整片段截图夹具.mp4");
        // A small synthetic source; the detector is not run. 15 fps makes the frame chip deterministic.
        await CommandAsync(ffmpeg, "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15:duration=252.38", "-an", "-c:v", "libx264", "-threads", "1", "-preset", "ultrafast", "-crf", "32", "-pix_fmt", "yuv420p", video);
        var engine = new MediaEngine(new AppSettings { Language = "zh-CN", Theme = "Dark", FFmpegPath = ffmpeg, FFprobePath = ffprobe, AutoDetectGpu = false, CheckForUpdates = false, AutoUpdate = false });
        var info = await engine.Probe(video, CancellationToken.None);
        var detection = new PersonClipOptions(FramesPerSecond: 1.5, DetectorIds: [ModelCatalog.NanoDetId], ExcludedRanges: [new(226.5, 236)]);
        var samples = new List<PersonClipSample>();
        for (var i = 0; i / 1.5 < info.Duration; i++)
        {
            var time = i / 1.5; var person = Ranges.Any(range => time >= range[0] && time <= range[1]);
            var dark = time is >= 31 and < 34; var excluded = time is >= 226.5 and < 236;
            var uncertain = !dark && !excluded && i % 31 == 7;
            var score = dark || excluded ? 0 : uncertain ? .34 : person ? .68 + .25 * Math.Abs(Math.Sin(i * 1.71)) : .04 + .15 * Math.Abs(Math.Sin(i * 1.3));
            // A whole low-confidence segment as well as isolated uncertain samples.
            if (time is >= 131 and <= 135.5) { score = .38; uncertain = true; }
            var flags = (person ? PersonClipSampleFlags.Person : 0) | (uncertain ? PersonClipSampleFlags.Uncertain : 0)
                | (dark ? PersonClipSampleFlags.Dark : 0) | (excluded ? PersonClipSampleFlags.Excluded : 0)
                | (person && i % 11 == 0 ? PersonClipSampleFlags.Boundary : 0);
            samples.Add(new(time, (float)score, flags));
        }
        var analysed = samples.Count(s => s.Analysed);
        var result = new PersonClipResult(video, info, Ranges.Select(range => new ConversionOptions { Start = range[0], End = range[1] }).ToArray(),
            samples.Count, samples.Count(s => s.Flags.HasFlag(PersonClipSampleFlags.Uncertain)), analysed,
            samples.Count(s => s.Flags.HasFlag(PersonClipSampleFlags.Boundary)), "截图夹具")
        { Samples = samples, AnalyzedSeconds = info.Duration, DarkFrames = samples.Count(s => s.Flags.HasFlag(PersonClipSampleFlags.Dark)), ExcludedSeconds = 9.5,
            Detectors = [new(ModelCatalog.NanoDetId, "NanoDet · 截图夹具", analysed, samples.Count(s => s.Flags.HasFlag(PersonClipSampleFlags.Person)), "截图夹具", null)] };
        var source = new FileInfo(video);
        var job = new Job { FeatureId = "person-clip", Inputs = [video], State = JobState.Completed, Duration = info.Duration,
            Options = new() { Format = "json", PersonClip = new() { Detection = detection, AnalysisOnly = true } },
            PersonDetectionResult = new(result, source.Length, source.LastWriteTimeUtc) };
        job.Output = AiTaskResults.InternalOutputFor(job);
        if (state == "stale") File.SetLastWriteTimeUtc(video, source.LastWriteTimeUtc.AddMinutes(1));
        var session = await PersonClipEditing.OpenAsync(job);
        session.SetBounds(session.Segments[13].Id, 155.6, 159.5);
        session.SetKeep(session.Segments[17].Id, false); session.Add(205, 207.6);
        await session.SaveAsync();
        var host = new PersonClipAdjustHost(engine, Path.Combine(temporary, "保留片段"), false, PersonClipTaskOptions.DefaultExportPreset, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var window = new PersonClipAdjustWindow(session, host) { Width = 1240, Height = 800, SizeToContent = SizeToContent.Manual };
        try
        {
            window.ShowIndependent(); window.UpdateLayout();
            var frame = Field<Image>(window, "_frame");
            // Let LoadMediaAsync finish its initial seek before preparing the selected segment.
            await UntilAsync(() => Field<Playback?>(window, "_player") is not null && frame.Source is not null, "initial preview");
            var previousFrame = frame.Source;
            var boundsImages = Field<Image[]>(window, "_boundsImages");
            var previousBounds = boundsImages.Select(image => image.Source).ToArray();
            var selected = session.Segments.First(s => s.Start == 178.875);
            Call(window, "Select", selected.Id, true, true); Call(window, "SeekTo", 183.4);
            var timeline = Field<AdjustTimeline>(window, "_timeline");
            timeline.Zoom = session.Duration / 66; timeline.Reveal(150, 216);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            await UntilAsync(() => frame.Source is not null && !ReferenceEquals(frame.Source, previousFrame)
                && boundsImages.Select((image, index) => image.Source is not null && !ReferenceEquals(image.Source, previousBounds[index])).All(ready => ready)
                && !Field<bool>(window, "_loadingThumbs") && StripReady(window, timeline), "selected preview, bounds and visible thumbnails");
            window.UpdateLayout();
            switch (state)
            {
                case "hover":
                    var surface = Field<MediaPreviewSurface>(window, "_surface");
                    window.MouseMove(surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value);
                    await UntilAsync(() => surface.IsPointerOver, "preview hover");
                    break;
                case "menu":
                    var button = Field<Button>(window, "_moreButton");
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                    await UntilAsync(() => window.GetVisualDescendants().OfType<ContextMenu>().Any(menu => menu.IsOpen), "segment menu");
                    break;
                case "drag":
                    double X(double seconds) => AdjustTimeline.LabelWidth + (seconds - timeline.ViewStart) / timeline.VisibleDuration * (timeline.Bounds.Width - AdjustTimeline.LabelWidth);
                    var edge = timeline.TranslatePoint(new Point(X(selected.End), 92), window)!.Value;
                    var target = session.Snap(selected.End - .15, 1, selected.Id) ?? throw new InvalidOperationException("Fixture has no snap target");
                    var drag = timeline.TranslatePoint(new Point(X(target.Seconds + .01), 92), window)!.Value;
                    previousFrame = frame.Source;
                    window.MouseMove(edge); window.MouseDown(edge, MouseButton.Left); window.MouseMove(drag, RawInputModifiers.LeftMouseButton);
                    await UntilAsync(() => timeline.Dragging && Field<PersonClipSnap?>(timeline, "_dragSnap") is not null
                        && Math.Abs(timeline.DragPosition - target.Seconds) < 1e-6 && timeline.DragFrame is not null
                        && frame.Source is not null && !ReferenceEquals(frame.Source, previousFrame), "snapped end-handle preview");
                    break;
            }
            await Task.Delay(500); window.UpdateLayout();
            var size = new Size(1240, 800);
            if (window.ClientSize != size) throw new InvalidOperationException($"Unexpected adjust client size: {window.ClientSize}");
            window.InvalidateMeasure(); window.Measure(size); window.Arrange(new Rect(size)); window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize(1240, 800), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(output);
            Console.WriteLine($"Captured adjust/{state}: {output} (1240 × 800; illustrative detector fixture)");
        }
        finally
        {
            var closed = WindowChrome.WhenClosed(window); window.Close(); await closed.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
    // Keep capture-only access in this tool instead of adding screenshot state to the production window.
    private static T Field<T>(object owner, string name) => (T)(owner.GetType().GetField(name, Private)
        ?? throw new MissingFieldException(owner.GetType().FullName, name)).GetValue(owner)!;
    private static void Call(object owner, string name, params object[] arguments) => (owner.GetType().GetMethod(name, Private)
        ?? throw new MissingMethodException(owner.GetType().FullName, name)).Invoke(owner, arguments);
    private static bool StripReady(PersonClipAdjustWindow window, AdjustTimeline timeline)
    {
        var times = timeline.TileTimes();
        var frames = Field<Dictionary<long, Bitmap>>(window, "_stripFrames");
        return times.Count > 0 && times.All(at => frames.ContainsKey(AdjustTimeline.Key(at)));
    }
    private static async Task UntilAsync(Func<bool> ready, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException($"Adjust capture timed out waiting for {description}");
            await Task.Delay(100);
        }
    }
    private static async Task CommandAsync(string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start ffmpeg");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        if (process.ExitCode != 0) throw new IOException(await error);
        await error;
    }
}
