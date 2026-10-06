using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

var root = Path.GetFullPath("artifacts/settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
var checks = 0; var outputs = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(name); }
var settings = new AppSettings { OutputFolder = root, AutoDetectGpu = false, ReduceMotion = true };
var engine = new MediaEngine(settings);
ProcessResult Run(params string[] args) => Task.Run(() => ProcessRunner.Run(engine.FFmpeg, args)).GetAwaiter().GetResult();
var image = Path.Combine(root, "测试 image O'Brien.png"); var video = Path.Combine(root, "测试视频.mp4");
Check(Run("-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=512x384:rate=25", "-frames:v", "1", image).ExitCode == 0, "Image fixture failed.");
Check(Run("-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25", "-t", "1", "-c:v", "mpeg4", video).ExitCode == 0, "Video fixture failed.");
var hash = SHA256.HashData(File.ReadAllBytes(image));
Job EncodeImage(string format, string name, int quality, int? explicitQuality = null)
{
    settings.JpegQuality = settings.WebpQuality = quality;
    var job = new Job { FeatureId = "image-" + format, Inputs = [image], Output = Path.Combine(root, name + "." + format), Options = new() { Format = format, ImageQuality = explicitQuality } };
    engine.Execute(job, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(job.Output);
    var info = engine.Probe(job.Output).GetAwaiter().GetResult(); Check(info.Width == 512 && info.Height == 384, "Image size changed.");
    Check(job.Options.Threads == 0 && job.Options.ImageQuality == explicitQuality, "Applying defaults mutated saved job options."); return job;
}
double Error(string path)
{
    byte[] Decode(string source)
    {
        var rgb = Path.Combine(root, Guid.NewGuid() + ".rgb");
        Check(Run("-v", "error", "-n", "-i", source, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", rgb).ExitCode == 0, "Image could not be decoded.");
        return File.ReadAllBytes(rgb);
    }
    var source = Decode(image); var target = Decode(path); Check(source.Length == target.Length, "Decoded dimensions changed.");
    return source.Zip(target, (a, b) => Math.Pow(a - b, 2)).Average();
}
foreach (var format in new[] { "jpg", "webp" })
{
    var low = EncodeImage(format, format + "-low", 10); var high = EncodeImage(format, format + "-high", 95);
    Check(new FileInfo(high.Output).Length > new FileInfo(low.Output).Length * 1.2, "Global quality did not change file size: " + format);
    Check(Error(high.Output) < Error(low.Output), "Higher global quality did not reduce decoded image error: " + format);
    var overridden = EncodeImage(format, format + "-override", 10, 95);
    Check(File.ReadAllBytes(overridden.Output).SequenceEqual(File.ReadAllBytes(high.Output)), "Per-job quality did not override global setting.");
}
Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(image))), "Source image changed.");
Reject(() => SettingsPolicy.Validate(settings.Clone().WithValues(s => s.CpuThreads = 17)), "Invalid thread count accepted.");
Reject(() => SettingsPolicy.Validate(settings.Clone().WithValues(s => s.JpegQuality = 0)), "Invalid JPG quality accepted.");
Reject(() => MediaEngine.ValidateEncodingOptions(new() { ImageQuality = 101 }), "Invalid job quality accepted.");
var draft = SettingsPolicy.Resolve(new(), settings); Check(draft.Threads == 8, "Thread count is not applied.");
settings.MultiThread = false;
Check(SettingsPolicy.Resolve(new() { Threads = 8 }, settings).Threads == 1, "Single-thread switch ignored.");
var encodeArgs = MediaEngine.BuildArguments(new() { FeatureId = "mp4", Inputs = [video], Output = Path.Combine(root, "threads.mp4"), Options = SettingsPolicy.Resolve(new(), settings) }, [engine.Probe(video).GetAwaiter().GetResult()]);
Check(encodeArgs.Contains("-threads") && encodeArgs.LastIndexOf("-threads") > encodeArgs.IndexOf("-i") && encodeArgs[encodeArgs.LastIndexOf("-threads") + 1] == "1" && encodeArgs.Contains("-filter_threads"), "Encoder/filter thread settings missing from command.");
var storage = new Storage(Path.Combine(root, "state")); storage.SaveSettings(settings); var restored = storage.LoadSettings();
Check(restored.CpuThreads == 8 && restored.JpegQuality == 10 && !restored.AutoDetectGpu, "New settings not persisted.");
var legacy = JsonSerializer.Deserialize<AppSettings>("{\"ParallelJobs\":2}")!;
Check(legacy.CpuThreads == 8 && legacy.JpegQuality == 90 && legacy.WebpQuality == 90 && legacy.AutoDetectGpu, "Legacy settings lack defaults.");
var hardware = HardwareAcceleration.TestAsync(engine.FFmpeg).GetAwaiter().GetResult();
Check(hardware.Count == 7 && hardware.All(r => r.Detail.Length > 0), "Hardware report incomplete.");
File.WriteAllText(Path.Combine(root, "hardware.json"), JsonSerializer.Serialize(hardware, new JsonSerializerOptions { WriteIndented = true }));
Check(HardwareAcceleration.SelectCodec("mpg", hardware) is null, "Incompatible hardware/container selected.");
var supportedReport=HardwareAcceleration.Encoders.Reverse().Select(e=>new HardwareEncoderResult(e.Name,e.Codec,true,"Fixture capability")).ToArray();
Check(HardwareAcceleration.Candidates("mp4",supportedReport).SequenceEqual(new[]{"h264_nvenc","h264_amf","h264_qsv","hevc_nvenc","hevc_amf","hevc_qsv"}),"Automatic codec selection depends on report ordering or skips H.265.");
Check(HardwareAcceleration.Candidates("webm",supportedReport).SequenceEqual(new[]{"vp9_qsv"}) && HardwareAcceleration.Candidates("flv",supportedReport).All(c=>c.StartsWith("h264_")),"Automatic codec choice is incompatible with its container.");
if(HardwareAcceleration.SelectCodec("mp4",hardware) is { } supportedCodec)
{
    var actualGpu = new MediaEngine(new() { AutoDetectGpu = true });
    var gpuJob = new Job { FeatureId="mp4", Inputs=[video], Output=Path.Combine(root,"actual-gpu.mp4") };
    actualGpu.Execute(gpuJob,_=>{},CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(gpuJob.Output);
    Check(gpuJob.Log.Contains(supportedCodec) && engine.Probe(gpuJob.Output).GetAwaiter().GetResult().Width==320,"Available GPU was not used for automatic encoding.");
    foreach(var format in new[]{"flv","ts","m4v"})
    {
        var extended=new Job{FeatureId="mp4",Inputs=[video],Options=new(){Format=format},Output=Path.Combine(root,"actual-gpu."+format)};
        actualGpu.Execute(extended,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(extended.Output);
        Check(engine.Probe(extended.Output).GetAwaiter().GetResult() is {VideoCodec:"h264",Width:320} && extended.Log.Contains("使用硬件编码"),"GPU container extension failed: "+format);
    }
}
var badHardware = hardware.FirstOrDefault(r => !r.Supported && r.Codec is "h264_nvenc" or "h264_amf" or "h264_qsv");
if (badHardware is not null)
{
    var calls = 0;
    var fallbackEngine = new MediaEngine(new() { AutoDetectGpu = true }, (_, _) => { calls++; return Task.FromResult<IReadOnlyList<HardwareEncoderResult>>([badHardware with { Supported = true }]); });
    var job = new Job { FeatureId = "mp4", Inputs = [video], Output = Path.Combine(root, "fallback.mp4") };
    fallbackEngine.Execute(job, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(job.Output);
    File.WriteAllText(Path.Combine(root,"fallback.log"),$"probe calls: {calls}\n"+job.Log);
    Check(calls == 1 && job.Log.Contains("回退软件编码") && job.Options.VideoCodec == "自动", "Automatic GPU failure did not fall back safely.");
    Check(engine.Probe(job.Output).GetAwaiter().GetResult().Width == 320 && !Directory.EnumerateFiles(root, ".AvaMedia-gpu-*").Any(), "GPU fallback left invalid output or temporary files.");
    var copy = new Job { FeatureId = "mp4", Inputs = [video], Output = Path.Combine(root, "copy.mp4"), Options = new() { CopyStreams = true } };
    fallbackEngine.Execute(copy, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); outputs.Add(copy.Output);
    Check(calls == 1 && File.Exists(copy.Output), "Stream copy incorrectly probed or re-encoded on GPU.");
    var remaining=hardware.Where(r=>!r.Supported && HardwareAcceleration.CompatibleCodecs("mp4").Contains(r.Codec)).Select(r=>r with{Supported=true}).ToArray();
    var exhaustedEngine=new MediaEngine(new(),(_,_)=>Task.FromResult<IReadOnlyList<HardwareEncoderResult>>(remaining));
    var exhausted=new Job{FeatureId="mp4",Inputs=[video],Output=Path.Combine(root,"all-hardware-fallback.mp4")};
    exhaustedEngine.Execute(exhausted,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(exhausted.Output);
    Check(remaining.All(r=>exhausted.Log.Contains("自动硬件编码 "+r.Codec+" 失败")) && exhausted.Log.Contains("回退软件编码") && engine.Probe(exhausted.Output).GetAwaiter().GetResult().Width==320,"Multiple hardware failures did not retain details and fall back.");
    if(hardware.FirstOrDefault(r=>r.Supported && r.Codec.StartsWith("hevc_")) is {} hevc)
    {
        var nextEngine=new MediaEngine(new(),(_,_)=>Task.FromResult<IReadOnlyList<HardwareEncoderResult>>([hevc,badHardware with{Supported=true}]));
        var next=new Job{FeatureId="mp4",Inputs=[video],Output=Path.Combine(root,"hardware-next-hevc.mp4")};
        nextEngine.Execute(next,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(next.Output);
        var encoded=engine.Probe(next.Output).GetAwaiter().GetResult();
        using var json=JsonDocument.Parse(encoded.RawJson);
        Check(encoded.VideoCodec=="hevc" && next.Log.Contains("自动硬件编码 "+badHardware.Codec+" 失败") && next.Log.Contains("使用硬件编码 "+hevc.Codec) && !next.Log.Contains("回退软件编码"),"A working second hardware encoder was skipped.");
        Check(json.RootElement.GetProperty("streams")[0].GetProperty("codec_tag_string").GetString()=="hvc1","MP4 H.265 output lacks the hvc1 compatibility tag.");
    }
}
var skippedCalls=0;var disabledEngine=new MediaEngine(new(){AutoDetectGpu=false},(_,_)=>{skippedCalls++;return Task.FromResult<IReadOnlyList<HardwareEncoderResult>>(hardware);});
var software=new Job{FeatureId="mp4",Inputs=[video],Output=Path.Combine(root,"gpu-disabled.mp4")};
disabledEngine.Execute(software,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(software.Output);
Check(skippedCalls==0 && engine.Probe(software.Output).GetAwaiter().GetResult().VideoCodec=="mpeg4","Disabling GPU auto-detection still uses GPU.");

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true); Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
var presets = new Storage(Path.Combine(root, "image-presets"));
foreach(var format in new[]{"mp4","mkv","webm","flv","ts"})
{
    foreach(var codec in HardwareAcceleration.CompatibleCodecs(format))
    {
        var hardwareOptions=new OptionsWindow(new(){Format=format,VideoCodec=codec},presetStorage:presets);hardwareOptions.Show();Dispatcher.UIThread.RunJobs();
        Check(FindOption<ComboBox>(hardwareOptions,"VideoCodecCombo").SelectedItem as string==codec && hardwareOptions.ReadOptions().VideoCodec==codec,"Hardware encoder cannot be selected in output options: "+format+"/"+codec);hardwareOptions.Close();
    }
}
foreach(var format in new[]{"jpg","webp"})
{
    presets.SavePreset("Image|"+format+"|saved",new(){Format=format,ImageQuality=64});
    var imageOptions=new OptionsWindow(new(){Format=format},kind:MediaOptionsKind.Image,presetStorage:presets,imageQualityDefault:78);
    imageOptions.Show();Dispatcher.UIThread.RunJobs();
    Check(FindOption<TextBox>(imageOptions,"ImageQualityInput").Text=="78" && imageOptions.ReadOptions().ImageQuality==78,"Image output options ignore global quality.");
    FindOption<TextBox>(imageOptions,"ImageQualityInput").Text="87";
    Check(imageOptions.ReadOptions().ImageQuality==87,"Image output quality editing failed.");
    FindOption<TextBox>(imageOptions,"ImageQualityInput").Text="0";Reject(()=>imageOptions.ReadOptions(),"Out-of-range image quality accepted in output options.");
    FindOption<ComboBox>(imageOptions,"PresetCombo").SelectedItem="saved";Dispatcher.UIThread.RunJobs();
    Check(imageOptions.ReadOptions().ImageQuality==64,"Image quality preset failed to restore.");imageOptions.Close();
    var oldOptions=new OptionsWindow(new(){Format=format,Quality=32},kind:MediaOptionsKind.Image,presetStorage:presets,imageQualityDefault:78);
    oldOptions.Show();Dispatcher.UIThread.RunJobs();
    Check(oldOptions.ReadOptions() is {ImageQuality:null,Quality:32},"Opening output options changed legacy quality.");
    FindOption<TextBox>(oldOptions,"ImageQualityInput").Text="70";
    Check(oldOptions.ReadOptions().ImageQuality==70,"Legacy quality cannot be edited as a percentage.");oldOptions.Close();
}
var saved = new AppSettings { OutputFolder = root, ReduceMotion = true };
var window = new SettingsWindow(saved); window.Show(); Dispatcher.UIThread.RunJobs();
Check(window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex == 0 && window.FindControl<Button>("ApplyButton")!.IsEnabled == false, "Options tab and clean Apply state are not the initial view.");
window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
Capture(window, "settings-advanced-light.png", 926, 800);
window.FindControl<CheckBox>("AutoGpuInput")!.IsChecked=false;window.FindControl<CheckBox>("AutoGpuInput")!.IsChecked=true;
Check(!window.FindControl<Button>("ApplyButton")!.IsEnabled,"Reverting a checkbox leaves a false unsaved-change state.");
var threadInput=window.FindControl<NumericUpDown>("ThreadsInput")!;threadInput.Value=12;threadInput.Value=8;
Check(!window.FindControl<Button>("ApplyButton")!.IsEnabled,"Reverting a number leaves a false unsaved-change state.");
foreach(var text in new[]{"bad","1.5","17",""})
{
    threadInput.Text=text;Click(window.FindControl<Button>("ApplyButton")!);
    Check(window.FindControl<TextBlock>("StatusText")!.IsVisible && saved.CpuThreads==8 && window.IsVisible,"Invalid numeric text silently commits a stale value: "+text);
}
threadInput.Text="8";
Check(!window.FindControl<Button>("ApplyButton")!.IsEnabled,"Restoring valid numeric text remains dirty.");
window.FindControl<NumericUpDown>("ThreadsInput")!.Value = 16;
Check(window.FindControl<Button>("ApplyButton")!.IsEnabled && saved.CpuThreads == 8, "Editing mutates applied settings.");
Click(window.FindControl<Button>("ApplyButton")!);
Check(saved.CpuThreads == 16 && window.IsVisible && !window.FindControl<Button>("ApplyButton")!.IsEnabled, "Apply did not commit without closing.");
window.FindControl<NumericUpDown>("ThreadsInput")!.Value = 4;
Click(window.FindControl<Button>("CancelButton")!);
Check(saved.CpuThreads == 16 && !window.IsVisible, "Cancel changed the previously applied value.");
window = new SettingsWindow(saved); window.Show(); Click(window.FindControl<Button>("DefaultButton")!);
Check(window.ReadSettings().CpuThreads == 8 && window.ReadSettings().JpegQuality == 90 && saved.CpuThreads == 16, "Reset does not remain a draft.");
window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 0;
window.FindControl<TextBox>("OutputInput")!.Text = ""; Click(window.FindControl<Button>("OkButton")!);
Check(window.IsVisible && window.FindControl<TextBlock>("StatusText")!.IsVisible && saved.CpuThreads == 16, "Invalid settings partially committed.");
window.FindControl<TextBox>("OutputInput")!.Text = root;
Capture(window, "settings-options.png", 926, 800);
window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 3;
Capture(window, "settings-internal.png", 926, 800);
window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1;
Click(window.FindControl<Button>("HardwareTestButton")!);
var test = window.OwnedWindows.OfType<HardwareTestWindow>().Single(); Pump(test.Ready);
Check(test.Report.Count == 7 && test.Report.All(r => r.Summary.Contains("supported")), "HA Test dialog did not execute the backend.");
Capture(test, "hardware-test.png", 580, 392); test.Close(); Dispatcher.UIThread.RunJobs();
Check(window.FindControl<Button>("HardwareTestButton")!.IsEnabled, "HA Test button remains disabled after close.");
Application.Current.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs();
Capture(window, "settings-advanced-dark.png", 926, 800);
window.Width = 780; window.Height = 640; Capture(window, "settings-minimum.png", 780, 640);
Check(new[] { "DefaultButton", "CancelButton", "ApplyButton", "OkButton" }.All(n => window.FindControl<Button>(n)!.TranslatePoint(new Point(165, 34), window) is { } p && p.X <= 764.5 && p.Y <= 616.5), "Footer buttons overflow at minimum size.");
window.Close();
var cancelledTest = new HardwareTestWindow(""); cancelledTest.Show(); cancelledTest.Close(); Pump(cancelledTest.Ready);
Check(!cancelledTest.IsVisible, "Closing HA Test did not cancel cleanly.");
// Verify the main-window Apply event persists immediately and synchronizes the toolbar.
var mainState = new Storage(Path.Combine(root, "main-state")); mainState.SaveSettings(new() { OutputFolder = root, ReduceMotion = true, NotifyComplete = false });
mainState.SaveJobs([]);var queueFile=Path.Combine(root,"main-state","queue.json");var queueModified=File.GetLastWriteTimeUtc(queueFile);
var main = new MainWindow(mainState); main.Show();
main.GetLogicalDescendants().OfType<MenuItem>().Single(m => Equals(m.Header, "选项")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
var settingsDialog = main.OwnedWindows.OfType<SettingsWindow>().Single();
settingsDialog.FindControl<CheckBox>("MultithreadInput")!.IsChecked = false;
settingsDialog.FindControl<NumericUpDown>("JpegQualityInput")!.Value = 72;
Click(settingsDialog.FindControl<Button>("ApplyButton")!);
Check(!mainState.LoadSettings().MultiThread && mainState.LoadSettings().JpegQuality == 72 && main.FindControl<CheckBox>("Multithread")!.IsChecked == false, "Main Apply loses settings or waits for close to save.");
Check(File.GetLastWriteTimeUtc(queueFile)==queueModified,"Applying settings unnecessarily rewrites the task queue.");
settingsDialog.FindControl<NumericUpDown>("JpegQualityInput")!.Value = 50; Click(settingsDialog.FindControl<Button>("CancelButton")!);
Check(mainState.LoadSettings().JpegQuality == 72, "Cancelling a later draft reverted committed settings."); main.Close();
var failedSettings=new AppSettings{OutputFolder=root,ReduceMotion=true};var failedWindow=new SettingsWindow(failedSettings);
failedWindow.Applied+=(_,_)=>throw new IOException("Settings save failed.");failedWindow.Show();failedWindow.FindControl<NumericUpDown>("ThreadsInput")!.Value=12;
Click(failedWindow.FindControl<Button>("ApplyButton")!);
Check(failedSettings.CpuThreads==8 && failedWindow.FindControl<TextBlock>("StatusText")!.IsVisible && failedWindow.FindControl<Button>("ApplyButton")!.IsEnabled,"A failed save commits settings or loses the draft.");failedWindow.Close();
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks, outputs, hardwareSupported = hardware.Count(r => r.Supported), resourceSharingIntegrated = false, visualScope = "General options, advanced tab and HA Test dialog." }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks} settings checks / {outputs.Count} actual outputs. {root}");

void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
T FindOption<T>(Window target,string name) where T:Control => target.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
void Pump(Task task)
{
    var end = DateTime.UtcNow.AddSeconds(65);
    while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > end) throw new TimeoutException(); Thread.Sleep(5); }
    Dispatcher.UIThread.RunJobs(); task.GetAwaiter().GetResult();
}
void Capture(Window target, string name, int width, int height)
{
    target.Measure(new Size(width, height)); target.Arrange(new Rect(0, 0, width, height)); Dispatcher.UIThread.RunJobs();
    using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96)); bitmap.Render(target); bitmap.Save(Path.Combine(root, name));
}
static class TestSettingsExtensions
{
    public static AppSettings WithValues(this AppSettings value, Action<AppSettings> edit) { edit(value); return value; }
}
