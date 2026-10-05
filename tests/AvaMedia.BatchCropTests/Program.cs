using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

var root = Path.GetFullPath("artifacts/batch-crop-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
var engine = new MediaEngine(new()); var checks = 0; var outputs = new List<string>();
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
ProcessResult Run(params string[] args) => Task.Run(() => ProcessRunner.Run(engine.FFmpeg, args)).GetAwaiter().GetResult();
string Fixture(string name, int width, int height, bool audio)
{
    var path = Path.Combine(root, name + ".mp4");
    var args = new List<string> { "-v", "error", "-n", "-f", "lavfi", "-i", $"color=c=red:s={width}x{height}:r=25,drawbox=x={width / 2}:y=0:w={width / 2}:h={height}:color=blue:t=fill" };
    if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100"]);
    args.AddRange(["-t", "1.2", "-c:v", "mpeg4", "-q:v", "2", "-c:a", "aac", path]);
    var result = ProcessRunner.Run(engine.FFmpeg, args).GetAwaiter().GetResult();
    Check(result.ExitCode == 0, "Fixture failed: " + result.Error); return path;
}
var small = Fixture("横屏 O'Brien 测试", 320, 180, true);
var large = Fixture("高分辨率", 640, 360, true);
var portrait = Fixture("竖屏", 180, 320, false);
var sources = new[] { small, large, portrait };
var hashes = sources.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
var infos = sources.ToDictionary(p => p, p => engine.Probe(p).GetAwaiter().GetResult());
var area = new CropArea(160, 20, 160, 100); var reference = infos[small];
BatchCropRequest Request(string folder, BatchCropMode mode = BatchCropMode.Pixels, params string[] files) =>
    new((files.Length == 0 ? [small, large] : files).Select(p => new BatchCropInput(p, infos[p])).ToArray(), area, reference, mode, new(), folder);
void Reject(Action action, string message)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception(message);
}
var pixelJobs = BatchCrop.CreateJobs(Request(Path.Combine(root, "pixels")));
Check(pixelJobs.Count == 2 && pixelJobs.All(j => j.FeatureId == "crop" && j.Options.CropX == 160 && j.Options.CropY == 20 && j.Options.CropWidth == 160 && j.Options.CropHeight == 100), "Shared pixel region not applied to all jobs.");
Check(!ReferenceEquals(pixelJobs[0].Options, pixelJobs[1].Options), "Jobs share mutable options.");
pixelJobs[0].Options.Quality = 5;
Check(pixelJobs[1].Options.Quality == 23, "Changing one job changes the batch.");
var relativeJobs = BatchCrop.CreateJobs(Request(Path.Combine(root, "relative"), BatchCropMode.Relative, small, large, portrait));
Check(relativeJobs[1].Options.CropX == 320 && relativeJobs[1].Options.CropY == 40 && relativeJobs[1].Options.CropWidth == 320 && relativeJobs[1].Options.CropHeight == 200, "Relative region does not scale correctly.");
Check(relativeJobs[2].Options.CropX == 90 && relativeJobs[2].Options.CropY == 34 && relativeJobs[2].Options.CropWidth == 90 && relativeJobs[2].Options.CropHeight == 178, "Portrait relative region is not even-aligned.");
var badFolder = Path.Combine(root, "invalid-batch");
Reject(() => BatchCrop.CreateJobs(Request(badFolder, BatchCropMode.Pixels, small, portrait)), "Out-of-bounds batch was accepted.");
Check(!Directory.Exists(badFolder), "Invalid batch created output directories before complete validation.");
Reject(() => BatchCrop.ValidateArea(new(1, 0, 160, 100), reference), "Odd crop coordinates accepted.");
Reject(() => BatchCrop.ValidateArea(new(0, 0, 0, 100), reference), "Zero-width crop accepted.");
Reject(() => BatchCrop.ValidateArea(new(int.MaxValue - 1, 0, 160, 100), reference), "Crop overflow accepted.");
Reject(() => BatchCrop.Resolve(new(0, 0, 2, 2), reference, infos[portrait], BatchCropMode.Relative), "Relative region rounding to zero width accepted.");
Reject(() => BatchCrop.Resolve(area, reference, reference, (BatchCropMode)9), "Unknown mapping mode accepted.");
Reject(() => BatchCrop.CreateJobs(Request(root) with { Inputs = [] }), "Empty batch accepted.");
Reject(() => BatchCrop.CreateJobs(Request(root, BatchCropMode.Pixels, small, small)), "Duplicate batch sources accepted.");
Reject(() => BatchCrop.CreateJobs(Request(root) with { Options = new() { CopyStreams = true } }), "Copy mode accepted with crop.");
Reject(() => BatchCrop.CreateJobs(Request(root) with { Options = new() { VideoCodec = "copy" } }), "Video copy accepted with crop.");
Reject(() => BatchCrop.CreateJobs(Request(root) with { Options = new() { Format = "mp3" } }), "Audio format accepted with crop.");
Reject(() => BatchCrop.CreateJobs(Request(root) with { OutputFolder = "" }), "Empty output folder accepted.");
var names = BatchCrop.CreateJobs(Request(root), [Path.Combine(root, Path.GetFileNameWithoutExtension(small) + "_crop.mp4")]);
Check(names[0].Output.EndsWith("_crop (1).mp4"), "Queued output names not reserved.");
var sameNameFolder = Path.Combine(root, "same-name"); Directory.CreateDirectory(sameNameFolder);
var sameName = Path.Combine(sameNameFolder, Path.GetFileName(small)); File.Copy(small, sameName);
var nameRequest = Request(root) with { Inputs = [new(small, reference), new(sameName, reference)] };
var nameJobs = BatchCrop.CreateJobs(nameRequest);
Check(nameJobs.Select(j => j.Output).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Same-name sources collide.");
var croppedSource = Path.Combine(root, Path.GetFileNameWithoutExtension(small) + "_crop.mp4"); File.Copy(small, croppedSource);
var sourceReserved = BatchCrop.CreateJobs(Request(root) with { Inputs = [new(small, reference), new(croppedSource, reference)] });
Check(sourceReserved.All(j => j.Output != croppedSource && j.Output != small), "An output can overwrite another batch source.");

var actualJobs = pixelJobs.Concat(relativeJobs).ToArray();
new QueueService(engine).Run(actualJobs, 2).GetAwaiter().GetResult();
foreach (var job in actualJobs)
{
    Check(job.State == JobState.Completed, "Queue job failed: " + job.Error);
    var output = engine.Probe(job.Output).GetAwaiter().GetResult(); outputs.Add(job.Output);
    Check(output.Width == job.Options.CropWidth && output.Height == job.Options.CropHeight && Math.Abs(output.Duration - 1.2) < .15, "Encoded crop dimensions/duration incorrect.");
    Check(output.HasAudio == infos[job.Inputs[0]].HasAudio, "Audio preservation incorrect.");
    var rgb = Path.Combine(root, Guid.NewGuid() + ".rgb");
    Check(Run("-v", "error", "-n", "-i", job.Output, "-frames:v", "1", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", rgb).ExitCode == 0, "Output could not be decoded.");
    var bytes = File.ReadAllBytes(rgb);
    // On the larger frame the unchanged pixel region is red; proportional mapping is blue.
    // Dimensions alone cannot prove the offset and mapping mode are correct.
    var red = job.Inputs[0] == large && job.Options.CropX == 160;
    Check(bytes.Length == 3 && bytes[1] < 35 && (red ? bytes[0] > 200 && bytes[2] < 35 : bytes[0] < 35 && bytes[2] > 200), "Encoded crop selected the wrong region: " + job.Output);
}
Check(sources.All(p => hashes[p].SequenceEqual(SHA256.HashData(File.ReadAllBytes(p)))), "Source media changed.");

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true);
Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
var window = new BatchCropWindow(engine, root, sources); window.Show(); Pump(window.Ready);
Check(window.Entries.All(e => e.Info is not null) && window.FindControl<Image>("PreviewImage")!.Source is not null, "Media info/preview did not load.");
var list = window.FindControl<ListBox>("FileList")!;
var mode = window.FindControl<ComboBox>("ModeCombo")!;
var ok = window.FindControl<Button>("OkButton")!;
var layer = window.FindControl<CropOverlay>("CropLayer")!;
Capture(window, "batch-crop-light.png");
Check(window.FindControl<TextBlock>("ValidationText")!.Foreground is SolidColorBrush { Color.A: > 0 }, "Validation feedback is invisible.");
Check(layer.Enabled && layer.Bounds.Width > 0, "Drawing surface not enabled.");
Point PixelPoint(double x, double y)
{
    var scale = Math.Min(layer.Bounds.Width / layer.SourceWidth, layer.Bounds.Height / layer.SourceHeight);
    var p = new Point((layer.Bounds.Width - layer.SourceWidth * scale) / 2 + x * scale, (layer.Bounds.Height - layer.SourceHeight * scale) / 2 + y * scale);
    return layer.TranslatePoint(p, window)!.Value;
}
window.FindControl<ComboBox>("CropRatio")!.SelectedItem="1:1";
window.MouseDown(PixelPoint(20.1,20.1),MouseButton.Left);window.MouseMove(PixelPoint(100.1,60.1));window.MouseUp(PixelPoint(100.1,60.1),MouseButton.Left);Dispatcher.UIThread.RunJobs();
Check(window.Area==new CropArea(20,20,40,40),"Batch drawing does not honor the same square ratio as the per-file editor.");
Click(window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"重置为当前全画面")));
window.MouseDown(PixelPoint(32.1, 20.1), MouseButton.Left);
window.MouseMove(PixelPoint(160.1, 120.1));
window.MouseUp(PixelPoint(160.1, 120.1), MouseButton.Left); Dispatcher.UIThread.RunJobs();
Check(window.Area == new CropArea(32, 20, 128, 100), "Actual pointer drag did not update crop coordinates.");
Check(window.CreateRequest().Inputs.Count == 3 && window.Entries.All(e => e.Status.StartsWith("选区")), "Shared drag not applied to included files.");
list.SelectedIndex = 1; Pump(window.Ready);
Check(window.Area == new CropArea(32, 20, 128, 100) && layer.Selection == new Rect(32, 20, 128, 100), "Preview switching mutates pixel region.");
mode.SelectedItem = BatchCropWindow.RelativeMode;
Check(layer.Selection == new Rect(64, 40, 256, 200), "Preview overlay differs from relative output.");
window.FindControl<Slider>("PreviewSeek")!.Value = .5; Pump(window.Ready);
Check(window.Area == new CropArea(32, 20, 128, 100) && layer.Enabled, "Seeking resets/disables the shared region.");
list.SelectedIndex = 0; Pump(window.Ready);
mode.SelectedItem = BatchCropWindow.PixelMode; window.SetArea(area);
Check(!ok.IsEnabled && window.Entries[2].Status.Contains("超出"), "Pixel bounds failure is not shown/blocking.");
Check(window.FindControl<TextBlock>("ValidationText")!.Classes.Contains("error"), "Invalid batch has no error style.");
mode.SelectedItem = BatchCropWindow.RelativeMode;
Check(ok.IsEnabled && window.Entries.All(e => e.Status.StartsWith("选区")), "Relative mode cannot recover mixed-size batch.");
window.Entries[2].Include = false;
Check(window.CreateRequest().Inputs.Count == 2 && window.Entries[2].Status == "不处理", "Unchecked files submitted.");
Check(window.AddFiles([small]).Count == 0 && window.Entries.Count == 3, "Duplicate import duplicated rows.");
var text = Path.Combine(root, "not-video.txt"); File.WriteAllText(text, "fixture");
Check(window.AddFiles([text, Path.Combine(root, "missing.mp4")]).Count == 2, "Unsupported/missing imports not filtered.");
var corrupt = Path.Combine(root, "损坏视频.mp4"); File.WriteAllText(corrupt, "invalid video");
window.AddFiles([corrupt]); Pump(window.Ready);
Check(!ok.IsEnabled && window.Entries.Last().Error is not null, "Unreadable media can be submitted.");
window.Entries.Last().Include = false;
Check(ok.IsEnabled && window.CreateRequest().Inputs.Count == 2, "Unreadable unchecked media blocks valid files.");
window.FindControl<NumericUpDown>("CropWidthInput")!.Value = 159;
Check(!ok.IsEnabled, "Odd numeric width accepted by UI."); window.SetArea(area);
window.FindControl<TextBox>("OutputInput")!.Text = "";
Check(!ok.IsEnabled, "Empty output folder accepted by UI."); window.FindControl<TextBox>("OutputInput")!.Text = root;
Capture(window, "batch-crop-light.png");
Application.Current.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs();
Capture(window, "batch-crop-dark.png");
Check(window.FindControl<TextBlock>("ValidationText")!.Foreground is SolidColorBrush { Color.R: > 220 }, "Feedback does not update to dark theme.");
window.Width = 940; window.Height = 640; Capture(window, "batch-crop-minimum.png", 940, 640);
Check(new Control[] { ok, mode, window.FindControl<ComboBox>("CropRatio")!, window.FindControl<NumericUpDown>("CropWidthInput")!, window.FindControl<TextBox>("OutputInput")! }.All(c => c.Bounds.Width > 20 && c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), window) is { } p && p.X <= 924.5 && p.Y <= 624.5), "Minimum size hides/overflows controls.");
list.SelectedIndex = 3;
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "移除选中")));
Check(window.Entries.Count == 3 && window.Entries.All(e => e.Path != corrupt), "Remove selected did not remove invalid row.");
Click(window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "全选")));
Check(window.CreateRequest().Inputs.Count == 3 && ok.IsEnabled, "Select all does not include all valid files.");
window.Close();
// Confirm and cancel through the real modal actions, preserving the returned request.
var owner = new Window(); owner.Show();
var confirm = new BatchCropWindow(engine, root, [small, large]);
var dialog = confirm.ShowDialog<BatchCropRequest?>(owner); Pump(confirm.Ready); confirm.SetArea(area);
Click(confirm.FindControl<Button>("OkButton")!); Pump(dialog);
Check(dialog.Result is { Inputs.Count: 2, Area.X: 160 } && !confirm.IsVisible, "Confirm did not return shared batch request.");
var cancel = new BatchCropWindow(engine, root, [small]); var cancelDialog = cancel.ShowDialog<BatchCropRequest?>(owner);
Click(cancel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "取消"))); Pump(cancelDialog);
Check(cancelDialog.Result is null && !cancel.IsVisible, "Cancel submitted a batch."); owner.Close();

