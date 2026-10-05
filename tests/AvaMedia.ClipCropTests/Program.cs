using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;
#if !CORE_ONLY
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;
#endif

var root=Path.GetFullPath("artifacts/clip-crop-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var engine=new MediaEngine(new());var checks=new List<string>();var outputs=new List<string>();
void Check(bool ok,string message){if(!ok)throw new Exception(message);checks.Add(message);Console.WriteLine("PASS "+message);}
void FF(params string[] args){var r=ProcessRunner.Run(engine.FFmpeg,args).GetAwaiter().GetResult();if(r.ExitCode!=0)throw new Exception(r.Error);}
var constant=Path.Combine(root,"逐帧 O'Brien.mp4");
FF("-v","error","-n","-f","lavfi","-i","nullsrc=size=128x72:rate=5,geq=lum=25+N*25:cb=128:cr=128","-t","1.2","-c:v","mpeg4","-q:v","2",constant);
byte[] Thumb(string input,double at,bool exclusive=false)=>engine.Thumbnail(input,at,128,72,pad:false,endExclusive:exclusive).GetAwaiter().GetResult();
Check(Math.Abs(engine.Probe(constant).GetAwaiter().GetResult().FrameRate-5)<.0001,"Probe reads selected video frame rate");
Check(Thumb(constant,.6,true).SequenceEqual(Thumb(constant,.4)),"End preview excludes the frame exactly at the cut boundary");
Check(!Thumb(constant,.6,true).SequenceEqual(Thumb(constant,.6)),"End preview differs from the first excluded frame");
Check(Thumb(constant,1.2,true).SequenceEqual(Thumb(constant,1)),"Full-duration end preview displays the final included frame");
Check(Thumb(constant,.1,true).SequenceEqual(Thumb(constant,0)),"Sub-frame interval preview includes the first valid frame");
var variable=Path.Combine(root,"可变帧率.mkv");
FF("-v","error","-n","-f","lavfi","-i","nullsrc=size=128x72:rate=10,geq=lum=25+N*25:cb=128:cr=128","-frames:v","6","-vf",@"setpts=if(eq(N\,0)\,0\,if(eq(N\,1)\,1\,if(eq(N\,2)\,3\,if(eq(N\,3)\,6\,if(eq(N\,4)\,10\,16)))))","-fps_mode","vfr","-c:v","ffv1",variable);
Check(Thumb(variable,.55,true).SequenceEqual(Thumb(variable,.3)),"End preview uses actual timestamps in variable-rate video");
Check(Thumb(variable,1.6,true).SequenceEqual(Thumb(variable,1)),"Variable-rate cut excludes the next irregular frame");
Check(Math.Abs(engine.AdjacentFrameTime(variable,.3,1).GetAwaiter().GetResult()-.6)<.00001,"Forward frame stepping follows irregular frame timestamps");
Check(Math.Abs(engine.AdjacentFrameTime(variable,.6,-1).GetAwaiter().GetResult()-.3)<.00001,"Backward frame stepping follows irregular frame timestamps");
var sparse=Path.Combine(root,"低帧率.mkv");FF("-v","error","-n","-f","lavfi","-i","nullsrc=size=128x72:rate=1/4,geq=lum=25+N*50:cb=128:cr=128","-frames:v","3","-c:v","ffv1",sparse);
Check(Thumb(sparse,7,true).SequenceEqual(Thumb(sparse,4)),"Boundary preview handles gaps longer than one second");
Check(Math.Abs(engine.AdjacentFrameTime(sparse,0,1).GetAwaiter().GetResult()-4)<.00001,"Frame stepping crosses long timestamp gaps without a guessed rate");
var offset=Path.Combine(root,"时间戳偏移.ts");FF("-v","error","-n","-i",constant,"-c:v","mpeg2video","-bf","0","-output_ts_offset","5","-muxdelay","0","-muxpreload","0","-f","mpegts",offset);
Check(Thumb(offset,.6,true).SequenceEqual(Thumb(offset,.4)),"Boundary preview normalizes nonzero source timestamps");
Check(Math.Abs(engine.AdjacentFrameTime(offset,.4,1).GetAwaiter().GetResult()-.6)<.00001,"Frame stepping normalizes transport-stream timestamps");
var info=engine.Probe(constant).GetAwaiter().GetResult();
void Reject(Action action,string label){try{action();}catch(ArgumentException){Check(true,label);return;}throw new Exception(label);}
Reject(()=>CropGeometry.Validate(new(1,0,64,40),info),"Video crop rejects odd pixel coordinates consistently");
Reject(()=>CropGeometry.Validate(new(int.MaxValue-1,0,64,40),info),"Crop bounds cannot overflow integer arithmetic");
Reject(()=>CropGeometry.Validate(new(0,0,0,40),info),"Zero-area video crop is rejected");
CropGeometry.Validate(new(1,1,63,39),info,false);Check(true,"Image crop can retain exact odd-pixel geometry");
var sourceHash=SHA256.HashData(File.ReadAllBytes(constant));
var job=new Job{FeatureId="crop",Inputs=[constant],Output=Path.Combine(root,"clip-crop.mp4"),Options=new(){Start=.2,End=1,CropX=32,CropY=16,CropWidth=64,CropHeight=40}};
engine.Execute(job,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(job.Output);var result=engine.Probe(job.Output).GetAwaiter().GetResult();
Check(result.Width==64&&result.Height==40&&Math.Abs(result.Duration-.8)<.03,"Combined trimming and crop produce requested dimensions and interval");
var invalid=new Job{FeatureId="crop",Inputs=[constant],Output=Path.Combine(root,"odd-crop.mp4"),Options=new(){CropX=1,CropWidth=64,CropHeight=40}};
Reject(()=>engine.Execute(invalid,_=>{},CancellationToken.None).GetAwaiter().GetResult(),"Engine refuses odd crop before creating media output");Check(!File.Exists(invalid.Output),"Rejected crop does not create an empty output file");
var imageCrop=new Job{FeatureId="image-png",Inputs=[constant],Output=Path.Combine(root,"odd-image-crop.png"),Options=new(){Format="png",CropX=1,CropY=1,CropWidth=63,CropHeight=39}};
engine.Execute(imageCrop,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(imageCrop.Output);var imageInfo=engine.Probe(imageCrop.Output).GetAwaiter().GetResult();Check(imageInfo.Width==63&&imageInfo.Height==39,"Image export preserves odd crop pixels from a subsampled video source");
var repeatedOptions=new[]{new ConversionOptions{Start=.2,End=.6},new ConversionOptions{Start=.6,End=1}};
var repeatedJobs=ConversionBatch.CreateJobs(Catalog.Find("mp4"),[constant,constant],Path.Combine(root,"repeated"),new(),repeatedOptions);
Check(repeatedJobs.Count==2&&repeatedJobs[0].Options.Start==.2&&repeatedJobs[1].Options.Start==.6,"Repeated source conversions preserve each row's own interval");
Check(repeatedJobs[0].Output!=repeatedJobs[1].Output,"Repeated sources reserve distinct output paths");
repeatedOptions[0].Start=0;Check(repeatedJobs[0].Options.Start==.2,"Queue creation isolates drafts from the source list");
var invalidFolder=Path.Combine(root,"rejected-batch");Reject(()=>ConversionBatch.CreateJobs(Catalog.Find("mp4"),[constant,constant],invalidFolder,new(),[new(),new(){Start=.8,End=.6}]),"A later invalid row rejects the complete conversion batch");Check(!Directory.Exists(invalidFolder),"Rejected complete batch creates no output directories");
Reject(()=>ConversionBatch.CreateJobs(Catalog.Find("mp4"),[constant,constant],invalidFolder,new(),[new()]),"A mismatched input-option list is rejected");
var emptyClip=new Job{FeatureId="join",Inputs=[constant,constant],Output=Path.Combine(root,"empty-join.mp4"),Options=new(),InputOptions=[new(){Start=2},new()]};
Reject(()=>engine.Execute(emptyClip,_=>{},CancellationToken.None).GetAwaiter().GetResult(),"A merge input cannot silently trim beyond its duration");Check(!File.Exists(emptyClip.Output),"Rejected merge interval creates no empty media file");
var muxAudio=Path.Combine(root,"混流音频.wav");FF("-v","error","-n","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","2","-c:a","pcm_s16le",muxAudio);
var mutedMux=new Job{FeatureId="mux",Inputs=[constant,muxAudio],Options=new(),InputOptions=[new(){Start=.2,End=1},new(){End=.2,Mute=true}],Output=Path.Combine(root,"muted-mux.mp4")};engine.Execute(mutedMux,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(mutedMux.Output);var mutedInfo=engine.Probe(mutedMux.Output).GetAwaiter().GetResult();Check(!mutedInfo.HasAudio&&Math.Abs(mutedInfo.Duration-.8)<.03&&!mutedMux.Options.Mute,"Muted mux source removes output audio without shortening or mutating the video draft");
string[] Packets(string path,string stream){var result=ProcessRunner.Run(engine.FFprobe,["-v","error","-select_streams",stream,"-show_packets","-show_entries","packet=data_hash","-show_data_hash","sha256","-of","json",path]).GetAwaiter().GetResult();if(result.ExitCode!=0)throw new Exception(result.Error);using var json=JsonDocument.Parse(result.Output);return json.RootElement.GetProperty("packets").EnumerateArray().Select(p=>p.GetProperty("data_hash").GetString()!).ToArray();}
var videoCopyMux=new Job{FeatureId="mux",Inputs=[constant,muxAudio],Output=Path.Combine(root,"video-copy-audio-edit.mkv"),Options=new(){Format="mkv",VideoCodec="copy"},InputOptions=[new(),new(){Volume=.5}]};engine.Execute(videoCopyMux,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(videoCopyMux.Output);Check(Packets(videoCopyMux.Output,"v:0").SequenceEqual(Packets(constant,"v:0")),"Mux preserves video packet hashes while independently editing audio");
var audioCopyMux=new Job{FeatureId="mux",Inputs=[constant,muxAudio],Output=Path.Combine(root,"audio-copy-video-crop.mkv"),Options=new(){Format="mkv",AudioCodec="copy"},InputOptions=[new(){CropX=32,CropY=16,CropWidth=64,CropHeight=40},new()]};engine.Execute(audioCopyMux,_=>{},CancellationToken.None).GetAwaiter().GetResult();outputs.Add(audioCopyMux.Output);var copiedAudio=Packets(audioCopyMux.Output,"a:0");Check(copiedAudio.Length>0&&copiedAudio.SequenceEqual(Packets(muxAudio,"a:0").Take(copiedAudio.Length))&&engine.Probe(audioCopyMux.Output).GetAwaiter().GetResult() is{Width:64,Height:40},"Mux crops video and copies original audio packets without a filter conflict");
var copyConflict=new Job{FeatureId="mux",Inputs=[constant,muxAudio],Output=Path.Combine(root,"copy-conflict.mkv"),Options=new(){Format="mkv",VideoCodec="copy"},InputOptions=[new(){Start=.2,End=1},new()]};Reject(()=>engine.Execute(copyConflict,_=>{},CancellationToken.None).GetAwaiter().GetResult(),"Edited copied mux stream is rejected before encoding");Check(!File.Exists(copyConflict.Output),"Copied mux conflict creates no empty output file");

#if !CORE_ONLY
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();Motion.SetReducedMotion(true);
void Pump(Task? task=null){var deadline=DateTime.UtcNow.AddSeconds(30);do{Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();if(task is null||task.IsCompleted)break;if(DateTime.UtcNow>deadline)throw new TimeoutException();Thread.Sleep(5);}while(true);task?.GetAwaiter().GetResult();}
void Click(Button b){b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();}
T Find<T>(Window w,string name)where T:Control=>w.FindControl<T>(name)!;
var draft=new ConversionOptions();var editor=new EditorWindow(engine,constant,draft,"clip");editor.Show();Pump(editor.Ready);
Check(Find<Button>(editor,"ConfirmButton").IsEnabled,"Loaded editor enables confirmation only with valid fields");
Find<TextBox>(editor,"EndTime").Text="00:00:00.600";Find<TextBox>(editor,"EndTime").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));Pump(editor.ThumbnailsReady);
var expectedFrame=engine.Thumbnail(constant,.6,960,540,pad:false,endExclusive:true);Pump(expectedFrame);using(var expected=new Bitmap(new MemoryStream(expectedFrame.Result))){using var rendered=new MemoryStream();((Bitmap)Find<Image>(editor,"EndImage").Source!).Save(rendered);using var target=new MemoryStream();expected.Save(target);Check(rendered.ToArray().SequenceEqual(target.ToArray()),"Editor end image uses the final frame inside the chosen interval");}
Find<TextBox>(editor,"StartTime").Text="00:00:00.800";Check(!Find<Button>(editor,"ConfirmButton").IsEnabled&&Find<TextBlock>(editor,"TimeError").Text!.Length>0,"Reversed time interval has inline feedback and blocks submission");Find<TextBox>(editor,"StartTime").Text="00:00:00.200";
Find<TextBox>(editor,"CropX").Text="1";Find<TextBox>(editor,"CropWidth").Text="64";Find<TextBox>(editor,"CropHeight").Text="40";Check(!Find<Button>(editor,"ConfirmButton").IsEnabled,"Odd crop fields block editor confirmation");
Find<TextBox>(editor,"CropX").Text="100";Check(Find<TextBlock>(editor,"CropError").Text!.Contains("超出"),"Out-of-bounds crop is reported before queue submission");
Find<TextBox>(editor,"CropX").Text="32";Find<TextBox>(editor,"CropY").Text="16";Check(Find<Button>(editor,"ConfirmButton").IsEnabled,"Valid geometry restores confirmation");
var edited=editor.ReadDraft();Check(edited.Start==.2&&edited.End==.6&&edited.CropWidth==64&&draft.CropWidth==0,"Editor combines trim and crop without changing caller draft");
Find<ComboBox>(editor,"PrecisionCombo").SelectedIndex=3;Find<Slider>(editor,"SeekBar").Value=.2;Click(Find<Button>(editor,"ForwardButton"));Pump(editor.PositionReady);Check(Math.Abs(Find<Slider>(editor,"SeekBar").Value-.4)<.0001,"One-frame precision follows actual source frame timestamps");
string? firstPlaybackTime=null;var firstPlaybackPixel=-1;var preview=Find<Image>(editor,"PreviewImage");
preview.PropertyChanged+=(_,eventArgs)=>{if(eventArgs.Property==Image.SourceProperty&&firstPlaybackTime is null&&preview.Source is WriteableBitmap decoded){firstPlaybackTime=Find<TextBlock>(editor,"CurrentTime").Text;using var buffer=decoded.Lock();firstPlaybackPixel=System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address);}};
Click(Find<Button>(editor,"PlaySelectionButton"));var playbackDeadline=DateTime.UtcNow.AddSeconds(5);while(editor.IsPreviewPlaying||Math.Abs(Find<Slider>(editor,"SeekBar").Value-.6)>.0001){Pump();Thread.Sleep(10);if(DateTime.UtcNow>playbackDeadline)throw new TimeoutException($"Interval playback did not stop: position={Find<Slider>(editor,"SeekBar").Value}, label={Find<TextBlock>(editor,"CurrentTime").Text}, playing={editor.IsPreviewPlaying}, status={Find<TextBlock>(editor,"PreviewStatus").Text}.");}Pump();Check(Find<TextBlock>(editor,"CurrentTime").Text=="00:00:00.600"&&Find<ActionIcon>(editor,"PlayIcon").Kind=="play","Selection playback stops at the chosen endpoint with the play action restored");
Check(firstPlaybackTime=="00:00:00.200"&&firstPlaybackPixel>20,"Playback publishes a decoded first frame at the selected source time, without a blank frame or startup clock offset");
Check(Find<TextBlock>(editor,"SelectionDuration").Text=="00:00:00.400","Interval duration retains millisecond precision");
void WaitFor(Func<bool> done,string message){var deadline=DateTime.UtcNow.AddSeconds(8);while(!done()){Pump();Thread.Sleep(5);if(DateTime.UtcNow>deadline)throw new TimeoutException(message);}Pump();}
byte[] ImageBytes(Image image){using var memory=new MemoryStream();((Bitmap)image.Source!).Save(memory);return memory.ToArray();}
var heldFrames=new HeldPreview(engine);var heldEditor=new EditorWindow(engine,constant,new(){Start=.2,End=.6},"clip",previewFrames:heldFrames);heldEditor.Show();Pump(heldEditor.Ready);
Check(Find<TextBlock>(heldEditor,"CurrentTime").Text=="00:00:00.200"&&Find<Slider>(heldEditor,"SeekBar").Value==.2,"Loading a saved clip aligns source preview position and current-time label with its start");
heldFrames.HoldEnds(.6);Click(Find<Button>(heldEditor,"PlaySelectionButton"));Pump(heldEditor.PlaybackReady);
WaitFor(()=>!heldEditor.IsPreviewPlaying&&Find<TextBlock>(heldEditor,"CurrentTime").Text=="00:00:00.600"&&heldFrames.HeldEnds>=2,"Boundary previews did not reach the delayed delivery gate.");
var obsoleteEndPreview=heldEditor.PreviewReady;var obsoleteThumbnails=heldEditor.ThumbnailsReady;Find<Slider>(heldEditor,"SeekBar").Value=.8;Pump(heldEditor.PreviewReady);var freshPreview=ImageBytes(Find<Image>(heldEditor,"PreviewImage"));Find<TextBox>(heldEditor,"EndTime").Text="00:00:01.000";Find<TextBox>(heldEditor,"EndTime").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));Pump(heldEditor.ThumbnailsReady);var freshEndPreview=ImageBytes(Find<Image>(heldEditor,"EndImage"));heldFrames.ReleaseEnds();Pump(obsoleteEndPreview);Pump(obsoleteThumbnails);
Check(Find<TextBlock>(heldEditor,"CurrentTime").Text=="00:00:00.800"&&Find<Slider>(heldEditor,"SeekBar").Value==.8&&ImageBytes(Find<Image>(heldEditor,"PreviewImage")).SequenceEqual(freshPreview),"A delayed selection-end frame cannot overwrite a newer seek's time or decoded pixels");
Check(ImageBytes(Find<Image>(heldEditor,"EndImage")).SequenceEqual(freshEndPreview)&&heldEditor.ReadDraft().End==1,"A delayed old endpoint thumbnail cannot replace a corrected interval's endpoint image");
var requestedPreview=engine.Thumbnail(constant,.8,960,540,pad:false);Pump(requestedPreview);using(var requested=new Bitmap(new MemoryStream(requestedPreview.Result))){using var expected=new MemoryStream();requested.Save(expected);Check(freshPreview.SequenceEqual(expected.ToArray()),"The newer seek displays pixels from its actual source position");}
heldFrames.HoldSteps();Find<ComboBox>(heldEditor,"PrecisionCombo").SelectedIndex=3;Find<Slider>(heldEditor,"SeekBar").Value=.2;Pump(heldEditor.PreviewReady);Click(Find<Button>(heldEditor,"ForwardButton"));WaitFor(()=>heldFrames.HeldSteps>0,"Frame-step delivery was not held.");var obsoleteStep=heldEditor.PositionReady;Find<Slider>(heldEditor,"SeekBar").Value=.8;Pump(heldEditor.PreviewReady);heldFrames.ReleaseSteps();Pump(obsoleteStep);
Check(Find<Slider>(heldEditor,"SeekBar").Value==.8&&Find<TextBlock>(heldEditor,"CurrentTime").Text=="00:00:00.800","A delayed frame step cannot replace a newer direct seek");
Find<TextBox>(heldEditor,"EndTime").Text="00:00:00.800";Find<TextBox>(heldEditor,"EndTime").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));Pump(heldEditor.ThumbnailsReady);heldFrames.HoldSteps();Click(Find<Button>(heldEditor,"EndPlusButton"));WaitFor(()=>heldFrames.HeldSteps>0,"Boundary-step delivery was not held.");var obsoleteBoundary=heldEditor.PositionReady;Find<TextBox>(heldEditor,"EndTime").Text="00:00:00.400";Find<TextBox>(heldEditor,"EndTime").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));heldFrames.ReleaseSteps();Pump(obsoleteBoundary);Pump(heldEditor.ThumbnailsReady);Check(heldEditor.ReadDraft().End==.4&&Find<TextBox>(heldEditor,"EndTime").Text=="00:00:00.400","A delayed boundary step cannot overwrite a newer typed interval");
Click(Find<Button>(heldEditor,"PlayButton"));Pump(heldEditor.PlaybackReady);WaitFor(()=>Find<Image>(heldEditor,"PreviewImage").Source is WriteableBitmap,"Playback did not decode a frame before stop.");Click(Find<Button>(heldEditor,"StopButton"));Pump(heldEditor.PreviewReady);
Check(!heldEditor.IsPreviewPlaying&&Find<Slider>(heldEditor,"SeekBar").Value==0&&Find<TextBlock>(heldEditor,"CurrentTime").Text=="00:00:00.000","Stop cancels playback and displays the actual source beginning");heldEditor.Close();
var closeFrames=new HeldPreview(engine);var closingEditor=new EditorWindow(engine,constant,new(){Start=.2,End=.6},"clip",previewFrames:closeFrames);closingEditor.Show();Pump(closingEditor.Ready);closeFrames.HoldEnds();Click(Find<Button>(closingEditor,"PlaySelectionButton"));Pump(closingEditor.PlaybackReady);WaitFor(()=>!closingEditor.IsPreviewPlaying&&closeFrames.HeldEnds>=2&&Find<TextBlock>(closingEditor,"CurrentTime").Text=="00:00:00.600","Closing fixture did not enter pending endpoint rendering.");var closedPreview=closingEditor.PreviewReady;var closedThumbnails=closingEditor.ThumbnailsReady;closingEditor.Close();closeFrames.ReleaseEnds();Pump(closedPreview);Pump(closedThumbnails);Check(!closingEditor.IsVisible&&!closingEditor.IsPreviewPlaying,"Closing with pending preview deliveries cancels them without touching disposed bitmaps");
var invalidEditor=new EditorWindow(engine,constant,new(){Start=.2,End=2},"clip");invalidEditor.Show();Pump(invalidEditor.Ready);Check(Find<TextBox>(invalidEditor,"EndTime").Text=="00:00:02.000"&&!Find<Button>(invalidEditor,"ConfirmButton").IsEnabled&&Find<TextBlock>(invalidEditor,"TimeError").Text!.Length>0,"Opening an out-of-range saved clip reports its unchanged end instead of silently shortening it");invalidEditor.Close();
var extendedAudio=Path.Combine(root,"短画面长音轨.mkv");Pump(Task.Run(()=>FF("-v","error","-n","-i",constant,"-i",muxAudio,"-map","0:v","-map","1:a","-c:v","copy","-c:a","pcm_s16le",extendedAudio)));
var extendedEditor=new EditorWindow(engine,extendedAudio,new(),"clip");extendedEditor.Show();Pump(extendedEditor.Ready);Click(Find<Button>(extendedEditor,"SoundButton"));Click(Find<Button>(extendedEditor,"PlayButton"));Pump(extendedEditor.PlaybackReady);WaitFor(()=>Find<Slider>(extendedEditor,"SeekBar").Value>=1.5,"Playback did not continue through the longer audio interval.");
Check(extendedEditor.IsPreviewPlaying&&Find<Image>(extendedEditor,"PreviewImage").Source is WriteableBitmap,"Video EOF retains its last frame while the media's longer audio interval continues");WaitFor(()=>!extendedEditor.IsPreviewPlaying,"Extended-audio playback did not finish.");Pump(extendedEditor.PreviewReady);Check(Find<TextBlock>(extendedEditor,"CurrentTime").Text=="00:00:02.000","Extended-audio preview completes at the media duration instead of video EOF");extendedEditor.Close();
editor.CaptureRenderedFrame()!.Save(Path.Combine(root,"editor-clip-light.png"));
var tabs=Find<TabControl>(editor,"EditTabs");tabs.SelectedIndex=1;Click(editor.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"应用坐标")));
var layer=Find<CropOverlay>(editor,"CropLayer");
Point Pixel(double x,double y){var scale=Math.Min(layer.Bounds.Width/layer.SourceWidth,layer.Bounds.Height/layer.SourceHeight);return layer.TranslatePoint(new Point((layer.Bounds.Width-layer.SourceWidth*scale)/2+x*scale,(layer.Bounds.Height-layer.SourceHeight*scale)/2+y*scale),editor)!.Value;}
editor.MouseDown(Pixel(60,36),MouseButton.Left);editor.MouseMove(Pixel(76,44));editor.MouseUp(Pixel(76,44),MouseButton.Left);Pump();var moved=editor.ReadDraft();if(moved is not {CropX:48,CropY:24,CropWidth:64,CropHeight:40})throw new Exception($"Crop movement: {moved.CropX},{moved.CropY},{moved.CropWidth},{moved.CropHeight}; layer={layer.Bounds}; selection={layer.Selection}; enabled={layer.Enabled}; pixel={Pixel(60,36)}.");Check(true,"Dragging inside moves the existing crop without changing its size");
editor.MouseDown(Pixel(112,64),MouseButton.Left);editor.MouseMove(Pixel(104,56));editor.MouseUp(Pixel(104,56),MouseButton.Left);Pump();Check(editor.ReadDraft() is {CropX:48,CropY:24,CropWidth:56,CropHeight:32},"Dragging a corner resizes the crop with an anchored opposite corner");
layer.Focus();editor.KeyPressQwerty(PhysicalKey.ArrowLeft,RawInputModifiers.None);Pump();Check(editor.ReadDraft().CropX==46,"Focused crop supports precise keyboard movement");
var fullReset=editor.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"重置选区"));Click(fullReset);Check(editor.ReadDraft().CropWidth==0,"Reset disables cropping instead of adding a full-frame filter");
Find<ComboBox>(editor,"CropRatio").SelectedItem="1:1";editor.MouseDown(Pixel(10,10),MouseButton.Left);editor.MouseMove(Pixel(70,50));editor.MouseUp(Pixel(70,50),MouseButton.Left);Pump();Check(editor.ReadDraft() is {CropWidth:40,CropHeight:40},"Drawing with a ratio lock creates the expected square crop");
Pump();editor.CaptureRenderedFrame()!.Save(Path.Combine(root,"editor-crop-light.png"));Application.Current!.RequestedThemeVariant=ThemeVariant.Dark;Pump();editor.CaptureRenderedFrame()!.Save(Path.Combine(root,"editor-crop-dark.png"));editor.Width=1000;editor.Height=730;Pump();editor.CaptureRenderedFrame()!.Save(Path.Combine(root,"editor-minimum.png"));Check(Find<Button>(editor,"ConfirmButton").TranslatePoint(new Point(180,30),editor) is {} position&&position.X<=1000&&position.Y<=730,"Minimum editor size keeps confirmation visible");editor.Close();
var rangeWindow=new Window{Width=320,Height=100,Content=new RangeBar{Duration=.005,End=.005}};rangeWindow.Show();Pump();var bar=(RangeBar)rangeWindow.Content!;var at=bar.TranslatePoint(new Point(0,10),rangeWindow)!.Value;rangeWindow.MouseDown(at,MouseButton.Left);rangeWindow.MouseUp(at,MouseButton.Left);Pump();Check(bar.Start>=0&&bar.End<=bar.Duration&&bar.End>bar.Start,"Timeline handles very short media without negative boundaries");rangeWindow.Close();
var owner=new Window();owner.Show();Pump();
Button Labeled(Window window,string content)=>window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,content)||(content=="✓ 确定"&&Equals(b.Content,"确定")));
void Until(Func<bool> done){var deadline=DateTime.UtcNow.AddSeconds(15);while(!done()){Pump();Thread.Sleep(5);if(DateTime.UtcNow>deadline)throw new TimeoutException("Conversion workflow timed out.");}Pump();}
async Task<byte> LumaAt(string path,double time){using var process=ProcessRunner.Start(engine.FFmpeg,["-v","error","-ss",MediaEngine.Number(time),"-i",path,"-frames:v","1","-vf","scale=1:1,format=gray","-f","rawvideo","pipe:1"]);var error=process.StandardError.ReadToEndAsync();var data=new byte[1];await process.StandardOutput.BaseStream.ReadExactlyAsync(data);await process.WaitForExitAsync();if(process.ExitCode!=0)throw new Exception(await error);return data[0];}
var inputs=new[]{new ConversionOptions{Start=0,End=.4,CropX=32,CropY=16,CropWidth=64,CropHeight=40},new ConversionOptions{Start=.6,End=1,CropWidth=64,CropHeight=40}};
var merge=new ConvertWindow(engine,Catalog.Find("join"),root,[constant,constant],inputOptions:inputs);var mergeTask=merge.ShowDialog<ConversionRequest?>(owner);Pump();var mergeList=merge.GetVisualDescendants().OfType<ListBox>().Single();mergeList.SelectedIndex=0;Click(Labeled(merge,"选项 / 剪辑"));var mergeEditor=merge.OwnedWindows.OfType<EditorWindow>().Single();Pump(mergeEditor.Ready);Find<TextBox>(mergeEditor,"StartTime").Text="00:00:00.200";Find<TextBox>(mergeEditor,"EndTime").Text="00:00:00.600";Click(Find<Button>(mergeEditor,"ConfirmButton"));Until(()=>!merge.OwnedWindows.Any());
Check(mergeList.Items.Cast<ConversionEntry>().First().Options!.Start==.2&&mergeList.Items.Cast<ConversionEntry>().Last().Options!.Start==.6&&inputs[0].Start==0,"Editing one repeated merge source leaves the other row and caller unchanged");
Click(Labeled(merge,"下移"));Check(mergeList.Items.Cast<ConversionEntry>().First().Options!.CropX==0&&mergeList.Items.Cast<ConversionEntry>().Last().Options!.CropX==32,"Reordering repeated sources carries their own crop regions");
merge.CaptureRenderedFrame()!.Save(Path.Combine(root,"merge-repeated-dark.png"));Application.Current!.RequestedThemeVariant=ThemeVariant.Light;Pump();merge.CaptureRenderedFrame()!.Save(Path.Combine(root,"merge-repeated-light.png"));Application.Current.RequestedThemeVariant=ThemeVariant.Dark;Pump();Click(Labeled(merge,"✓ 确定"));Pump(mergeTask);var mergeRequest=mergeTask.Result!;var mergeJob=ConversionBatch.CreateJobs(mergeRequest.Feature,mergeRequest.Files,root,mergeRequest.Options,mergeRequest.InputOptions).Single();Pump(engine.Execute(mergeJob,_=>{},CancellationToken.None));outputs.Add(mergeJob.Output);var mergeInfo=engine.Probe(mergeJob.Output);Pump(mergeInfo);Check(Math.Abs(mergeInfo.Result.Duration-.8)<.06&&mergeInfo.Result.HasVideo,"Repeated source merge exports both selected intervals");var mergeSamples=Task.WhenAll(LumaAt(mergeJob.Output,.1),LumaAt(mergeJob.Output,.5));Pump(mergeSamples);Check(mergeSamples.Result[0]-mergeSamples.Result[1]>20,"Decoded merge pixels prove the reordered intervals are in the requested sequence");
var generic=new ConvertWindow(engine,Catalog.Find("mp4"),root,[constant,constant],inputOptions:[new(){Start=.2,End=.4},new(){Start=.6,End=1}]);var genericTask=generic.ShowDialog<ConversionRequest?>(owner);Pump();Click(Labeled(generic,"✓ 确定"));Pump(genericTask);var genericRequest=genericTask.Result!;var genericJobs=ConversionBatch.CreateJobs(genericRequest.Feature,genericRequest.Files,root,genericRequest.Options,genericRequest.InputOptions);foreach(var queued in genericJobs){Pump(engine.Execute(queued,_=>{},CancellationToken.None));outputs.Add(queued.Output);}var firstProbe=engine.Probe(genericJobs[0].Output);Pump(firstProbe);var lastProbe=engine.Probe(genericJobs[1].Output);Pump(lastProbe);Check(Math.Abs(firstProbe.Result.Duration-.2)<.04&&Math.Abs(lastProbe.Result.Duration-.4)<.04,"Repeated source conversion produces the distinct durations accepted by the dialog");
var mux=new ConvertWindow(engine,Catalog.Find("mux"),root,[constant,muxAudio],new(){Format="mp4",Start=.1,End=.5,Speed=2},[new(){Start=.2,End=1},new()]);var muxTask=mux.ShowDialog<ConversionRequest?>(owner);Pump();Click(Labeled(mux,"输出配置"));var muxConfig=mux.OwnedWindows.OfType<OptionsWindow>().Single();Click(Labeled(muxConfig,"确定"));Until(()=>!mux.OwnedWindows.Any());Click(Labeled(mux,"✓ 确定"));Pump(muxTask);var muxRequest=muxTask.Result!;Check(muxRequest.Options.Speed==2&&muxRequest.InputOptions!.All(o=>o.Speed==1)&&muxRequest.InputOptions![1].Start==0,"Mux output settings do not overwrite the independent source timeline");var muxJob=ConversionBatch.CreateJobs(muxRequest.Feature,muxRequest.Files,root,muxRequest.Options,muxRequest.InputOptions).Single();Pump(engine.Execute(muxJob,_=>{},CancellationToken.None));outputs.Add(muxJob.Output);var muxProbe=engine.Probe(muxJob.Output);Pump(muxProbe);Check(Math.Abs(muxProbe.Result.Duration-.2)<.06&&muxProbe.Result.HasAudio&&muxProbe.Result.HasVideo,"Mux applies the output interval and speed exactly once");
var untouchedMux=new ConvertWindow(engine,Catalog.Find("mux"),root,[constant,muxAudio],new(){Format="mp4",Speed=2},[new(){Start=.2,End=1}]);var untouchedTask=untouchedMux.ShowDialog<ConversionRequest?>(owner);Pump();Click(Labeled(untouchedMux,"✓ 确定"));Pump(untouchedTask);Check(untouchedTask.Result!.InputOptions![1].Speed==1,"An unedited mux input starts from its own default parameters");
var badMerge=new ConvertWindow(engine,Catalog.Find("join"),root,[constant,constant],inputOptions:[new(){Start=2},new()]);var badMergeTask=badMerge.ShowDialog<ConversionRequest?>(owner);Pump();Click(Labeled(badMerge,"✓ 确定"));Until(()=>badMerge.OwnedWindows.Any());var problem=badMerge.OwnedWindows.Single();Check(!badMergeTask.IsCompleted&&problem.GetVisualDescendants().OfType<TextBox>().Single().Text!.Contains("超出"),"Dialog preflight retains a merge draft whose source interval is invalid");Click(Labeled(problem,"确定"));Until(()=>!badMerge.OwnedWindows.Any());badMerge.GetVisualDescendants().OfType<ListBox>().Single().Items.Cast<ConversionEntry>().First().SetOptions(new(){Start=.2,End=.6});Click(Labeled(badMerge,"✓ 确定"));Pump(badMergeTask);Check(badMergeTask.Result!.InputOptions![0].Start==.2,"Corrected source interval passes the same preflight validation");owner.Close();
#endif
Check(SHA256.HashData(File.ReadAllBytes(constant)).SequenceEqual(sourceHash),"Clip and crop operations preserve source media bytes");
File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{platform=System.Runtime.InteropServices.RuntimeInformation.OSDescription,checks=checks.Count,results=checks,outputs},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Verified {checks.Count} checks. {root}");

