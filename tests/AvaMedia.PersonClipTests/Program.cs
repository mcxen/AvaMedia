using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

internal static class Program
{
    public static string Root = "";
    public static string Fixture => Path.Combine(Root, "mixed people O'Brien.mp4");
    public static AppSettings Settings => new()
    {
        FFmpegPath = "/opt/homebrew/bin/ffmpeg", FFprobePath = "/opt/homebrew/bin/ffprobe",
        AutoDetectGpu = false, EnableBetaFeatures = true, CheckForUpdates = false, AutoUpdate = false,
        CloseToTray = false, MinimizeToTray = false, NotifyComplete = false, OutputFolder = Path.Combine(Root, "output")
    };
    [STAThread]
    public static int Main(string[] args)
    {
        Root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/person-multidet");
        Directory.CreateDirectory(Root);
        var mode = args.FirstOrDefault() ?? "core";
        if (mode is not ("core" or "ui") && (!Enum.TryParse<PersonDetectionMode>(mode, out var selectedMode) || !Enum.IsDefined(selectedMode)))
        { Console.Error.WriteLine("Use core, ui, Balanced, Recall or Consensus."); return 1; }
        if (mode == "ui" && !File.Exists(Fixture))
        { Console.Error.WriteLine("Run core first to prepare the video fixture."); return 1; }
        if (mode == "ui")
            return AppBuilder.Configure<PersonTestApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        try { RunAsync(args.FirstOrDefault() ?? "core").GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { File.WriteAllText(Path.Combine(Root, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    public static void Check(bool passed, string label)
    { if (!passed) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); }
    private static async Task RunAsync(string modeFilter)
    {
        var store = new ModelStore();
        foreach (var model in PersonDetectorCatalog.All.Where(_ => modeFilter == "core"))
        {
            string? stage = null;
            await store.DownloadAsync(model.Id, new InlineProgress<ModelDownloadProgress>(value =>
            {
                if (value.Stage == stage) return;
                stage = value.Stage; Console.WriteLine(model.Name + " · " + value.Stage);
            }));
            Check(await store.IsInstalledAsync(model.Id, true), model.Name + " downloaded and SHA-256 verified");
        }
        var engine = new MediaEngine(Settings);
        var upstream = Path.Combine(Root, "upstream");
        Directory.CreateDirectory(upstream);
        using (var client = new HttpClient())
            foreach (var fixture in new[]
            {
                ("messi5.jpg", "football.jpg", "1d570e49654e84c7a943918537bd9e5e1ef82920152e147c834006e235be97c9"),
                ("fruits.jpg", "fruits.jpg", "9c031d80a1c52da5eca790db896baffec6a7e52bf786cdb7bbfca5c7f880e6a1"),
                ("vtest.avi", "pedestrians.avi", "45cddc9490be69345cbdab64ca583be65987e864ca408038e648db99e10516cf")
            })
            {
                var path = Path.Combine(upstream, fixture.Item2);
                if (!File.Exists(path))
                    await File.WriteAllBytesAsync(path, await client.GetByteArrayAsync("https://raw.githubusercontent.com/opencv/opencv/4.12.0/samples/data/" + fixture.Item1));
                Check(Hash(path).Equals(fixture.Item3, StringComparison.OrdinalIgnoreCase), "fixed public fixture SHA-256: " + fixture.Item2);
            }
        var inputs = new List<string> { "-v", "error", "-y" };
        var pieces = new[] { ("fruits.jpg", 2), ("football.jpg", 3), ("fruits.jpg", 2), ("football.jpg", 2), ("fruits.jpg", 2) };
        foreach (var (file, seconds) in pieces) inputs.AddRange(["-loop", "1", "-t", seconds.ToString(), "-i", Path.Combine(upstream, file)]);
        var filters = string.Join(';', Enumerable.Range(0, pieces.Length).Select(index =>
            $"[{index}:v]scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,fps=20,setsar=1,setpts=PTS-STARTPTS[v{index}]"))
            + ";" + string.Concat(Enumerable.Range(0, pieces.Length).Select(index => $"[v{index}]")) + "concat=n=5:v=1:a=0[v]";
        inputs.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-filter_complex", filters,
            "-map", "[v]", "-map", "5:a", "-t", "11", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", Fixture]);
        Check((await ProcessRunner.Run(engine.FFmpeg, inputs)).ExitCode == 0, "mixed people/no-people fixture with audio");
        var sourceHash = Hash(Fixture);
        var negative = Path.Combine(Root, "empty.mp4");
        Check((await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-y", "-loop", "1", "-i", Path.Combine(upstream, "fruits.jpg"),
            "-t", "2", "-vf", "scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2", "-c:v", "libx264", "-pix_fmt", "yuv420p", negative])).ExitCode == 0, "negative fixture");
        var report = new List<object>();
        var all = PersonDetectorCatalog.All.Select(model => model.Id).ToArray();
        var options = new PersonClipOptions(PaddingSeconds: .1, MergeGapSeconds: .25, MinimumSeconds: .2,
            PreferGpu: false, ReuseSimilarFrames: false);
        foreach (var model in PersonDetectorCatalog.All.Where(_ => modeFilter == "core"))
        {
            var singleton = options with { DetectorIds = [model.Id] };
            var positive = await new PersonClipAnalysis(engine, store).AnalyzeAsync(Fixture, singleton);
            Check(positive.Detectors.Single().Evaluations == positive.InferredFrames + positive.BoundaryFrames,
                model.Name + " executes real inference on sampled and boundary frames");
            Check(positive.Segments.Count == 2 && Covers(positive, 3) && Covers(positive, 8) && !Covers(positive, 1) && !Covers(positive, 6) && !Covers(positive, 10),
                model.Name + " distinguishes true people intervals from identical no-people frames");
            var empty = await new PersonClipAnalysis(engine, store).AnalyzeAsync(negative, singleton);
            Check(empty.Segments.Count == 0, model.Name + " rejects no-people footage");
            report.Add(new { Case = model.Name, Positive = positive, Negative = empty });
        }
        PersonClipResult? combined = null;
        foreach (var mode in Enum.GetValues<PersonDetectionMode>().Where(mode => modeFilter == "core" || mode.ToString() == modeFilter))
        {
            var updates = new List<PersonClipProgress>();
            var result = await new PersonClipAnalysis(engine, store).AnalyzeAsync(Fixture, options with { DetectorIds = all, DetectionMode = mode },
                new InlineProgress<PersonClipProgress>(updates.Add));
            File.WriteAllText(Path.Combine(Root, mode + ".json"), JsonSerializer.Serialize(new
            {
                Result = result, Evidence = updates.Where(value => value.Evidence.Count > 0).Select(value => new { value.Seconds, value.Evidence }).DistinctBy(value => value.Seconds)
            }, new JsonSerializerOptions { WriteIndented = true }));
            Check(result.Detectors.Count == 3 && result.Detectors.All(model => model.Evaluations == result.InferredFrames + result.BoundaryFrames),
                mode + " evaluates all three selected detectors");
            Check(result.Segments.Count == 2 && Covers(result, 2.5) && Covers(result, 4.5) && Covers(result, 7.5) && Covers(result, 8.5)
                && !Covers(result, 1) && !Covers(result, 6) && !Covers(result, 10), mode + " keeps both people intervals and removes empty intervals");
            Check(Math.Abs(result.Segments[0].Start - 2) < .4 && Math.Abs(result.Segments[0].End - 5) < .4
                && Math.Abs(result.Segments[1].Start - 7) < .4 && Math.Abs(result.Segments[1].End - 9) < .4, mode + " refines actual cut boundaries");
            Check(updates.Any(value => value.Evidence.Count == 3) && updates.Any(value => value.Activity?.Preview is { Length: > 0 }),
                mode + " reports actual detector evidence and decoded previews");
            if (mode == PersonDetectionMode.Balanced) combined = result;
            report.Add(new { Case = mode.ToString(), Result = result });
        }
        if (modeFilter != "core") return;
        var lightweight = await new PersonClipAnalysis(engine, store).AnalyzeAsync(Fixture,
            options with { PreferGpu = true, ReuseSimilarFrames = true });
        Check(lightweight.Detectors.Count == 2 && lightweight.Detectors.All(model => model.Evaluations > 0)
            && lightweight.Segments.Count == 2, "default lightweight combination and acceleration/fallback");
        report.Add(new { Case = "LightweightGPU", Result = lightweight });
        var motion = Path.Combine(Root, "pedestrian-motion.mp4");
        Check((await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-y", "-ss", "0", "-i", Path.Combine(upstream, "pedestrians.avi"),
            "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", motion])).ExitCode == 0, "public moving pedestrians fixture");
        var pedestrians = await new PersonClipAnalysis(engine, store).AnalyzeAsync(motion,
            options with { DetectorIds = all, DetectionMode = PersonDetectionMode.Recall });
        Check(pedestrians.Segments.Count > 0, "moving distant pedestrians detected");
        report.Add(new { Case = "MovingPedestrians", Result = pedestrians });
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                await new PersonClipAnalysis(engine, store).AnalyzeAsync(Fixture, options, new InlineProgress<PersonClipProgress>(value =>
                { if (value.Stage == "扫描视频" && value.Evidence.Count > 0) cancellation.Cancel(); }), cancellation.Token);
                throw new InvalidOperationException("Expected cancelled analysis");
            }
            catch (OperationCanceledException) { Check(true, "cancel during real multi-model inference"); }
        }
        foreach (var model in PersonDetectorCatalog.All)
        { using var lease = await store.AcquireAsync(model.Id); Check(true, model.Name + " lease released after cancellation"); }
        // Loading a later locked detector must dispose sessions and release earlier acquired leases.
        using (var locked = await store.AcquireAsync(ModelCatalog.PersonId))
        {
            try { await new PersonClipAnalysis(engine, store).AnalyzeAsync(Fixture, options with { DetectorIds = all }); throw new Exception("Expected busy model"); }
            catch (InvalidOperationException error) when (error.Message.Contains("模型正在")) { Check(true, "busy selected model reported"); }
        }
        foreach (var id in options.SelectedDetectors) { using var lease = await store.AcquireAsync(id); Check(true, "partial initialization releases " + id); }
        var decision = PersonDetectionPolicy.Decide([new("a", .4, .35, "CPU"), new("b", .1, .5, "CPU")], PersonDetectionMode.Consensus, false);
        Check(!decision.Keep && decision.Uncertain, "single unconfirmed vote is not retained by cross confirmation");
        var output = await ExportAsync(combined!);
        Check(Hash(Fixture) == sourceHash, "source video remains byte-identical");
        report.Add(new { Case = "Export", output });
        File.WriteAllText(Path.Combine(Root, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task<string> ExportAsync(PersonClipResult result)
    {
        var engine = new MediaEngine(Settings); Directory.CreateDirectory(Settings.OutputFolder);
        var edits = new[] { new ClipEditResult(result.Path, result.Info, result.Segments) };
        var export = new ConversionOptions { VideoCodec = "libx264", AudioCodec = "aac", Quality = 24 };
        var joined = QuickClipWorkflow.PrepareJoinedJobs(edits, "MP4", export, Settings.OutputFolder, false, "People").Single();
        await engine.Execute(joined, _ => { }, CancellationToken.None);
        await ValidateOutput(joined.Output, result.Segments.Sum(segment => segment.End - segment.Start));
        var separate = QuickClipBatch.CreateJobs(QuickClipWorkflow.PrepareExports(edits, "MP4", export), Settings.OutputFolder, settingName: "People-Part");
        for (var index = 0; index < separate.Count; index++)
        {
            await engine.Execute(separate[index], _ => { }, CancellationToken.None);
            await ValidateOutput(separate[index].Output, result.Segments[index].End - result.Segments[index].Start);
        }
        return joined.Output;
        async Task ValidateOutput(string path, double expected)
        {
            var info = await engine.Probe(path);
            Check(info.HasVideo && info.HasAudio && Math.Abs(info.Duration - expected) < .15, "exported video/audio and duration: " + Path.GetFileName(path));
            Check((await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-i", path, "-f", "null", "-"])).ExitCode == 0,
                "export fully decodes: " + Path.GetFileName(path));
        }
    }
    private static bool Covers(PersonClipResult result, double time) => result.Segments.Any(segment => segment.Start <= time && segment.End >= time);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

public sealed class PersonTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        foreach (var name in new[] { "Localization", "UiStyles", "Motion", "ControlRoles", "CategoryNavigation", "EditorStyles", "PlayerStyles" })
            Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/")) { Source = new Uri($"avares://AvaMedia.Desktop/Styles/{name}.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var owner = new Window { Title = "AvaMedia Person Clip E2E", Width = 440, Height = 160 };
            owner.Content = new TextBlock { Text = "人物剪辑端到端验证", Margin = new(24) }; desktop.MainWindow = owner;
            owner.Opened += async (_, _) =>
            {
                try
                {
                    var window = new PersonClipWindow(new MediaEngine(Program.Settings), Program.Settings, [Program.Fixture], _ => Task.CompletedTask);
                    var dialog = window.ShowDialog<IReadOnlyList<ClipEditResult>?>(owner);
                    await WaitAsync(() => FindButton("AnalyzePersonClips").IsEnabled);
                    var models = window.GetVisualDescendants().OfType<CheckBox>().Where(control => control.Name?.StartsWith("Detector_") == true).ToArray();
                    Program.Check(models.Length == 3 && models.Count(model => model.IsChecked == true) == 2, "native window defaults to lightweight detectors");
                    foreach (var model in models) model.IsChecked = false;
                    Program.Check(!FindButton("AnalyzePersonClips").IsEnabled, "native window blocks empty selection");
                    foreach (var model in models) model.IsChecked = true;
                    window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "PersonDetectionMode").SelectedIndex = (int)PersonDetectionMode.Consensus;
                    Click("AnalyzePersonClips");
                    await WaitAsync(() => FindButton("StopPersonClips").IsVisible);
                    Click("StopPersonClips");
                    await WaitAsync(() => FindButton("AnalyzePersonClips").IsEnabled);
                    Program.Check(!FindButton("ExportPersonClips").IsEnabled, "native stop cancels analysis without exporting partial decisions");
                    Click("AnalyzePersonClips");
                    await WaitAsync(() => FindButton("ExportPersonClips").IsEnabled);
                    Program.Check(window.GetVisualDescendants().OfType<TextBox>().Any(control => control.Text?.Contains("MediaPipe") == true
                        && control.Text.Contains("NanoDet") && control.Text.Contains("YOLOX")), "native analysis displays all three actual detector statistics");
                    // Parameter changes must invalidate the previous export rather than exporting stale decisions.
                    window.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "PersonDetectionMode").SelectedIndex = (int)PersonDetectionMode.Balanced;
                    Program.Check(!FindButton("ExportPersonClips").IsEnabled, "native strategy change invalidates prior export");
                    Click("AnalyzePersonClips");
                    await WaitAsync(() => FindButton("ExportPersonClips").IsEnabled);
                    Click("ExportPersonClips");
                    var edits = await dialog;
                    Program.Check(edits is { Count: 1 } && edits[0].Segments.Count == 2, "native analyze/edit/export returns two retained intervals");
                    var edit = edits![0];
                    var engine = new MediaEngine(Program.Settings);
                    var editor = new EditorWindow(engine, edit.Path, edit.Segments[0], "quick-workflow", edit.Segments);
                    var editing = editor.ShowDialog<ClipEditResult?>(owner);
                    await editor.Ready;
                    Program.Check(editor.ReadClipEdit().Segments.Count == 2, "native editor receives both analysis intervals");
                    editor.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    edit = (await editing)!;
                    var state = new ClipExportState("MP4", Program.Settings.OutputFolder, false,
                        new() { VideoCodec = "libx264", AudioCodec = "aac", Quality = 24 }, JoinSegments: true);
                    var exporter = new ClipExportWindow([edit], state.Folder, state, allowJoin: true);
                    var exporting = exporter.ShowDialog<ClipExportDecision?>(owner);
                    Program.Check(exporter.ReadState().JoinSegments && exporter.FindControl<Button>("JoinQueueButton")!.IsEnabled,
                        "native export dialog accepts merged people intervals");
                    exporter.FindControl<Button>("JoinQueueButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var decision = (await exporting)!;
                    Program.Check(decision.Request is not null && !decision.BackToEditing, "native export returns queue request");
                    var job = QuickClipWorkflow.PrepareJoinedJobs([edit], decision.State.Preset, decision.State.Options,
                        decision.Request!.OutputFolder, decision.Request.OutputToSource, "Native-People").Single();
                    await engine.Execute(job, _ => { }, CancellationToken.None);
                    var output = job.Output; var info = await engine.Probe(output);
                    Program.Check(info.HasAudio && info.HasVideo && Math.Abs(info.Duration - edit.Segments.Sum(segment => segment.End - segment.Start)) < .15,
                        "native analyze/editor/export/execute retains expected audio and duration");
                    Program.Check((await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-i", output, "-f", "null", "-"])).ExitCode == 0,
                        "native workflow output fully decodes");
                    File.WriteAllText(Path.Combine(Program.Root, "ui.json"), JsonSerializer.Serialize(new { Passed = true, Output = output, Segments = edit.Segments.Count }));
                    desktop.Shutdown(0);
                    Button FindButton(string name) => window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name);
                    void Click(string name) => FindButton(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error)
                { File.WriteAllText(Path.Combine(Program.Root, "ui-error.txt"), error.ToString()); Console.Error.WriteLine(error); desktop.Shutdown(1); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    private static async Task WaitAsync(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!completed()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Native UI did not complete"); await Task.Delay(100); }
    }
}
