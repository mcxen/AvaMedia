using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

var root=Path.GetFullPath("artifacts/audio-options-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var engine=new MediaEngine(new());var checks=new List<string>();var outputs=new List<string>();
void Check(bool value,string message){if(!value)throw new Exception(message);checks.Add(message);Console.WriteLine("PASS "+message);}
ProcessResult FF(params string[] args){var result=ProcessRunner.Run(engine.FFmpeg,args).GetAwaiter().GetResult();if(result.ExitCode!=0)throw new Exception(result.Error);return result;}
Job Convert(string source,ConversionOptions options,string name,string id="mp4")
{
    var job=new Job{FeatureId=id,Inputs=[source],Options=options,Output=Path.Combine(root,name+"."+options.Format)};
    engine.Execute(job,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(job.Output);return job;
}
JsonDocument Probe(string path)=>JsonDocument.Parse(ProcessRunner.Run(engine.FFprobe,["-v","error","-show_streams","-of","json",path]).GetAwaiter().GetResult().Output);
string[] PacketHashes(string path,string stream)
{
    using var doc=JsonDocument.Parse(ProcessRunner.Run(engine.FFprobe,["-v","error","-select_streams",stream,"-show_packets","-show_data_hash","sha256","-of","json",path]).GetAwaiter().GetResult().Output);
    return doc.RootElement.GetProperty("packets").EnumerateArray().Select(p=>p.GetProperty("data_hash").GetString()!).ToArray();
}
short[] Samples(string path,string name,string stream="0:a:0")
{
    var raw=Path.Combine(root,name+".s16");FF("-v","error","-n","-i",path,"-map",stream,"-ac","1","-ar","44100","-c:a","pcm_s16le","-f","s16le",raw);
    var bytes=File.ReadAllBytes(raw);return Enumerable.Range(0,bytes.Length/2).Select(i=>BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i*2,2))).ToArray();
}
double Rms(short[] samples,double start,double end)=>Math.Sqrt(samples.Skip((int)(start*44100)).Take((int)((end-start)*44100)).Select(v=>(double)v*v).Average());
string Wave(string name,double duration,Func<double,double> signal)
{
    var raw=Path.Combine(root,name+"-input.s16");var bytes=new byte[(int)(duration*44100)*2];
    for(var i=0;i<bytes.Length/2;i++)BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i*2,2),(short)Math.Clamp(signal(i/44100d)*32767,-32767,32767));File.WriteAllBytes(raw,bytes);
    var path=Path.Combine(root,name+".wav");FF("-v","error","-n","-f","s16le","-ar","44100","-ac","1","-i",raw,"-c:a","pcm_s16le",path);return path;
}
bool Reject(ConversionOptions options){try{MediaEngine.ValidateEncodingOptions(options);return false;}catch(ArgumentException){return true;}}

