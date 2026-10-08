using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

var fixtures = Path.GetFullPath("artifacts/speech-acceptance/fixtures");
if (args.Contains("--native-ui"))
{
    SpeechAcceptanceApp.Fixture = Path.Combine(fixtures, "chinese.mp4");
    AppBuilder.Configure<SpeechAcceptanceApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    return;
}
var root = Path.GetFullPath("artifacts/speech-acceptance/run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
var engine = new MediaEngine(new() { AutoDetectGpu = false });
var checks = new List<string>(); var metrics = new Dictionary<string, double>();
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS " + label); }
void FF(params string[] values)
{
    var result = ProcessRunner.Run(engine.FFmpeg, values).GetAwaiter().GetResult();
    if (result.ExitCode != 0) throw new Exception(result.Error);
}
Job Run(string feature, string input, string name, ConversionOptions options)
{
    var job = new Job { FeatureId = feature, Inputs = [input], Output = Path.Combine(root, name + "." + options.Format), Options = options };
    engine.Execute(job, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
    Check(File.Exists(job.Output) && new FileInfo(job.Output).Length > 0, name + " produced an output"); return job;
}
var chinese = Path.Combine(fixtures, "chinese.mp4"); var english = Path.Combine(fixtures, "english.mp4");
var state = new Storage(Path.Combine(root, "state"));
if (args.Contains("--audio-ass-only"))
{
    var audio = Run("auto-subtitle", Path.Combine(fixtures,"english.aiff"), "audio-styled", new() { Format="ass", Transcription=new() { Language="en",Model=SpeechModel.Tiny } });
    var text = File.ReadAllText(audio.Output);
    Check(text.Contains("PlayResX: 1920") && text.Contains("PlayResY: 1080") && text.Contains("test",StringComparison.OrdinalIgnoreCase), "audio-only ASS uses a usable canvas and recognizes speech");
    File.WriteAllText(Path.Combine(root,"audio-ass-acceptance.json"),JsonSerializer.Serialize(checks));Console.WriteLine("RESULT "+root);return;
}
if (args.Contains("--models-only"))
{
    var modelStore = new ModelStore();
    foreach (var model in new[] { SpeechModel.Base, SpeechModel.Tiny })
    {
        var job = Run("auto-subtitle", english, "model-" + model, new() { Format="srt", Transcription=new() { Language="en",Model=model } });
        Check(modelStore.IsInstalledAsync(SpeechModelInstaller.Id(model),true).GetAwaiter().GetResult(), model + " is installed and verified by shared model management");
        using var lease = modelStore.AcquireAsync(SpeechModelInstaller.Id(model)).GetAwaiter().GetResult();
        Check(File.Exists(new SpeechModelInstaller().EnsureInstalledAsync(model).GetAwaiter().GetResult()), model + " can prepare a queued task while the installed model is in use");
        bool protectedModel = false;
        try { modelStore.DeleteAsync(SpeechModelInstaller.Id(model)).GetAwaiter().GetResult(); } catch(InvalidOperationException) { protectedModel = true; }
        Check(protectedModel && File.ReadAllText(job.Output).Contains("test",StringComparison.OrdinalIgnoreCase), model + " recognizes speech and is protected from deletion while in use");
    }
    File.WriteAllText(Path.Combine(root,"model-acceptance.json"),JsonSerializer.Serialize(checks));Console.WriteLine("RESULT "+root);return;
}
if (!args.Contains("--ui-only"))
{
foreach (var (input, language, name) in new[] { (chinese, "zh", "chinese-base"), (english, "en", "english-base") })
{
    var job = Run("auto-subtitle", input, name, new() { Format = "srt", Transcription = new() { Language = language } });
    var text = File.ReadAllText(job.Output); File.WriteAllText(Path.Combine(root, name + "-log.txt"), job.Log);
    Check(text.Contains(" --> ") && (language == "zh" ? text.Contains("字幕") : text.Contains("subtitle", StringComparison.OrdinalIgnoreCase)), name + " recognized actual speech with timestamps");
}
var tiny = Run("auto-subtitle", english, "english-tiny", new() { Format = "srt", Transcription = new() { Language = "en", Model = SpeechModel.Tiny } });
Check(File.ReadAllText(tiny.Output).Contains("test", StringComparison.OrdinalIgnoreCase), "tiny multilingual model executes locally");
var ass = Run("auto-subtitle", chinese, "styled", new() { Format = "ass", Transcription = new() { Language = "zh" },
    SubtitleFont = "Arial", SubtitleFontSize = 36, SubtitleColor = "#FF0000", SubtitleAlignment = 8, SubtitleMargin = 12 });
Check(File.ReadAllText(ass.Output).Contains("Style: Default,Arial,36,&H000000FF") && File.ReadAllText(ass.Output).Contains(",8,12,12,12,1"), "ASS retains selected font, size, color, position and margin");
var styled = Run("auto-subtitle", chinese, "burned", new() { Format = "mp4", Transcription = new() { Language = "zh" },
    SubtitleFont = "Arial", SubtitleFontSize = 36, SubtitleColor = "#FF0000", SubtitleAlignment = 8, SubtitleMargin = 12 });
File.WriteAllText(Path.Combine(root, "burned-log.txt"), styled.Log);
var raw = Path.Combine(root, "styled.rgb");
FF("-v", "error", "-y", "-ss", "2", "-i", styled.Output, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw);
var pixels = File.ReadAllBytes(raw);
var red = Enumerable.Range(0, pixels.Length / 3).Where(index => pixels[index * 3] > 60 && pixels[index * 3] > pixels[index * 3 + 1] * 2 && pixels[index * 3] > pixels[index * 3 + 2] * 2).ToArray();
Check(red.Length > 100 && red.Average(index => index / 640d) < 100, "actual video renders red subtitles in the upper position");
var smaller = Run("mp4", chinese, "smaller-style", new() { Format = "mp4", SubtitleMode = SubtitleMode.BurnIn, Subtitle = ass.Output,
    SubtitleFont = "Times New Roman", SubtitleFontSize = 18, SubtitleColor = "#FF0000", SubtitleAlignment = 2, SubtitleMargin = 12 });
FF("-v", "error", "-y", "-ss", "2", "-i", smaller.Output, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", Path.Combine(root, "smaller.rgb"));
var smallPixels = File.ReadAllBytes(Path.Combine(root, "smaller.rgb"));
var smallRed = Enumerable.Range(0, smallPixels.Length / 3).Where(index => smallPixels[index * 3] > 60 && smallPixels[index * 3] > smallPixels[index * 3 + 1] * 2 && smallPixels[index * 3] > smallPixels[index * 3 + 2] * 2).ToArray();
Check(smallRed.Length > 20 && smallRed.Length < red.Length && smallRed.Average(index => index / 640d) > 260, "changing font, size and position changes the rendered subtitle");
var noisy = Path.Combine(fixtures, "noisy.mkv");
var enhanced = Run("voice-enhance", noisy, "enhanced", new() { Format = "mkv", VideoCodec = "copy", VoiceEnhancement = true, VoiceEnhancementStrength = 90 });
var audioOnly = Run("audio-enhance", Path.Combine(fixtures, "noisy.wav"), "enhanced-audio", new() { Format = "wav", VoiceEnhancement = true, VoiceEnhancementStrength = 90 });
string[] VideoHashes(string path)
{
    var output = ProcessRunner.Run(engine.FFprobe, ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_data_hash", "sha256", "-of", "json", path]).GetAwaiter().GetResult();
    using var doc = JsonDocument.Parse(output.Output);
    return doc.RootElement.GetProperty("packets").EnumerateArray().Select(packet => packet.GetProperty("data_hash").GetString()!).ToArray();
}
Check(VideoHashes(noisy).SequenceEqual(VideoHashes(enhanced.Output)), "voice enhancement copies the original video packets without re-encoding");
double[] Samples(string path)
{
    var pcm = Path.Combine(root, Guid.NewGuid() + ".f32");
    FF("-v", "error", "-y", "-i", path, "-vn", "-ar", "48000", "-ac", "1", "-f", "f32le", pcm);
    var data = File.ReadAllBytes(pcm); File.Delete(pcm);
    return Enumerable.Range(0, data.Length / 4).Select(index => (double)BitConverter.ToSingle(data, index * 4)).ToArray();
}
double Rms(double[] samples, double from, double to) => Math.Sqrt(samples.Skip((int)(from * 48000)).Take((int)((to - from) * 48000)).Average(value => value * value));
var before = Samples(noisy); var after = Samples(audioOnly.Output);
var beforeRatio = Rms(before, .2, 1.2) / Rms(before, 2, 7); var afterRatio = Rms(after, .2, 1.2) / Rms(after, 2, 7);
metrics["noiseReductionDb"] = 20 * Math.Log10(beforeRatio / afterRatio);
Check(metrics["noiseReductionDb"] > 6, "speech-relative background noise is reduced by more than 6 dB");
Check(Math.Abs(engine.Probe(enhanced.Output).GetAwaiter().GetResult().Duration - engine.Probe(noisy).GetAwaiter().GetResult().Duration) < .1, "enhancement preserves video duration and audio synchronization");
var sourceHash = SHA256.HashData(File.ReadAllBytes(chinese));
using (var cancellation = new CancellationTokenSource())
{
    var cancelled = new Job { FeatureId = "auto-subtitle", Inputs = [chinese], Output = Path.Combine(root, "cancelled.srt"), Options = new() { Format = "srt", Transcription = new() { Language = "zh" } } };
    bool stopped = false;
    try { engine.Execute(cancelled, percent => { if (percent >= 15) cancellation.Cancel(); }, cancellation.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped && !File.Exists(cancelled.Output), "cancelled recognition leaves no completed output");
}
Check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(chinese))), "subtitle generation leaves the original media unchanged");
state.SaveJobs([styled, enhanced]);
var restored = state.LoadJobs();
Check(restored[0].Options.Transcription?.Language == "zh" && restored[0].Options.SubtitleAlignment == 8 && restored[1].Options.VoiceEnhancementStrength == 90, "queue persistence retains speech and subtitle style settings");
File.WriteAllText(Path.Combine(root, "media-acceptance.json"), JsonSerializer.Serialize(new { checks, metrics, outputs = root }, new JsonSerializerOptions { WriteIndented = true }));
}
if (args.Contains("--media-only")) { Console.WriteLine("RESULT " + root); return; }
var uiStyle = new ConversionOptions { Format = "mp4", Transcription = new() { Language = "zh" }, SubtitleFont = "Arial", SubtitleFontSize = 36, SubtitleColor = "#FF0000", SubtitleAlignment = 8, SubtitleMargin = 12 };
var uiAss = Path.Combine(root, "ui-style.ass"); File.WriteAllText(uiAss, SpeechSubtitles.Ass([new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "字幕预览")], uiStyle, 640, 360));

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true); Localization.Apply("zh-CN");
void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
void Wait(Task task) { while (!task.IsCompleted) { Pump(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); Pump(); }
T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
void Capture(Window window, string name)
{
    Pump(); using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height), new Vector(96, 96));
    bitmap.Render(window); bitmap.Save(Path.Combine(root, name + ".png"));
}
var subtitleWindow = new SpeechToolsWindow(engine, Catalog.Find("auto-subtitle"), root, [chinese]);
subtitleWindow.Show(); Wait(subtitleWindow.PreviewReady);
Find<ComboBox>(subtitleWindow, "SubtitleFont").SelectedItem = "Arial";
Find<NumericUpDown>(subtitleWindow, "SubtitleFontSize").Value = 36;
Find<TextBox>(subtitleWindow, "SubtitleColor").Text = "#FF0000";
Find<ToggleButton>(subtitleWindow, "SubtitlePosition8").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
var uiOptions = subtitleWindow.ReadRequest().Options;
Check(uiOptions.SubtitleFont == "Arial" && uiOptions.SubtitleFontSize == 36 && uiOptions.SubtitleColor == "#FF0000" && uiOptions.SubtitleAlignment == 8, "intuitive controls read font, size, palette color and nine-grid position");
subtitleWindow.AddFiles([chinese]); Check(subtitleWindow.ReadRequest().Files.Length == 1, "duplicate file imports stay deduplicated");
Capture(subtitleWindow, "subtitles-light");
Find<ComboBox>(subtitleWindow, "SpeechOutput").SelectedIndex = 2; Pump();
Check(!subtitleWindow.GetVisualDescendants().OfType<SubtitleStyleEditor>().Single().IsVisible, "SRT output hides unsupported style controls");
Find<ComboBox>(subtitleWindow, "SpeechOutput").SelectedIndex = 3; Pump();
Check(subtitleWindow.ReadRequest().Options.Format == "ass" && subtitleWindow.GetVisualDescendants().OfType<SubtitleStyleEditor>().Single().IsVisible, "ASS output exposes and retains style controls");
Find<ComboBox>(subtitleWindow, "SpeechOutput").SelectedIndex = 0;
Skin.Apply("Dark"); Pump(); Capture(subtitleWindow, "subtitles-dark");
Skin.Apply("MacOS9"); Pump(); Capture(subtitleWindow, "subtitles-macos9"); subtitleWindow.Close();
Skin.Apply("Light");
var edit = new SpeechToolsWindow(engine, Catalog.Find("auto-subtitle"), root, [chinese], uiStyle, editing: true);
edit.Show(); Pump(); Check(edit.ReadRequest().Options.SubtitleFontSize == 36 && edit.ReadRequest().Options.SubtitleAlignment == 8, "task editing refills subtitle settings"); edit.Close();
var voiceWindow = new SpeechToolsWindow(engine, Catalog.Find("voice-enhance"), root, [Path.Combine(fixtures,"noisy.mkv")], new() { Format="mkv",VoiceEnhancement=true,VoiceEnhancementStrength=90 }, editing: true);
voiceWindow.Show(); Pump();
Check(voiceWindow.ReadRequest().Options.VoiceEnhancementStrength == 90 && voiceWindow.ReadRequest().Options.VideoCodec == "copy", "voice task editing refills intensity and preserves video copy");
Capture(voiceWindow, "voice-light"); voiceWindow.Close();
var styles = new OptionsWindow(uiStyle, presetStorage: state); styles.Show(); Pump();
var tabs = styles.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 2; Pump();
Find<ComboBox>(styles, "SubtitleModeCombo").SelectedIndex = 1; Find<TextBox>(styles, "SubtitlePath").Text = uiAss; Pump();
Check(styles.GetVisualDescendants().OfType<SubtitleStyleEditor>().Single().IsVisible, "existing conversion settings share the same subtitle style editor"); styles.Close();
File.WriteAllText(Path.Combine(root, "acceptance.json"), JsonSerializer.Serialize(new { checks, metrics, outputs = root, models = new SpeechModelInstaller().DirectoryPath }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("RESULT " + root);

public sealed class SpeechAcceptanceApp : Application
{
    public static string Fixture = "";
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        foreach (var file in new[] { "Localization", "UiStyles", "Motion", "ControlRoles" })
            Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop")) { Source = new Uri("avares://AvaMedia.Desktop/Styles/" + file + ".axaml") });
        Localization.Apply("zh-CN");
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SpeechToolsWindow(new MediaEngine(new() { AutoDetectGpu = false }), Catalog.Find("auto-subtitle"), Path.GetDirectoryName(Fixture)!, [Fixture]);
        base.OnFrameworkInitializationCompleted();
    }
}
