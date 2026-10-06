using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

var root = Path.GetFullPath("artifacts/batch-rotate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
var engine = new MediaEngine(new()); var checks = 0; var outputs = new List<string>();
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
ProcessResult Run(IEnumerable<string> args) => Task.Run(() => ProcessRunner.Run(engine.FFmpeg, args)).GetAwaiter().GetResult();
MediaInfo Probe(string path) => Task.Run(() => engine.Probe(path)).GetAwaiter().GetResult();
string Fixture(string name, int width, int height, bool audio)
{
    var path = Path.Combine(root, name + ".mp4");
    var args = new List<string> { "-v", "error", "-n", "-f", "lavfi", "-i", $"color=c=red:s={width}x{height}:r=25,drawbox=x={width / 2}:y=0:w={width / 2}:h={height}:color=blue:t=fill" };
    if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100"]);
    args.AddRange(["-t", "1.2", "-c:v", "mpeg4", "-q:v", "2", "-c:a", "aac", path]);
    var result = Run(args); Check(result.ExitCode == 0, "Fixture failed: " + result.Error); return path;
}
byte[] Frame(string path, int width, int height)
{
    var raw = Path.Combine(root, Guid.NewGuid() + ".rgb");
    Check(Run(["-v", "error", "-n", "-i", path, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw]).ExitCode == 0, "Frame decode failed.");
    var bytes = File.ReadAllBytes(raw); Check(bytes.Length == width * height * 3, "Decoded frame size differs from metadata."); return bytes;
}
(byte R, byte G, byte B) Pixel(byte[] frame, int width, int x, int y)
{ var i = (y * width + x) * 3; return (frame[i], frame[i + 1], frame[i + 2]); }
bool Similar((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) => Math.Abs(a.R - b.R) < 30 && Math.Abs(a.G - b.G) < 30 && Math.Abs(a.B - b.B) < 30;
var wide = Fixture("横屏 O'Brien 测试", 320, 180, true); var portrait = Fixture("竖屏", 180, 320, false);
var tagged = Path.Combine(root, "手机旋转标记.mp4");
Check(Run(["-v", "error", "-n", "-display_rotation", "90", "-i", wide, "-c", "copy", tagged]).ExitCode == 0, "Rotation metadata fixture failed.");
var sources = new[] { wide, portrait, tagged }; var infos = sources.ToDictionary(p => p, Probe);
Check(infos[tagged].Width == 180 && infos[tagged].Height == 320, "Tagged video display geometry not detected.");
var hashes = sources.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
var frames = sources.ToDictionary(p => p, p => Frame(p, infos[p].Width, infos[p].Height));
BatchRotateRequest Request(int rotation = 90, string format = "mp4", string? folder = null) =>
    new(sources.Select(p => new BatchRotateInput(p, infos[p])).ToArray(), rotation, format, folder ?? Path.Combine(root, "out-" + rotation));
void Reject(Action action, string message)
{
    try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException) { checks++; return; }
    throw new Exception(message);
}
foreach (var angle in new[] { 90, 180, 270 })
{
    var jobs = BatchRotate.CreateJobs(Request(angle));
    Check(jobs.Count == 3 && jobs.All(j => j.FeatureId == "rotate" && j.Options.Rotation == angle && !j.Options.CopyStreams && j.Options.VideoCodec != "copy"), "Batch rotation options not applied to each video.");
    Check(!ReferenceEquals(jobs[0].Options, jobs[1].Options), "Jobs share options.");
    new QueueService(engine).Run(jobs, 2).GetAwaiter().GetResult();
    foreach (var job in jobs)
    {
        Check(job.State == JobState.Completed, "Rotation job failed: " + job.Error); outputs.Add(job.Output);
        var source = infos[job.Inputs[0]]; var output = Probe(job.Output); var size = BatchRotate.OutputSize(source, angle);
        Check(output.Width == size.Width && output.Height == size.Height && Math.Abs(output.Duration - source.Duration) < .15 && output.HasAudio == source.HasAudio, "Rotation lost dimensions/duration/audio.");
        var result = Frame(job.Output, output.Width, output.Height);
        foreach (var (fx, fy) in new[] { (.25, .25), (.75, .25), (.25, .75), (.75, .75) })
        {
            var x = (int)(fx * output.Width); var y = (int)(fy * output.Height);
            var (sx, sy) = angle switch { 90 => (y, source.Height - 1 - x), 180 => (source.Width - 1 - x, source.Height - 1 - y), _ => (source.Width - 1 - y, x) };
            Check(Similar(Pixel(result, output.Width, x, y), Pixel(frames[job.Inputs[0]], source.Width, sx, sy)), "Actual pixels rotate in the wrong direction: " + job.Output);
        }
    }
}
Check(sources.All(p => hashes[p].SequenceEqual(SHA256.HashData(File.ReadAllBytes(p)))), "Sources were modified.");
foreach (var angle in new[] { -90, 45, 360 }) Reject(() => BatchRotate.CreateJobs(Request(angle)), "Invalid angle accepted.");
Check(BatchRotate.OutputSize(infos[wide], 0) == (320, 180), "No rotation swaps dimensions.");
Check(BatchRotate.CreateJobs(Request(0)).Count == 0, "Upright videos create unnecessary conversion jobs.");
Reject(() => BatchRotate.CreateJobs(Request() with { Inputs = [] }), "Empty batch accepted.");
Reject(() => BatchRotate.CreateJobs(Request() with { Inputs = [new(wide, infos[wide]), new(wide, infos[wide])] }), "Duplicate batch sources accepted.");
Reject(() => BatchRotate.CreateJobs(Request(format: "mp3")), "Audio output format accepted.");
Reject(() => BatchRotate.CreateJobs(Request() with { OutputFolder = "" }), "Empty output folder accepted.");
var invalidFolder = Path.Combine(root, "invalid-batch");
Reject(() => BatchRotate.CreateJobs(Request(folder: invalidFolder) with { Inputs = [new(wide, infos[wide]), new(portrait, infos[portrait] with { HasVideo = false })] }), "Non-video input accepted.");
Check(!Directory.Exists(invalidFolder), "Invalid batch created output directory.");
Reject(() => BatchRotate.CreateJobs(Request() with { Inputs = [new(Path.Combine(root, "missing.mp4"), infos[wide])] }), "Missing source accepted.");
var reserved = Path.Combine(root, Path.GetFileNameWithoutExtension(wide) + "_rotate90.mp4");
var collision = BatchRotate.CreateJobs(Request(folder: root), [reserved]);
Check(collision[0].Output.EndsWith("_rotate90 (1).mp4"), "Queued output collision not avoided.");
File.Copy(wide, reserved);
var sourceConflict = BatchRotate.CreateJobs(Request(folder: root) with { Inputs = [new(wide, infos[wide]), new(reserved, infos[wide])] });
Check(sourceConflict.All(j => j.Output != wide && j.Output != reserved), "Output can overwrite another source.");
var duplicateFolder = Path.Combine(root, "same-name"); Directory.CreateDirectory(duplicateFolder);
var sameName = Path.Combine(duplicateFolder, Path.GetFileName(wide)); File.Copy(wide, sameName);
Check(BatchRotate.CreateJobs(Request(folder: root) with { Inputs = [new(wide, infos[wide]), new(sameName, infos[wide])] }).Select(j => j.Output).Distinct().Count() == 2, "Same filenames collide.");

outputs.AddRange(OrientationChecks.Run(engine, root, wide, Check).GetAwaiter().GetResult());
outputs.AddRange(SourceVideoExportChecks.Run(engine, root, wide, tagged, Check).GetAwaiter().GetResult());
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true); Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
var mixed = new BatchRotateWindow(engine, root, [Path.Combine(root, "原属性 双音轨字幕.mov"), wide]); mixed.Show(); Pump(mixed.Ready);
var mixedJobs = BatchRotate.CreateJobs(mixed.CreateRequest());
Check(mixedJobs.Select(j => j.Options.Format).SequenceEqual(new[] { "mov", "mp4" }) && mixed.Entries[0].Status.EndsWith("MOV") && mixed.Entries[1].Status.EndsWith("MP4"),
    "Mixed-container UI does not preserve each video's original format.");