var multi=Path.Combine(root,"双音轨 O'Brien space.mp4");
FF("-v","error","-n","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=44100","-f","lavfi","-i","sine=frequency=880:sample_rate=44100","-map","0:v","-map","1:a","-map","2:a","-metadata:s:a:0","language=eng","-metadata:s:a:1","language=zho","-t","2","-c:v","mpeg4","-q:v","3","-c:a","aac",multi);
var sourceHash=SHA256.HashData(File.ReadAllBytes(multi));
var all=Convert(multi,new(){CopyStreams=true,KeepAllAudioStreams=true},"all-copy","clip");
using(var doc=Probe(all.Output))
{
    var audio=doc.RootElement.GetProperty("streams").EnumerateArray().Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
    Check(audio.Length==2,"Fast Copy preserves all requested audio tracks");
    Check(audio.Select(s=>s.GetProperty("tags").GetProperty("language").GetString()).SequenceEqual(new[]{"eng","zho"}),"All-track copy retains language metadata");
}
Check(PacketHashes(multi,"v:0").SequenceEqual(PacketHashes(all.Output,"v:0")),"Full-copy video packets remain identical");
Check(PacketHashes(multi,"a:1").SequenceEqual(PacketHashes(all.Output,"a:1")),"Second audio stream is copied without re-encoding");
var videoCopy=Convert(multi,new(){VideoCodec="copy",AudioCodec="aac",Volume=.5,SampleRate=48000,AudioChannels=2},"video-copy-audio-encode");
var videoCopyInfo=engine.Probe(videoCopy.Output).GetAwaiter().GetResult();
Check(PacketHashes(multi,"v:0").SequenceEqual(PacketHashes(videoCopy.Output,"v:0")),"Video copy allows independent audio processing");
Check(videoCopyInfo.AudioSampleRate==48000 && videoCopyInfo.AudioChannels==2,"Independent audio encoding applies rate and channels");
var audioCopy=Convert(multi,new(){AudioCodec="copy",Width=160,AudioFadeIn=0,AudioFadeOut=0},"audio-copy-video-encode");
Check(engine.Probe(audioCopy.Output).GetAwaiter().GetResult().Width==160,"Audio copy allows independent video resizing");
Check(PacketHashes(multi,"a:0").SequenceEqual(PacketHashes(audioCopy.Output,"a:0")),"Audio-copy packets remain identical while video changes");
var encodedAll=Convert(multi,new(){KeepAllAudioStreams=true,SampleRate=48000,AudioChannels=2,Volume=.5},"all-encoded");
using(var doc=Probe(encodedAll.Output))
{
    var tracks=doc.RootElement.GetProperty("streams").EnumerateArray().Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
    Check(tracks.Length==2 && tracks.All(s=>s.GetProperty("sample_rate").GetString()=="48000"&&s.GetProperty("channels").GetInt32()==2),"Re-encoding retains and configures both audio tracks");
}
Check(Reject(new(){VideoCodec="copy",Width=160}),"Video-copy filters are rejected before execution");
Check(Reject(new(){AudioCodec="copy",NoiseReduction=true}),"Audio-copy effects are rejected before execution");
Check(Reject(new(){CopyStreams=true,SampleRate=48000}),"Full copy rejects sample-rate conversion");
Check(Reject(new(){AudioFadeIn=double.NaN}),"Non-finite audio fades are rejected");
Check(Reject(new(){Format="wav",KeepAllAudioStreams=true}),"Single-track container rejects all-stream preservation");

