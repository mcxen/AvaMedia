using System.Security.Cryptography;
using System.Text.Json;
#if !CORE_ONLY
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Desktop;
#endif
using AvaMedia.Core;

var root=Path.GetFullPath("artifacts/subtitles-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var engine=new MediaEngine(new());var checks=new List<string>();var outputs=new List<string>();
var multi=Path.Combine(root,"双画面 双声音 字幕 O'Brien [test],.mkv");
void Check(bool ok,string text){if(!ok)throw new Exception(text);checks.Add(text);Console.WriteLine("PASS "+text);}
ProcessResult FF(params string[] args){var r=ProcessRunner.Run(engine.FFmpeg,args).GetAwaiter().GetResult();if(r.ExitCode!=0)throw new Exception(r.Error);return r;}
Job Convert(ConversionOptions o,string name,string? source=null,string id="mp4")
{
    var job=new Job{FeatureId=id,Inputs=[source??multi],Output=Path.Combine(root,name+"."+o.Format),Options=o};engine.Execute(job,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(job.Output);return job;
}
JsonElement[] Streams(string path){using var doc=JsonDocument.Parse(engine.Probe(path).GetAwaiter().GetResult().RawJson);return doc.RootElement.GetProperty("streams").EnumerateArray().Select(e=>e.Clone()).ToArray();}
string[] Hashes(string path,string stream)
{
    var r=ProcessRunner.Run(engine.FFprobe,["-v","error","-select_streams",stream,"-show_packets","-show_data_hash","sha256","-of","json",path]).GetAwaiter().GetResult();using var doc=JsonDocument.Parse(r.Output);return doc.RootElement.GetProperty("packets").EnumerateArray().Select(p=>p.GetProperty("data_hash").GetString()!).ToArray();
}
byte[] PcmHash(string path,string stream){var raw=Path.Combine(root,Guid.NewGuid()+".pcm");FF("-v","error","-n","-i",path,"-map",stream,"-c:a","pcm_s16le","-f","s16le",raw);var hash=SHA256.HashData(File.ReadAllBytes(raw));File.Delete(raw);return hash;}
byte[] Frame(string path,double time,string format="gray")
{
    var raw=Path.Combine(root,Guid.NewGuid()+".raw");FF("-v","error","-n","-ss",MediaEngine.Number(time),"-i",path,"-frames:v","1","-pix_fmt",format,"-f","rawvideo",raw);var bytes=File.ReadAllBytes(raw);File.Delete(raw);return bytes;
}
double Energy(string path,double time)=>Frame(path,time).Average(v=>(double)v);
string Extract(string path,int index=0){var srt=Path.Combine(root,Guid.NewGuid()+".srt");FF("-v","error","-n","-i",path,"-map",$"0:s:{index}","-c:s","srt",srt);return File.ReadAllText(srt);}
bool Reject(ConversionOptions o){try{MediaEngine.ValidateEncodingOptions(o);return false;}catch(ArgumentException){return true;}}
var first=Path.Combine(root,"English.srt");var second=Path.Combine(root,"字幕 O'Brien [test],.srt");
File.WriteAllText(first,"1\n00:00:00,200 --> 00:00:01,000\nFIRST TRACK\n");File.WriteAllText(second,"1\n00:00:01,500 --> 00:00:02,500\nSECOND TRACK\n");
FF("-v","error","-n","-f","lavfi","-i","color=black:size=320x180:rate=25","-f","lavfi","-i","color=red:size=160x90:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=44100","-f","lavfi","-i","sine=frequency=880:sample_rate=48000","-i",first,"-i",second,"-map","0:v","-map","1:v","-map","2:a","-map","3:a","-map","4:s","-map","5:s","-t","4","-c:v","mpeg4","-c:a","pcm_s16le","-c:s","srt","-metadata:s:a:1","language=zho","-metadata:s:s:0","language=eng","-metadata:s:s:1","language=zho",multi);
var sourceHash=SHA256.HashData(File.ReadAllBytes(multi));
var chosen=engine.Probe(multi,videoStreamIndex:1,audioStreamIndex:1).GetAwaiter().GetResult();
Check(chosen.Width==160&&chosen.Height==90&&chosen.AudioSampleRate==48000,"Probe describes the selected video and audio streams");
var selected=Convert(new(){Format="mkv",CopyStreams=true,VideoStreamIndex=1,AudioStreamIndex=1},"selected",id:"clip");
Check(Hashes(multi,"v:1").SequenceEqual(Hashes(selected.Output,"v:0"))&&Hashes(multi,"a:1").SequenceEqual(Hashes(selected.Output,"a:0")),"Selected streams are copied byte-for-byte instead of default streams");
Check(Streams(selected.Output).All(s=>s.GetProperty("codec_type").GetString()!="subtitle"),"Disabled subtitles create no subtitle stream");
var audio=Convert(new(){Format="wav",AudioStreamIndex=1},"selected-audio",id:"split");
Check(PcmHash(multi,"0:a:1").SequenceEqual(PcmHash(audio.Output,"0:a:0")),"Audio extraction selects the requested source track");
var preserve=Convert(new(){Format="mkv",CopyStreams=true,SubtitleMode=SubtitleMode.Preserve,KeepAllAudioStreams=true},"preserve-all");
Check(Streams(preserve.Output).Count(s=>s.GetProperty("codec_type").GetString()=="subtitle")==2,"Preserve mode keeps both independent subtitle tracks");
Check(Extract(preserve.Output).Contains("FIRST TRACK")&&Extract(preserve.Output,1).Contains("SECOND TRACK"),"Both preserved subtitle tracks retain distinct text");
Check(Streams(preserve.Output).Where(s=>s.GetProperty("codec_type").GetString()=="subtitle").Select(s=>s.GetProperty("tags").GetProperty("language").GetString()).SequenceEqual(new[]{"eng","zho"}),"Preserved subtitle languages survive mapping");
var soft=Convert(new(){SubtitleMode=SubtitleMode.Preserve,SubtitleStreamIndex=1},"soft-mp4");
Check(Streams(soft.Output).Single(s=>s.GetProperty("codec_type").GetString()=="subtitle").GetProperty("codec_name").GetString()=="mov_text"&&Extract(soft.Output).Contains("SECOND TRACK"),"MP4 converts only the selected subtitle track to mov_text");
var external=Convert(new(){Format="mkv",CopyStreams=true,SubtitleMode=SubtitleMode.ExternalTrack,Subtitle=second,SubtitleLanguage="eng"},"external-soft");
Check(Extract(external.Output).Contains("SECOND TRACK")&&!Extract(external.Output).Contains("FIRST TRACK"),"External track replaces source subtitles without burning the image");
Check(Hashes(multi,"v:0").SequenceEqual(Hashes(external.Output,"v:0")),"Adding a subtitle track permits video stream copy");
Check(Streams(external.Output).Single(s=>s.GetProperty("codec_type").GetString()=="subtitle").GetProperty("tags").GetProperty("language").GetString()=="eng","External subtitle language override is written to the output");
var cut=Convert(new(){SubtitleMode=SubtitleMode.ExternalTrack,Subtitle=second,Start=1,End=3},"soft-trim");
Check(Extract(cut.Output).Contains("00:00:00,500 --> 00:00:01,500"),"External subtitle times follow the chosen clip interval");
var burn=Convert(new(){SubtitleMode=SubtitleMode.BurnIn,SubtitleStreamIndex=1,SubtitleFontSize=36},"source-burn");
Check(Energy(burn.Output,.5)<.2&&Energy(burn.Output,2)>.4,"Source burn-in renders the selected subtitle only during its cue");
Check(Streams(burn.Output).All(s=>s.GetProperty("codec_type").GetString()!="subtitle"),"Burn-in contains pixels and no extra subtitle track");
var styled=Convert(new(){Subtitle=second,SubtitleFont="Arial",SubtitleFontSize=36,SubtitleColor="#FF0000",SubtitleAlignment=8,SubtitleMargin=8},"styled-path");
var rgb=Frame(styled.Output,2,"rgb24");var red=Enumerable.Range(0,rgb.Length/3).Where(i=>rgb[i*3]>70&&rgb[i*3]>rgb[i*3+1]*2&&rgb[i*3]>rgb[i*3+2]*2).ToArray();
Check(red.Length>50&&red.Average(i=>i/320d)<90,"Font color and top alignment alter the rendered subtitle pixels");
Check(SubtitleOptions.Mode(new(){Subtitle=second})==SubtitleMode.BurnIn,"Legacy external-subtitle presets keep burn-in semantics");
var speed=Convert(new(){Subtitle=second,Start=1,End=3,Speed=2,SubtitleFontSize=36},"burn-trim-speed");
Check(Energy(speed.Output,.08)<.2&&Energy(speed.Output,.5)>.4&&Energy(speed.Output,.9)<.2,"Burn-in subtitles remain synchronized after seek and double speed");
Check(Math.Abs(engine.Probe(speed.Output).GetAwaiter().GetResult().Duration-1)<.12,"Subtitle burn-in preserves requested clip duration and speed");
var join=new Job{FeatureId="join",Inputs=[multi,multi],Output=Path.Combine(root,"join-burn.mp4"),Options=new(),InputOptions=[new(){Start=1,End=3,SubtitleMode=SubtitleMode.BurnIn,SubtitleStreamIndex=1,SubtitleFontSize=36},new(){VideoStreamIndex=1,AudioStreamIndex=1,Start=1,End=3}]};
engine.Execute(join,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(join.Output);
Check(Energy(join.Output,.2)<.2&&Energy(join.Output,1)>.4&&Energy(join.Output,3)>20,"Join applies each input's subtitle and video selection independently");
Check(Reject(new(){CopyStreams=true,SubtitleMode=SubtitleMode.BurnIn}),"Stream copy rejects source subtitle burn-in before encoding");
Check(Reject(new(){Format="avi",SubtitleMode=SubtitleMode.Preserve}),"Unsupported soft-subtitle containers are rejected");
Check(Reject(new(){Speed=2,SubtitleMode=SubtitleMode.Preserve}),"Unsynchronized soft-subtitle speed changes are rejected explicitly");
Check(Reject(new(){Format="wav",SubtitleMode=SubtitleMode.BurnIn}),"Audio-only output rejects meaningless subtitle burn-in");
Check(Reject(new(){SubtitleFont="Arial,FontSize=200"})&&Reject(new(){SubtitleColor="white"})&&Reject(new(){AudioStreamIndex=-1}),"Invalid styles and track indices are rejected");
var absent=false;try{Convert(new(){SubtitleMode=SubtitleMode.Preserve,SubtitleStreamIndex=4},"invalid");}catch(ArgumentException){absent=true;}Check(absent&&!File.Exists(Path.Combine(root,"invalid.mp4")),"Missing subtitle selection fails without creating output");
var externalAbsent=false;try{Convert(new(){SubtitleMode=SubtitleMode.BurnIn,Subtitle=second,SubtitleStreamIndex=4},"invalid-external");}catch(ArgumentException){externalAbsent=true;}Check(externalAbsent&&!File.Exists(Path.Combine(root,"invalid-external.mp4")),"External burn-in validates its subtitle index before creating output");
var invalidAudio=false;try{engine.Probe(multi,audioStreamIndex:4).GetAwaiter().GetResult();}catch(ArgumentException){invalidAudio=true;}Check(invalidAudio,"Missing audio selection fails during probing");
var thumbs=engine.Thumbnail(multi,2,160,90,videoStreamIndex:1).GetAwaiter().GetResult();var other=engine.Thumbnail(multi,2,160,90).GetAwaiter().GetResult();Check(!thumbs.SequenceEqual(other),"Thumbnail generation follows video selection");
#if !CORE_ONLY
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();Motion.SetReducedMotion(true);
void Pump(){Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();}
T Find<T>(Window w,string name) where T:Control=>w.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
var storage=new Storage(Path.Combine(root,"state"));var draft=new ConversionOptions{Format="mkv"};var window=new OptionsWindow(draft,presetStorage:storage);window.Show();Pump();var tabs=window.GetVisualDescendants().OfType<TabControl>().Single();
Find<TextBox>(window,"VideoStreamIndex").Text="1";tabs.SelectedIndex=1;Pump();Find<TextBox>(window,"AudioStreamIndex").Text="1";tabs.SelectedIndex=2;Pump();
Find<ComboBox>(window,"SubtitleModeCombo").SelectedIndex=3;Find<TextBox>(window,"SubtitlePath").Text=second;Find<TextBox>(window,"SubtitleLanguage").Text="zho";Find<TextBox>(window,"SubtitleStreamIndex").Text="0";Find<TextBox>(window,"SubtitleFont").Text="Arial";Find<TextBox>(window,"SubtitleFontSize").Text="36";Find<TextBox>(window,"SubtitleColor").Text="#FF0000";
var read=window.ReadOptions();Check(read.VideoStreamIndex==1&&read.AudioStreamIndex==1&&read.SubtitleMode==SubtitleMode.ExternalTrack&&read.Subtitle==second&&read.SubtitleStreamIndex==0&&read.SubtitleLanguage=="zho"&&read.SubtitleFontSize==36,"Configuration pages read track, subtitle and style options");
Check(draft.SubtitleMode==SubtitleMode.Auto&&draft.AudioStreamIndex==0,"Configuration edits leave the caller draft isolated");storage.SavePreset("subtitle",read);var saved=storage.LoadPresets()["subtitle"];Check(saved.SubtitleMode==read.SubtitleMode&&saved.SubtitleColor==read.SubtitleColor&&saved.AudioStreamIndex==1,"Subtitle and stream selections persist in presets");
Pump();window.CaptureRenderedFrame()!.Save(Path.Combine(root,"subtitles-light.png"));Application.Current!.RequestedThemeVariant=ThemeVariant.Dark;Pump();window.CaptureRenderedFrame()!.Save(Path.Combine(root,"subtitles-dark.png"));window.Close();
#endif
Check(SHA256.HashData(File.ReadAllBytes(multi)).SequenceEqual(sourceHash),"All subtitle and track operations preserve source bytes");
File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{platform=System.Runtime.InteropServices.RuntimeInformation.OSDescription,checks=checks.Count,results=checks,outputs},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Verified {checks.Count} checks. {root}");