mixed.FindControl<ComboBox>("FormatCombo")!.SelectedItem = SourceVideoExport.FastRotation;
mixed.AddFiles([Path.Combine(root, "unsupported.mkv")]); Pump(mixed.Ready);
Check(!mixed.FindControl<Button>("OkButton")!.IsEnabled && mixed.Entries.Last().Status.Contains("Fast Copy"), "Unsupported Fast Copy format is silently queued.");
mixed.Entries.Last().Include = false;
Check(mixed.FindControl<Button>("OkButton")!.IsEnabled, "Unchecked unsupported format blocks Fast Copy batch.");
mixed.Close();
var window = new BatchRotateWindow(engine, root, sources); window.Show(); Pump(window.Ready);
var direction = window.FindControl<ComboBox>("DirectionCombo")!; var list = window.FindControl<ListBox>("FileList")!; var ok = window.FindControl<Button>("OkButton")!;
Check(window.Rotation == 90 && ok.IsEnabled && window.Entries.All(e => e.Info is not null), "Initial batch direction/readiness incorrect.");
Check(window.CreateRequest().Format == SourceVideoExport.Original && window.FindControl<TextBlock>("ExportHint")!.Text == SourceVideoExport.OriginalHint,
    "Rotation does not default to original container/attributes.");
window.FindControl<ComboBox>("FormatCombo")!.SelectedItem = SourceVideoExport.FastRotation;
Check(ok.IsEnabled && window.FindControl<TextBlock>("ExportHint")!.Text == SourceVideoExport.FastHint && BatchRotate.CreateJobs(window.CreateRequest()).All(j => j.Options.LosslessRotation == 90 && j.Options.CopyStreams),
    "Fast Copy selection does not reach queue options or explain compatibility.");