// A second video stream must use the same geometry in preview, validation and encoding.
var multi = Path.Combine(root, "多视频轨.mkv");
Check(Run("-v", "error", "-n", "-i", small, "-i", portrait, "-map", "0:v", "-map", "1:v", "-map", "0:a", "-c", "copy", multi).ExitCode == 0, "Multistream fixture failed.");
var streamWindow = new BatchCropWindow(engine, root, [multi]); streamWindow.Show(); Pump(streamWindow.Ready);
Click(streamWindow.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "输出配置…")));
var optionsWindow = streamWindow.OwnedWindows.OfType<OptionsWindow>().Single();
optionsWindow.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "VideoStreamIndex").Text = "1";
Click(optionsWindow.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "确定"))); Pump(streamWindow.Ready);
Check(streamWindow.Entries[0].Info is { Width: 180, Height: 320, VideoStreamIndex: 1 } && streamWindow.Area == new CropArea(0, 0, 180, 320), "Changing video stream uses stale media geometry.");
streamWindow.SetArea(new(90, 20, 90, 100));
var streamJob = BatchCrop.CreateJobs(streamWindow.CreateRequest()).Single();
Pump(engine.Execute(streamJob, _ => { }, CancellationToken.None)); outputs.Add(streamJob.Output);
var streamProbe = engine.Probe(streamJob.Output); Pump(streamProbe); var streamInfo = streamProbe.Result;
Check(streamInfo.Width == 90 && streamInfo.Height == 100 && streamJob.Options.VideoStreamIndex == 1, "Crop encoded the wrong video stream.");
streamWindow.Close();