sealed class HeldPreview(IMediaPreview inner):IMediaPreview
{
    private bool _holdEnds,_holdSteps;
    private double? _endAt;
    private TaskCompletionSource _ends=new(TaskCreationOptions.RunContinuationsAsynchronously),_steps=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _heldEnds,_heldSteps;
    public int HeldEnds=>Volatile.Read(ref _heldEnds);
    public int HeldSteps=>Volatile.Read(ref _heldSteps);
    public void HoldEnds(double? at=null){_holdEnds=true;_endAt=at;_heldEnds=0;_ends=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    public void HoldSteps(){_holdSteps=true;_heldSteps=0;_steps=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    public void ReleaseEnds(){_holdEnds=false;_ends.TrySetResult();}
    public void ReleaseSteps(){_holdSteps=false;_steps.TrySetResult();}
    public async Task<byte[]> Thumbnail(string input,double seconds,int width=640,int height=360,CancellationToken ct=default,bool pad=true,int videoStreamIndex=0,bool endExclusive=false)
    {
        var data=await inner.Thumbnail(input,seconds,width,height,ct,pad,videoStreamIndex,endExclusive);
        // Deliberately deliver real decoded data after cancellation to exercise the consumer's stale-result guard.
        if(endExclusive&&_holdEnds&&(_endAt is null||Math.Abs(seconds-_endAt.Value)<.0000001)){var delivery=_ends.Task;Interlocked.Increment(ref _heldEnds);await delivery;}
        return data;
    }
    public async Task<double> AdjacentFrameTime(string input,double seconds,int direction,CancellationToken ct=default,int videoStreamIndex=0)
    {
        var time=await inner.AdjacentFrameTime(input,seconds,direction,ct,videoStreamIndex);
        if(_holdSteps){var delivery=_steps.Task;Interlocked.Increment(ref _heldSteps);await delivery;}
        return time;
    }
}