Capture(window, "batch-rotate-fastcopy.png");
window.FindControl<ComboBox>("FormatCombo")!.SelectedItem = SourceVideoExport.Original;
Check(ReferenceEquals(window.FindControl<Image>("SourceImage")!.Source, window.FindControl<Image>("ResultImage")!.Source), "Preview does not use the same source frame.");
foreach (var angle in new[] { 90, 270, 180 })
{
    direction.SelectedItem = BatchRotate.Direction(angle); var screenshot = Capture(window, "rotate-preview-" + angle + ".png");
    Check(window.CreateRequest().Rotation == angle && window.Entries.All(e => e.Status.StartsWith(BatchRotate.Direction(angle))), "Direction selection not shared with all videos.");
    Check(window.FindControl<LayoutTransformControl>("RotationTransform")!.LayoutTransform is RotateTransform t && t.Angle == angle, "Preview transform does not match output angle.");
    var size = BatchRotate.OutputSize(infos[wide], angle); var pane = window.FindControl<Border>("ResultPane")!;
    var pixels = Frame(screenshot, 1200, 760); var scale = Math.Min(pane.Bounds.Width / size.Width, pane.Bounds.Height / size.Height);
    foreach (var (fx, fy) in new[] { (.25, .25), (.75, .75) })
    {
        var x = (int)(fx * size.Width); var y = (int)(fy * size.Height);
        var p = pane.TranslatePoint(new Point((pane.Bounds.Width - size.Width * scale) / 2 + x * scale, (pane.Bounds.Height - size.Height * scale) / 2 + y * scale), window)!.Value;
        var (sx, sy) = angle switch { 90 => (y, infos[wide].Height - 1 - x), 180 => (infos[wide].Width - 1 - x, infos[wide].Height - 1 - y), _ => (infos[wide].Width - 1 - y, x) };
        Check(Similar(Pixel(pixels, 1200, (int)p.X, (int)p.Y), Pixel(frames[wide], infos[wide].Width, sx, sy)), "Rendered preview rotates/clips incorrectly.");
    }
}
list.SelectedIndex = 1; Pump(window.Ready);
Check(window.Rotation == 180 && window.FindControl<TextBlock>("ResultDescription")!.Text!.Contains("180 × 320"), "Preview switching changes direction or loses size.");
window.FindControl<Slider>("PreviewSeek")!.Value = .5; Pump(window.Ready);
Check(window.Rotation == 180 && !window.FindControl<TextBlock>("PreviewStatus")!.IsVisible, "Seeking loses direction or preview.");
window.Entries[1].Include = false;
Check(window.CreateRequest().Inputs.Count == 2 && window.Entries[1].Status == "不处理", "Unchecked video submitted.");
Check(window.AddFiles([wide]).Count == 0 && window.Entries.Count == 3, "Duplicate import added a row.");
var text = Path.Combine(root, "not-video.txt"); File.WriteAllText(text, "fixture");
Check(window.AddFiles([text, Path.Combine(root, "missing.mp4")]).Count == 2, "Unsupported inputs not filtered.");
var corrupt = Path.Combine(root, "损坏视频.mp4"); File.WriteAllText(corrupt, "invalid video"); window.AddFiles([corrupt]); Pump(window.Ready);
Check(!ok.IsEnabled && window.Entries.Last().Error is not null, "Corrupt media does not block submit."); window.Entries.Last().Include = false;
Check(ok.IsEnabled && window.CreateRequest().Inputs.Count == 2, "Unchecked corrupt input blocks valid batch.");
window.FindControl<TextBox>("OutputInput")!.Text = ""; Check(!ok.IsEnabled, "Empty output folder accepted by UI."); window.FindControl<TextBox>("OutputInput")!.Text = root;
Capture(window, "batch-rotate-light.png");
Application.Current.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs(); Capture(window, "batch-rotate-dark.png");
Check(window.FindControl<TextBlock>("ValidationText")!.Foreground is SolidColorBrush { Color.R: > 220 }, "Feedback does not follow dark theme.");
window.Width = 940; window.Height = 600; Capture(window, "batch-rotate-minimum.png", 940, 600);
Check(new Control[] { ok, direction, window.FindControl<TextBox>("OutputInput")! }.All(c => c.Bounds.Width > 20 && c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), window) is { } p && p.X <= 924.5 && p.Y <= 584.5), "Minimum window size hides/overflows controls.");
list.SelectedIndex = 3; Click(Button(window, "移除选中")); Check(window.Entries.Count == 3, "Remove selected failed.");
Click(Button(window, "全选")); Check(window.CreateRequest().Inputs.Count == 3, "Select all failed.");
window.Close(); Check(window.FindControl<Image>("SourceImage")!.Source is null && window.FindControl<Image>("ResultImage")!.Source is null, "Close does not release preview.");