// Exercise the actual main-window entry, queue submission, persistence and Start action.
var state = new Storage(Path.Combine(root, "isolated-state"));
state.SaveSettings(new() { OutputFolder = Path.Combine(root, "main-queue"), ReduceMotion = true, NotifyComplete = false });
var main = new MainWindow(state); main.Show();
var cropMenu = main.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "视频批量裁剪…"));
cropMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
var batch = main.OwnedWindows.OfType<BatchCropWindow>().Single(); batch.AddFiles([small, large]); Pump(batch.Ready); batch.SetArea(area);
Click(batch.FindControl<Button>("OkButton")!);
var queue = main.FindControl<ListBox>("JobList")!.Items.Cast<Job>().ToArray();
Check(queue.Length == 2 && queue.All(j => j.FeatureId == "crop" && j.Options.CropX == 160 && j.State == JobState.Waiting), "Main window did not queue shared crop jobs.");
Check(state.LoadJobs().Count == 2, "Shared crop queue was not saved.");
Click(main.FindControl<Button>("StartButton")!);
PumpUntil(() => queue.All(j => j.State is JobState.Completed or JobState.Failed));
Check(queue.All(j => j.State == JobState.Completed), "Main Start failed to execute crop queue.");
foreach (var job in queue) { outputs.Add(job.Output); var probe = engine.Probe(job.Output); Pump(probe); var result = probe.Result; Check(result.Width == 160 && result.Height == 100, "Main queue output dimensions incorrect."); }
main.Close();
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks, outputs, sourceHashesVerified = true, cropOffsetsVerified = true, uiPointerAndDialogsVerified = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} batch crop checks / {outputs.Count} actual outputs. {root}");

void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
void Pump(Task task)
{
    PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult();
}
void PumpUntil(Func<bool> done)
{
    var deadline = DateTime.UtcNow.AddSeconds(25);
    while (!done()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > deadline) throw new TimeoutException("UI action timed out."); Thread.Sleep(5); }
    Dispatcher.UIThread.RunJobs();
}
void Capture(Window target, string name, int width = 1200, int height = 800)
{
    target.Measure(new Size(width, height)); target.Arrange(new Rect(0, 0, width, height)); Dispatcher.UIThread.RunJobs();
    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96)); bitmap.Render(target); bitmap.Save(Path.Combine(root, name));
}