var shaped=Wave("amplitude-steps",2,t=>(t<1?.1:.6)*Math.Sin(2*Math.PI*440*t));
var original=Samples(shaped,"original");
var reversed=Convert(shaped,new(){Format="wav",ReverseAudio=true},"reversed","audio-wav");var reversedSamples=Samples(reversed.Output,"reversed-result");
Check(reversedSamples.SequenceEqual(Enumerable.Reverse(original)),"Audio reverse reverses every PCM sample exactly");
var trimReverse=Convert(shaped,new(){Format="wav",Start=.25,End=1.25,ReverseAudio=true},"trim-reversed","audio-wav");
var trimmed=Samples(trimReverse.Output,"trimmed-reverse-result");
Check(trimmed.SequenceEqual(original.Skip(11025).Take(44100).Reverse()),"Reverse applies only to the selected interval, not the remaining source tail");
var audioFade=Convert(multi,new(){VideoCodec="copy",AudioFadeIn=.8,AudioFadeOut=.5},"audio-fades-only");
Check(PacketHashes(multi,"v:0").SequenceEqual(PacketHashes(audioFade.Output,"v:0")),"Independent audio fades leave video packets unchanged");
var fadedSamples=Samples(audioFade.Output,"faded");var sourceSamples=Samples(multi,"unfaded");
Check(Rms(fadedSamples,.04,.12)<Rms(sourceSamples,.04,.12)*.3 && Rms(fadedSamples,1.85,1.95)<Rms(sourceSamples,1.85,1.95)*.4,"Actual output contains both audio fades");
var videoFade=Convert(multi,new(){FadeIn=.8,AudioFadeIn=0,AudioFadeOut=0,AudioCodec="copy"},"video-fade-only");
Check(PacketHashes(multi,"a:0").SequenceEqual(PacketHashes(videoFade.Output,"a:0")),"Explicit zero audio fade prevents legacy video-fade coupling");
var impulse=Wave("impulse",1,t=>t<.02?.5:0);
var echo=Convert(impulse,new(){Format="wav",Echo=true},"echo","audio-wav");var echoSamples=Samples(echo.Output,"echo-result");
Check(Rms(echoSamples,.08,.1)>1000,"Echo creates a delayed signal in originally silent samples");
Check(Math.Abs(engine.Probe(echo.Output).GetAwaiter().GetResult().Duration-1)<.01,"Echo retains the selected duration");
var noise=Path.Combine(root,"noise.wav");FF("-v","error","-n","-f","lavfi","-i","anoisesrc=color=white:amplitude=0.005:sample_rate=44100:duration=3:seed=42","-c:a","pcm_s16le",noise);
var denoise=Convert(noise,new(){Format="wav",NoiseReduction=true},"denoised","audio-wav");
Check(Rms(Samples(denoise.Output,"denoised-result"),.5,2.5)<Rms(Samples(noise,"noise-source"),.5,2.5)*.95,"Denoising reduces measured white-noise energy");
var legacy=JsonSerializer.Deserialize<ConversionOptions>("{\"Format\":\"mp4\",\"FadeIn\":0.5}")!;
Check(legacy.AudioFadeIn is null && MediaEngine.HasAudioFilters(legacy),"Existing queue records retain their linked fade behavior");
var storage=new Storage(Path.Combine(root,"state"));storage.SavePreset("advanced",new(){Echo=true,NoiseReduction=true,ReverseAudio=true,AudioFadeIn=.4,KeepAllAudioStreams=true,VideoCodec="copy"});
var preset=storage.LoadPresets()["advanced"];
Check(preset.Echo&&preset.NoiseReduction&&preset.ReverseAudio&&preset.KeepAllAudioStreams&&preset.AudioFadeIn==.4&&preset.VideoCodec=="copy","New options persist in saved presets");

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();Motion.SetReducedMotion(true);
void Pump(){Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();}
T Find<T>(Window w,string name) where T:Control=>w.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
var draft=new ConversionOptions{Format="mkv",Volume=.75};var window=new OptionsWindow(draft,presetStorage:storage);window.Show();Pump();
var tabs=window.GetVisualDescendants().OfType<TabControl>().Single();
Check(tabs.Items.Cast<TabItem>().Select(t=>t.Header).SequenceEqual(new[]{"视频","音频","字幕","其他","水印"}),"Video output configuration has all five pages");
tabs.SelectedIndex=1;Pump();
Check(Find<TextBox>(window,"VolumePercent").Text=="75","Volume is displayed as a percentage");
Find<TextBox>(window,"VolumePercent").Text="50";Find<TextBox>(window,"AudioFadeInInput").Text="0.3";
foreach(var control in window.GetVisualDescendants().OfType<CheckBox>().Where(c=>c.Content is string s && new[]{"启用回声","启用降噪","反向播放所选音频","保留全部音轨"}.Contains(s)))control.IsChecked=true;
var read=window.ReadOptions();Check(read.Volume==.5&&read.AudioFadeIn==.3&&read.FadeIn==0&&read.Echo&&read.NoiseReduction&&read.ReverseAudio&&read.KeepAllAudioStreams,"Audio-page edits populate independent output parameters");
Check(draft.Volume==.75&&!draft.Echo&&draft.AudioFadeIn is null,"Editing the configuration leaves caller draft unchanged");
Find<ComboBox>(window,"AudioCodecCombo").SelectedItem="copy";
var rejected=false;try{window.ReadOptions();}catch(ArgumentException){rejected=true;}
Check(rejected,"Audio-page confirmation validates copy/effect conflicts");Find<ComboBox>(window,"AudioCodecCombo").SelectedItem="自动";
Pump();window.CaptureRenderedFrame()!.Save(Path.Combine(root,"audio-options-light.png"));
Application.Current!.RequestedThemeVariant=ThemeVariant.Dark;Pump();window.CaptureRenderedFrame()!.Save(Path.Combine(root,"audio-options-dark.png"));
window.Close();
var locked=new OptionsWindow(new(){Format="mp4"},true,presetStorage:storage);locked.Show();Pump();
Check(Find<ComboBox>(locked,"VideoCodecCombo").SelectedItem as string=="copy"&&!Find<ComboBox>(locked,"VideoCodecCombo").IsEnabled,"Fast Copy displays and locks video Copy");
locked.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=1;Pump();
Check(Find<ComboBox>(locked,"AudioCodecCombo").SelectedItem as string=="copy"&&!Find<ComboBox>(locked,"AudioCodecCombo").IsEnabled&&locked.ReadOptions().CopyStreams,"Fast Copy displays and locks audio Copy without rewriting codec preferences");locked.Close();
Check(SHA256.HashData(File.ReadAllBytes(multi)).SequenceEqual(sourceHash),"All conversions preserve source media bytes");
File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{platform=System.Runtime.InteropServices.RuntimeInformation.OSDescription,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),checks=checks.Count,results=checks,outputs},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Verified {checks.Count} checks. {root}");