var results = new Dictionary<string, VideoOrientationResult>
{
    [wide] = new(90, OrientationReliability.High, 8, 8, 8, "有效 8/8 帧。"),
    [portrait] = new(0, OrientationReliability.High, 8, 8, 8, "有效 8/8 帧。"),
    [tagged] = new(null, OrientationReliability.Unknown, 8, 0, 0, "没有人脸。")
};
var realAuto = new BatchRotateWindow(engine, root, [Path.Combine(root, "人脸 baked-90.mp4"), Path.Combine(root, "人脸 upright O'Brien.mp4")]);
realAuto.Show(); Pump(realAuto.Ready); Click(realAuto.FindControl<Button>("DetectButton")!); Pump(realAuto.DetectionReady);
Check(realAuto.Entries.Select(e => e.Rotation).SequenceEqual(new int?[] { 270, 0 }) && realAuto.FindControl<Button>("OkButton")!.IsEnabled, "Real detector does not update UI asynchronously.");
Check(realAuto.CreateRequest().Inputs.Single().Rotation == 270, "Real detection does not reach queue configuration.");
Capture(realAuto, "auto-orientation-real-face.png"); realAuto.Close();
var automatic = new BatchRotateWindow(engine, root, sources, new FixtureDetector(results)); automatic.Show(); Pump(automatic.Ready);
var autoList = automatic.FindControl<ListBox>("FileList")!; var autoDirection = automatic.FindControl<ComboBox>("DirectionCombo")!;
var autoOk = automatic.FindControl<Button>("OkButton")!;
Click(automatic.FindControl<Button>("DetectButton")!); Pump(automatic.DetectionReady);
Check(automatic.PerFile && automatic.Entries.Select(e => e.Rotation).SequenceEqual(new int?[] { 90, 0, null }), "Auto detection does not store independent corrections.");
Check(!autoOk.IsEnabled && automatic.Entries[1].Status.Contains("跳过") && automatic.Entries[2].Status.Contains("无法确定"), "Uncertain/unchanged states are not reflected in UI.");
Reject(() => automatic.CreateRequest(), "Uncertain direction is silently queued.");
autoList.SelectedIndex = 2; Pump(automatic.Ready);
Check(autoDirection.SelectedItem is null && automatic.FindControl<TextBlock>("ResultDescription")!.Text!.Contains("请手动"), "Unknown direction displays a selected angle.");
autoDirection.SelectedItem = BatchRotate.Direction(270);
var perFile = automatic.CreateRequest();
Check(autoOk.IsEnabled && perFile.Inputs.Count == 2 && perFile.Inputs.Select(i => i.Rotation).SequenceEqual(new int?[] { 90, 270 }), "Manual correction changes other videos or queues unchanged inputs.");
Check(automatic.Entries[2].Detection is null && automatic.Entries[0].Detection?.Rotation == 90, "Manual override does not clear only its own detection.");
automatic.AddFiles([sameName]); Pump(automatic.Ready);
Check(automatic.Entries.Last().Rotation is null && !autoOk.IsEnabled, "New import after auto detection silently inherits a rotation.");
automatic.Entries.Last().Include = false;
foreach (var (index, angle) in new[] { (0, 90), (1, 0), (2, 270) })
{
    autoList.SelectedIndex = index; Pump(automatic.Ready);
    Check(automatic.Rotation == angle && automatic.FindControl<LayoutTransformControl>("RotationTransform")!.LayoutTransform is RotateTransform transform && transform.Angle == angle, "Per-video preview uses another row's correction.");
}
autoList.SelectedIndex = 2; Pump(automatic.Ready); Capture(automatic, "auto-orientation-manual-review.png");
automatic.FindControl<ComboBox>("ModeCombo")!.SelectedIndex = 0;
autoDirection.SelectedItem = BatchRotate.Direction(180);
Check(automatic.CreateRequest().Inputs.Count == 3 && automatic.CreateRequest().Rotation == 180, "Switching to shared mode loses manual batch rotation.");
automatic.FindControl<ComboBox>("ModeCombo")!.SelectedIndex = 1;
Check(automatic.Entries.All(e => e.Rotation == 180), "Switching back to per-video mode restores stale angles.");
automatic.Close();

