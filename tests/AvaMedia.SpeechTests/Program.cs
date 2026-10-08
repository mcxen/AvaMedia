using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;
using SkiaSharp;

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
if (!args.Contains("--ui-only") && !args.Contains("--position-only"))
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
    Pump(); using var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("Window did not render.");
    bitmap.Save(Path.Combine(root, name + ".png"));
}
if (args.Contains("--position-only"))
{
    var video = Path.Combine(root,"preview.mp4");
    FF("-v","error","-f","lavfi","-i","color=c=blue:s=640x360:r=10:d=1","-f","lavfi","-i","color=c=red:s=640x360:r=10:d=1",
        "-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0","-c:v","libx264","-pix_fmt","yuv420p",video);
    var window = new SpeechToolsWindow(engine,Catalog.Find("auto-subtitle"),root,[video]); window.Show(); Wait(window.PreviewReady);
    SKColor FrameColor()
    {
        using var stream = new MemoryStream(); ((Bitmap)Find<Image>(window,"SubtitlePreviewFrame").Source!).Save(stream);
        using var bitmap = SKBitmap.Decode(stream.ToArray()); return bitmap.GetPixel(10,10);
    }
    Check(FrameColor().Blue>200 && FrameColor().Red<30,"preview displays the actual video beginning");
    Find<Slider>(window,"SubtitlePreviewSeek").Value=1.5; Wait(window.PreviewReady);
    Check(FrameColor().Red>200 && Find<TextBox>(window,"SubtitlePreviewTime").Text=="00:00:01.500","timeline seeking updates both the source frame and time field");
    Find<TextBox>(window,"SubtitlePreviewTime").Text="00:00:00.250";
    Find<TextBox>(window,"SubtitlePreviewTime").RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.Enter }); Wait(window.PreviewReady);
    Check(FrameColor().Blue>200 && window.ReadRequest().Options.Start==0,"typed preview time seeks without changing the output interval");
    var seek=Find<Slider>(window,"SubtitlePreviewSeek"); seek.Value=.5; var obsolete=window.PreviewReady; seek.Value=1.7; Wait(window.PreviewReady); Wait(obsolete);
    Check(FrameColor().Red>200,"a cancelled older seek cannot replace the latest preview");
    Pump();
    var screen=Find<Grid>(window,"SubtitlePreviewScreen");
    Point At(double x,double y)=>screen.TranslatePoint(new Point(screen.Width*x,screen.Height*y),window)!.Value;
    window.MouseDown(At(.3,.4),MouseButton.Left); window.MouseMove(At(.68,.33)); window.MouseUp(At(.68,.33),MouseButton.Left); Pump();
    var positioned=window.ReadRequest().Options;
    Check(positioned.SubtitlePositionX is >.6 and <.75 && Math.Abs(positioned.SubtitlePositionY!.Value-.33)<.01,"dragging on the video stores arbitrary normalized subtitle coordinates");
    var caption=Find<Border>(window,"SubtitlePreviewCaption");var center=caption.TranslatePoint(new Point(caption.Bounds.Width/2,caption.Bounds.Height/2),screen)!.Value;
    if(Math.Abs(center.X/screen.Bounds.Width-positioned.SubtitlePositionX!.Value)>=.01||Math.Abs(center.Y/screen.Bounds.Height-positioned.SubtitlePositionY!.Value)>=.01)
        throw new Exception($"Caption center={center}, bounds={caption.Bounds}, frame={Find<Image>(window,"SubtitlePreviewFrame").Bounds}, expected={positioned.SubtitlePositionX},{positioned.SubtitlePositionY}");
    Check(true,"the visible caption center matches the saved coordinates");
    Capture(window,"subtitle-position-preview");
    using(var screenshot=SKBitmap.Decode(Path.Combine(root,"subtitle-position-preview.png")))
    {
        var at=At(.1,.1);Check(screenshot.GetPixel((int)at.X,(int)at.Y).Red>200,"the video frame is visible behind the draggable subtitle");
        var corner=caption.TranslatePoint(default,window)!.Value;var whites=new List<int>();
        for(int y=(int)corner.Y;y<corner.Y+caption.Bounds.Height;y++)for(int x=(int)corner.X;x<corner.X+caption.Bounds.Width;x++)
        {var pixel=screenshot.GetPixel(x,y);if(pixel.Red>220&&pixel.Green>220&&pixel.Blue>220)whites.Add(x);}
        Check(whites.Count>100&&Math.Abs(whites.Average()-corner.X-caption.Bounds.Width/2)<caption.Bounds.Width*.2,"the complete preview label stays centered and visible after seeking");
    }
    var edited=new SpeechToolsWindow(engine,Catalog.Find("auto-subtitle"),root,[video],positioned,editing:true);edited.Show();Wait(edited.PreviewReady);
    Check(edited.ReadRequest().Options.SubtitlePositionX==positioned.SubtitlePositionX,"task editing restores the dragged subtitle position");edited.Close();
    Find<ToggleButton>(window,"SubtitlePosition2").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
    Check(window.ReadRequest().Options.SubtitlePositionX is null && window.ReadRequest().Options.SubtitleAlignment==2,"nine-grid selection restores a preset position");window.Close();
    var settings=new OptionsWindow(positioned,presetStorage:state,previewEngine:engine,previewSource:video);settings.Show();Pump();
    settings.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=2;Pump();Find<ComboBox>(settings,"SubtitleModeCombo").SelectedIndex=1;Pump();
    var shared=settings.GetVisualDescendants().OfType<SubtitleStyleEditor>().Single();Wait(shared.PreviewReady);
    Check(Find<Image>(settings,"SubtitlePreviewFrame").Source is Bitmap && settings.ReadOptions().SubtitlePositionX==positioned.SubtitlePositionX,"conversion settings share video preview and preserve the visual placement");settings.Close();
    positioned.Format="mp4";positioned.SubtitleMode=SubtitleMode.BurnIn;positioned.SubtitleFont="Arial";positioned.SubtitleFontSize=20;positioned.SubtitleColor="#FF0000";
    var cue=new[]{new SubtitleCue(TimeSpan.Zero,TimeSpan.FromSeconds(4),"Subtitle position")};
    var ass=SpeechSubtitles.Ass(cue,positioned,640,360);
    Check(ass.Contains("{\\an5\\pos("),"generated ASS includes the visual subtitle placement");
    var external=positioned.Clone(); external.SubtitlePositionX=external.SubtitlePositionY=null;
    positioned.Subtitle=Path.Combine(root,"external-position.ass");
    File.WriteAllText(positioned.Subtitle,SpeechSubtitles.Ass(cue,external,640,360).Replace("Subtitle position","{\\an2\\pos(10,270)}Subtitle position"));
    var burned=new Job {FeatureId="mp4",Inputs=[chinese],Output=Path.Combine(root,"custom-position.mp4"),Options=positioned};
    Wait(engine.Execute(burned,_=>{},CancellationToken.None));Check(File.Exists(burned.Output),"custom subtitle placement produces a video");var rgb=Path.Combine(root,"position.rgb");
    FF("-v","error","-ss","1","-i",burned.Output,"-frames:v","1","-pix_fmt","rgb24","-f","rawvideo",rgb);
    var pixels=File.ReadAllBytes(rgb);
    var red=Enumerable.Range(0,pixels.Length/3).Where(i=>pixels[i*3]>60&&pixels[i*3]>pixels[i*3+1]*2&&pixels[i*3]>pixels[i*3+2]*2).ToArray();
    Check(red.Length>100&&Math.Abs(red.Average(i=>i%640)-positioned.SubtitlePositionX!.Value*640)<20&&Math.Abs(red.Average(i=>i/640)-positioned.SubtitlePositionY!.Value*360)<20,
        "burned subtitles match the dragged position and replace an external ASS position");
    state.SaveJobs([burned]);Check(state.LoadJobs()[0].Options.SubtitlePositionX==positioned.SubtitlePositionX,"queue persistence retains arbitrary subtitle coordinates");
    File.WriteAllText(Path.Combine(root,"position-acceptance.json"),JsonSerializer.Serialize(checks));Console.WriteLine("RESULT "+root);return;
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