var uprightResults = sources.ToDictionary(p => p, p => new VideoOrientationResult(0, OrientationReliability.High, 8, 8, 8, "已正向。"));
var noChanges = new BatchRotateWindow(engine, root, sources, new FixtureDetector(uprightResults)); noChanges.Show(); Pump(noChanges.Ready); Pump(noChanges.DetectDirectionsAsync());
Check(!noChanges.FindControl<Button>("OkButton")!.IsEnabled && noChanges.Entries.All(e => e.Status.Contains("跳过")), "All-upright batch creates conversion tasks.");
Reject(() => noChanges.CreateRequest(), "No-op batch submits."); noChanges.Close();

var pendingDetector = new BlockingDetector();
var cancellable = new BatchRotateWindow(engine, root, sources, pendingDetector); cancellable.Show(); Pump(cancellable.Ready);
Click(cancellable.FindControl<Button>("DetectButton")!); PumpUntil(() => pendingDetector.Started);
Check(!cancellable.FindControl<Button>("OkButton")!.IsEnabled && !cancellable.FindControl<ComboBox>("DirectionCombo")!.IsEnabled && cancellable.FindControl<Button>("CancelDetectionButton")!.IsVisible, "Detection allows submit/angle mutation or hides stop.");
Click(cancellable.FindControl<Button>("CancelDetectionButton")!); Pump(cancellable.DetectionReady);
Check(cancellable.Entries.All(e => e.Rotation is null) && cancellable.FindControl<ComboBox>("DirectionCombo")!.IsEnabled && !cancellable.FindControl<Button>("CancelDetectionButton")!.IsVisible, "Cancellation queues default angles or leaves controls disabled.");
cancellable.Entries[1].Include = cancellable.Entries[2].Include = false;
cancellable.FindControl<ComboBox>("DirectionCombo")!.SelectedItem = BatchRotate.Direction(180);
Check(cancellable.CreateRequest().Inputs.Count == 1 && cancellable.CreateRequest().Inputs[0].Rotation == 180, "Manual recovery after cancellation fails."); cancellable.Close();

var partialDetector = new PartialDetector(); var partial = new BatchRotateWindow(engine, root, sources, partialDetector); partial.Show(); Pump(partial.Ready);
var partialTask = partial.DetectDirectionsAsync(); PumpUntil(() => partialDetector.Blocked); partial.CancelDetection(); Pump(partialTask);
Check(partial.Entries[0].Rotation == 90 && partial.Entries[0].Detection is not null && partial.Entries.Skip(1).All(e => e.Rotation is null), "Cancellation erases completed corrections or assumes unprocessed angles."); partial.Close();

var closeDetector = new BlockingDetector(); var closeDuringDetection = new BatchRotateWindow(engine, root, [wide], closeDetector);
closeDuringDetection.Show(); Pump(closeDuringDetection.Ready); var closingDetection = closeDuringDetection.DetectDirectionsAsync(); PumpUntil(() => closeDetector.Started);
closeDuringDetection.Close(); Pump(closingDetection);
Check(closeDuringDetection.FindControl<Image>("SourceImage")!.Source is null, "Closing during detection retains preview or fails cancellation.");

var isolated = new BatchRotateWindow(engine, root, sources, new FixtureDetector(results, wide)); isolated.Show(); Pump(isolated.Ready); Pump(isolated.DetectDirectionsAsync());
Check(isolated.Entries[0].Rotation is null && isolated.Entries[0].Status.Contains("检测失败") && isolated.Entries[1].Rotation == 0 && isolated.Entries[2].Detection is not null, "One detector error aborts the entire batch.");
isolated.Entries[0].Include = isolated.Entries[2].Include = false;
isolated.FindControl<ListBox>("FileList")!.SelectedIndex = 1; Pump(isolated.Ready);
isolated.FindControl<ComboBox>("DirectionCombo")!.SelectedItem = BatchRotate.Direction(90);
Check(isolated.CreateRequest().Inputs.Count == 1, "Failure isolation prevents manual queue creation."); isolated.Close();

var state = new Storage(Path.Combine(root, "isolated-state")); state.SaveSettings(new() { OutputFolder = Path.Combine(root, "main-queue"), ReduceMotion = true, NotifyComplete = false });
var main = new MainWindow(state); main.Show();
var tile = main.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tile") && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "批量旋转"));
Click(tile); var cancel = main.OwnedWindows.OfType<BatchRotateWindow>().Single(); Click(Button(cancel, "取消"));
Check(state.LoadJobs().Count == 0 && !cancel.IsVisible, "Tile/cancel submits unexpected tasks.");
var menu = main.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "视频批量旋转…"));
menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
var batch = main.OwnedWindows.OfType<BatchRotateWindow>().Single(); batch.AddFiles([wide, portrait]); Pump(batch.Ready);
batch.FindControl<ComboBox>("DirectionCombo")!.SelectedItem = BatchRotate.Direction(270); batch.FindControl<ComboBox>("FormatCombo")!.SelectedItem = "mkv";
Click(batch.FindControl<Button>("OkButton")!); var queueList = main.FindControl<ListBox>("JobList")!; var queue = queueList.Items.Cast<Job>().ToArray();
Check(queue.Length == 2 && queue.All(j => j.FeatureId == "rotate" && j.Options.Rotation == 270 && j.State == JobState.Waiting && j.Options.Format == "mkv"), "Main queue does not receive shared rotation/format.");
Check(state.LoadJobs().Count == 2, "Rotation jobs are not persisted.");
Click(main.FindControl<Button>("StartButton")!); PumpUntil(() => queue.All(j => j.State is JobState.Completed or JobState.Failed));
Check(queue.All(j => j.State == JobState.Completed), "Main Start failed to run rotation queue.");
foreach (var job in queue) { outputs.Add(job.Output); var info = Probe(job.Output); var size = BatchRotate.OutputSize(infos[job.Inputs[0]], 270); Check(info.Width == size.Width && info.Height == size.Height, "Main queue output dimensions incorrect."); }
queueList.SelectAll(); menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
var seeded = main.OwnedWindows.OfType<BatchRotateWindow>().Single(); Pump(seeded.Ready); Check(seeded.Entries.Count == 2, "Selected queue sources not seeded into batch rotation."); Click(Button(seeded, "取消"));
main.Close();
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks, outputs, sourceHashesVerified = true, allDirectionsPixelVerified = true, phoneRotationVerified = true, renderedPreviewPixelVerified = true, queueButtonsVerified = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} batch rotate checks / {outputs.Count} actual outputs. {root}");

Button Button(Window target, string content) => target.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
void PumpUntil(Func<bool> done)
{
    var deadline = DateTime.UtcNow.AddSeconds(25);
    while (!done()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > deadline) throw new TimeoutException("UI action timed out."); Thread.Sleep(5); }
    Dispatcher.UIThread.RunJobs();
}
string Capture(Window target, string name, int width = 1200, int height = 760)
{
    target.Measure(new Size(width, height)); target.Arrange(new Rect(0, 0, width, height)); Dispatcher.UIThread.RunJobs();
    var path = Path.Combine(root, name); using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96)); bitmap.Render(target); bitmap.Save(path); return path;
}

sealed class FixtureDetector(IReadOnlyDictionary<string, VideoOrientationResult> results, string? fail = null) : IVideoOrientationDetector
{
    public Task<VideoOrientationResult> DetectAsync(string path, MediaInfo info, IProgress<OrientationDetectionProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (path == fail) throw new InvalidDataException("模拟单文件检测错误");
        return Task.FromResult(results[path]);
    }
}

sealed class BlockingDetector : IVideoOrientationDetector
{
    public bool Started { get; private set; }
    public async Task<VideoOrientationResult> DetectAsync(string path, MediaInfo info, IProgress<OrientationDetectionProgress>? progress = null, CancellationToken ct = default)
    {
        Started = true; await Task.Delay(Timeout.Infinite, ct);
        return new(null, OrientationReliability.Unknown, 0, 0, 0, "取消");
    }
}

sealed class PartialDetector : IVideoOrientationDetector
{
    private int _calls;
    public bool Blocked { get; private set; }
    public async Task<VideoOrientationResult> DetectAsync(string path, MediaInfo info, IProgress<OrientationDetectionProgress>? progress = null, CancellationToken ct = default)
    {
        if (++_calls == 1) return new(90, OrientationReliability.High, 8, 8, 8, "已完成。");
        Blocked = true; await Task.Delay(Timeout.Infinite, ct);
        return new(null, OrientationReliability.Unknown, 0, 0, 0, "取消");
    }
}
