using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

var root=Path.GetFullPath("artifacts/quick-workflow-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var engine=new MediaEngine(new(){AutoDetectGpu=false});var checks=0;var outputs=new List<string>();
void Check(bool value,string label){if(!value)throw new Exception(label);checks++;}
ProcessResult FF(params string[] args)=>Task.Run(()=>ProcessRunner.Run(engine.FFmpeg,args)).GetAwaiter().GetResult();
MediaInfo Probe(string path)=>Task.Run(()=>engine.Probe(path)).GetAwaiter().GetResult();
var source=Path.Combine(root,"片段 O'Brien space.mp4");var portrait=Path.Combine(root,"竖屏.mp4");
Check(FF("-v","error","-n","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-t","4","-c:v","mpeg4","-q:v","2","-c:a","aac",source).ExitCode==0,"Create landscape fixture");
Check(FF("-v","error","-n","-f","lavfi","-i","testsrc2=size=180x320:rate=25","-t","3","-c:v","mpeg4","-q:v","2",portrait).ExitCode==0,"Create portrait fixture");
var hash=SHA256.HashData(File.ReadAllBytes(source));var info=Probe(source);
var complex=new ConversionOptions{Start=.4,End=2.4,CropX=24,CropY=12,CropWidth=160,CropHeight=100,Rotation=90,Flip=true,Speed=2,FadeIn=.1,FadeOut=.1,Volume=.7,AudioFadeIn=.05,AudioFadeOut=.05,NoiseReduction=true};
var edits=new[]{new ClipEditResult(source,info,[complex,new(){Start=2,End=3.2,Rotation=180}])};
var merged=QuickClipWorkflow.PrepareExports(edits,"MKV",new(){VideoCodec="mpeg4",Quality=7,AudioBitrate=128,SampleRate=44100,AudioChannels=1,KeepMetadata=false});
Check(merged.Count==2 && merged.All(x=>x.Options.Format=="mkv"&&!x.Options.CopyStreams&&x.Options.Quality==7&&x.Options.SampleRate==44100),"Apply export settings to all segments");
Check(merged[0].Options is {Start:.4,End:2.4,CropX:24,CropY:12,CropWidth:160,CropHeight:100,Rotation:90,Flip:true,Speed:2,FadeIn:.1,FadeOut:.1,Volume:.7,AudioFadeIn:.05,AudioFadeOut:.05,NoiseReduction:true},"Export merge preserves all edits");
merged[0].Options.Start=0;Check(complex.Start==.4 && merged[1].Options.Start==2,"Drafts and segments remain independent");
void Reject(Action action,string label){try{action();}catch(ArgumentException){Check(true,label);return;}throw new Exception(label);}
Reject(()=>QuickClipWorkflow.PrepareExports(edits,"Fast Copy",new()),"Reject filtered Fast Copy");
Reject(()=>QuickClipWorkflow.PrepareExports([new(source,info,[new(){Start=5}])],"MP4",new()),"Reject beyond-duration intervals before enqueue");
Reject(()=>QuickClipWorkflow.PrepareExports([new(source,info,[new(){CropWidth=400,CropHeight=100}])],"MP4",new()),"Reject invalid spatial edits before enqueue");

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();Motion.SetReducedMotion(true);
var detector=new ResultDetector(new(270,OrientationReliability.High,8,8,8,"8 帧方向一致"));
var editor=new EditorWindow(engine,source,new(),"quick-workflow",orientationDetector:detector);editor.Show();Pump(editor.Ready);
Check(editor.Segments.Count==1 && editor.FindControl<Button>("ConfirmButton")!.Content!.ToString()!.Contains("导出选项"),"Editor opens with one active segment and export next step");
Check(editor.FindControl<TabItem>("DirectionTab")!.IsVisible && editor.FindControl<Border>("SegmentPane")!.IsVisible,"Direction editing and the segment pane are immediately available");
SetRange(editor,.4,1.6);SetCrop(editor,24,12,160,100);SetDirection(editor,90);Click(editor,"AddSegmentButton");
Check(editor.Segments.Count==2 && editor.Segments[0].Options is {Start:.4,End:1.6,CropX:24,Rotation:90},"Adding a segment saves the original draft");
SetRange(editor,2,3.2);SetCrop(editor,0,0,0,0);SetDirection(editor,180);
var read=editor.ReadClipEdit();Check(read.Segments[0].Start==.4&&read.Segments[1] is{Start:2,End:3.2,Rotation:180,CropWidth:0},"Each segment keeps distinct interval, crop and rotation");
editor.FindControl<ListBox>("SegmentList")!.SelectedIndex=0;Dispatcher.UIThread.RunJobs();
Check(editor.ReadDraft() is{Start:.4,End:1.6,CropX:24,Rotation:90},"Selecting a segment restores its draft");
editor.FindControl<TextBox>("StartTime")!.Text="invalid";editor.FindControl<ListBox>("SegmentList")!.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
Check(editor.FindControl<ListBox>("SegmentList")!.SelectedIndex==0 && !string.IsNullOrEmpty(editor.FindControl<TextBlock>("SegmentError")!.Text),"Invalid edits block segment switching");
SetRange(editor,.4,1.6);Click(editor,"SegmentDownButton");Check(editor.Segments[1].Options.Rotation==90,"Reorder preserves draft identity");
Click(editor,"AddSegmentButton");Check(editor.Segments.Count==3,"Can append another draft");Click(editor,"RemoveSegmentButton");Check(editor.Segments.Count==2,"Remove only selected segment");
editor.FindControl<ListBox>("SegmentList")!.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
editor.FindControl<TabControl>("EditTabs")!.SelectedItem=editor.FindControl<TabItem>("DirectionTab");
Click(editor,"DetectDirectionButton");Pump(editor.DirectionReady);
Check(editor.ReadDraft().Rotation==90 && editor.FindControl<Button>("ApplyDirectionButton")!.IsEnabled,"Detection suggests a direction without overwriting edits");
Click(editor,"ApplyDirectionButton");Check(editor.ReadDraft().Rotation==270,"Apply face direction to the selected segment");
Check(editor.ReadClipEdit().Segments[0].Rotation==180,"Face suggestion does not change other segments");
SetDirection(editor,90);Capture(editor,"editor-direction-light.png",1000,730);AssertVisible(editor,"DirectionCombo");AssertVisible(editor,"ConfirmButton");
editor.FindControl<TabControl>("EditTabs")!.SelectedIndex=0;Capture(editor,"editor-segments-light.png",1000,730);AssertVisible(editor,"SegmentDownButton");
Application.Current!.RequestedThemeVariant=ThemeVariant.Dark;Capture(editor,"editor-segments-dark.png",1000,730);
read=editor.ReadClipEdit();editor.Close();
var switchEditor=new EditorWindow(engine,source,new(){Start=.4,End=1.6},"quick-workflow",segments:[new(){Start=.4,End=1.6},new(){Start=.4,End=2.4}]);switchEditor.Show();Pump(switchEditor.Ready);Click(switchEditor,"PlaySelectionButton");Pump(switchEditor.PlaybackReady);
var switchDeadline=DateTime.UtcNow.AddSeconds(5);while(switchEditor.FindControl<Image>("PreviewImage")!.Source is not WriteableBitmap){Pump(Task.CompletedTask);Thread.Sleep(5);if(DateTime.UtcNow>switchDeadline)throw new TimeoutException("Segment playback did not display a decoded frame.");}
switchEditor.FindControl<ListBox>("SegmentList")!.SelectedIndex=1;Pump(switchEditor.PreviewReady);Check(!switchEditor.IsPreviewPlaying&&switchEditor.FindControl<Slider>("SeekBar")!.Value==.4&&switchEditor.ReadDraft().End==2.4,"Switching segments with identical starts stops old playback and restores the selected interval");
Check(switchEditor.FindControl<Image>("PreviewImage")!.Source is Bitmap and not WriteableBitmap&&switchEditor.FindControl<TextBlock>("CurrentTime")!.Text=="00:00:00.400","Segment switching replaces the playback frame with the selected source position");switchEditor.Close();
var splitEditor=new EditorWindow(engine,source,complex,"quick-workflow");splitEditor.Show();Pump(splitEditor.Ready);Click(splitEditor,"SplitSegmentButton");
var splitter=splitEditor.OwnedWindows.OfType<ClipSplitWindow>().Single();ClickButton(splitter.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"取消")));
Check(splitEditor.Segments.Count==1,"Cancel split retains original draft");Click(splitEditor,"SplitSegmentButton");splitter=splitEditor.OwnedWindows.OfType<ClipSplitWindow>().Single();
ClickButton(splitter.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"确定")));Dispatcher.UIThread.RunJobs();
var splitRead=splitEditor.ReadClipEdit();Check(splitRead.Segments.Count==2&&splitRead.Segments[0].Start==.4&&splitRead.Segments[0].End==1.4&&splitRead.Segments[1].Start==1.4&&splitRead.Segments[1].End==2.4,"Splitting a segment replaces only its source-time interval");
Check(splitRead.Segments.All(s=>s is{CropWidth:160,Rotation:90,Speed:2,Flip:true,Volume:.7}),"Split editor preserves editing parameters in every part");splitEditor.Close();

var uncertain=new EditorWindow(engine,source,new(){Rotation=180},"quick-workflow",orientationDetector:new ResultDetector(new(null,OrientationReliability.Unknown,8,0,0,"未找到人脸")));uncertain.Show();Pump(uncertain.Ready);Click(uncertain,"DetectDirectionButton");Pump(uncertain.DirectionReady);
Check(!uncertain.FindControl<Button>("ApplyDirectionButton")!.IsEnabled && uncertain.ReadDraft().Rotation==180,"Unknown orientation retains manual rotation");uncertain.Close();
var blocking=new BlockingDetector();var cancelEditor=new EditorWindow(engine,source,new(),"quick-workflow",orientationDetector:blocking);cancelEditor.Show();Pump(cancelEditor.Ready);Click(cancelEditor,"DetectDirectionButton");
PumpUntil(()=>blocking.Started==1);Check(true,"Detection started");Click(cancelEditor,"CancelDetectionButton");Pump(cancelEditor.DirectionReady);Check(cancelEditor.FindControl<Button>("DetectDirectionButton")!.IsEnabled&&blocking.Cancelled==1,"Canceled detection restores controls and cancels inference");
Click(cancelEditor,"DetectDirectionButton");PumpUntil(()=>blocking.Started==2);cancelEditor.Close();Check(!cancelEditor.DirectionReady.IsCompleted&&blocking.Cancelled==1,"Closing editor keeps its background detection running");blocking.Release();Pump(cancelEditor.DirectionReady);Check(true,"Background detection completes after its editor closes");

var export=new ClipExportWindow([read],root);export.Show();
Check(export.FindControl<ComboBox>("FormatCombo")!.SelectedItem is "Fast Copy"&&!export.FindControl<Button>("JoinQueueButton")!.IsEnabled,"Default source-format export blocks filtered segments before submission");
export.FindControl<ComboBox>("FormatCombo")!.SelectedItem="MP4";Dispatcher.UIThread.RunJobs();Check(export.CreateRequest().ClipInputs!.Count==2&&export.CreateRequest().ClipInputs!.All(i=>!i.Options.CopyStreams),"Selecting MP4 enables re-encoding for all edited segments");
export.FindControl<ComboBox>("FormatCombo")!.SelectedItem="MKV";Dispatcher.UIThread.RunJobs();Click(export,"ExportOptionsButton");
var options=export.OwnedWindows.OfType<OptionsWindow>().Single();
Check(!options.GetVisualDescendants().OfType<TextBox>().Any(t=>t.Name is "VideoStreamIndex" or "AudioStreamIndex" or "VolumePercent"),"Export options contain only output settings");
options.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="VideoCodecCombo").SelectedItem="mpeg4";
options.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex=1;Dispatcher.UIThread.RunJobs();
options.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="AudioSampleRateCombo").SelectedItem="44100";
ClickButton(options.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"确定")));
var request=export.CreateRequest();Check(request.ClipInputs![1].Options is{Rotation:90,CropWidth:160,SampleRate:44100,VideoCodec:"mpeg4"},"Output options do not reset segment edits");
Capture(export,"export-dark.png",820,520);AssertVisible(export,"JoinQueueButton");AssertVisible(export,"ExportFolder");Application.Current.RequestedThemeVariant=ThemeVariant.Light;Capture(export,"export-light.png",820,520);export.Close();

var jobs=QuickClipBatch.CreateJobs(request.ClipInputs!,Path.Combine(root,"output"));
foreach(var job in jobs){Pump(Task.Run(()=>engine.Execute(job,_=>{},CancellationToken.None)));outputs.Add(job.Output);}
var firstOutput=Probe(jobs[0].Output);var secondOutput=Probe(jobs[1].Output);
Check(firstOutput is{Width:320,Height:180,HasAudio:true} && Math.Abs(firstOutput.Duration-1.2)<.1,"First output retains its own dimensions and duration");
Check(secondOutput is{Width:100,Height:160,HasAudio:true,AudioSampleRate:44100}&&Math.Abs(secondOutput.Duration-1.2)<.1,"Second output applies trim, crop, rotation and output settings together");
Check(jobs[0].Output!=jobs[1].Output,"Repeated source outputs have unique filenames");
var expectedRaw=Path.Combine(root,"expected.rgb");var actualRaw=Path.Combine(root,"actual.rgb");
Check(FF("-v","error","-n","-ss","0.4","-i",source,"-vf","crop=160:100:24:12,transpose=1","-frames:v","1","-pix_fmt","rgb24","-f","rawvideo",expectedRaw).ExitCode==0,"Decode expected crop and rotation");
Check(FF("-v","error","-n","-i",jobs[1].Output,"-frames:v","1","-pix_fmt","rgb24","-f","rawvideo",actualRaw).ExitCode==0,"Decode actual exported segment");
var expectedPixels=File.ReadAllBytes(expectedRaw);var actualPixels=File.ReadAllBytes(actualRaw);
Check(actualPixels.Length==expectedPixels.Length && actualPixels.Select((p,i)=>Math.Abs(p-expectedPixels[i])).Average()<10,"Output pixels confirm crop offset and clockwise rotation");

// Drive the current multi-source workspace, editors and export actions into the actual queue.
var storage=new Storage(Path.Combine(root,"state"));storage.SaveSettings(new(){OutputFolder=Path.Combine(root,"queue-output"),AutoDetectGpu=false,CheckForUpdates=false,AutoUpdate=false});
var main=new MainWindow(storage);main.Show();var workflow=main.EditQuickClipAsync([source,portrait]);
Window Workspace(){PumpUntil(()=>main.OwnedWindows.Any(window=>window.GetType().Name=="QuickClipWorkspaceWindow"));return main.OwnedWindows.Single(window=>window.GetType().Name=="QuickClipWorkspaceWindow");}
EditorWindow EditSource(Window workspace,int index)
{
    workspace.GetVisualDescendants().OfType<ListBox>().Single().SelectedIndex=index;Dispatcher.UIThread.RunJobs();
    ClickButton(workspace.GetVisualDescendants().OfType<Button>().Single(button=>button.Content is "剪辑此视频…" or "继续编辑…"));
    PumpUntil(()=>workspace.OwnedWindows.OfType<EditorWindow>().Any());var editor=workspace.OwnedWindows.OfType<EditorWindow>().Single();Pump(editor.Ready);return editor;
}
void ExportWorkspace(Window workspace)=>ClickButton(workspace.GetVisualDescendants().OfType<Button>().Single(button=>Equals(button.Content,"导出选项…")));
var workspace=Workspace();var first=EditSource(workspace,0);
Check(!main.OwnedWindows.OfType<ClipExportWindow>().Any(),"Source workspace opens the editor before export");
SetRange(first,.4,1.6);SetDirection(first,90);Click(first,"AddSegmentButton");SetRange(first,2,3.2);SetDirection(first,180);
Check(storage.LoadJobs().Count==0,"Editing creates no queue entries");Click(first,"ConfirmButton");PumpUntil(()=>!workspace.OwnedWindows.Any());
var next=EditSource(workspace,1);Check(next.Title!.Contains(Path.GetFileName(portrait)),"Selecting another source opens its own editor");SetRange(next,.2,1.2);Click(next,"ConfirmButton");PumpUntil(()=>!workspace.OwnedWindows.Any());ExportWorkspace(workspace);
PumpUntil(()=>main.OwnedWindows.OfType<ClipExportWindow>().Any());var finalExport=main.OwnedWindows.OfType<ClipExportWindow>().Single();
Check(storage.LoadJobs().Count==0&&!finalExport.FindControl<Button>("JoinQueueButton")!.IsEnabled,"Queue stays empty and filtered source-copy export is blocked");
finalExport.FindControl<ComboBox>("FormatCombo")!.SelectedItem="MKV";Dispatcher.UIThread.RunJobs();Check(finalExport.CreateRequest().ClipInputs!.Count==3,"Re-encoding export includes all three edited segments");
var customFolder=Path.Combine(root,"custom-output");finalExport.FindControl<TextBox>("ExportFolder")!.Text=customFolder;Click(finalExport,"BackToEditingButton");
workspace=Workspace();var restored=EditSource(workspace,0);
Check(restored.Segments.Count==2&&restored.ReadClipEdit().Segments[1] is{Start:2,End:3.2,Rotation:180},"Returning to edit restores multiple drafts");
Click(restored,"ConfirmButton");PumpUntil(()=>!workspace.OwnedWindows.Any());ExportWorkspace(workspace);
PumpUntil(()=>main.OwnedWindows.OfType<ClipExportWindow>().Any());finalExport=main.OwnedWindows.OfType<ClipExportWindow>().Single();
Check(finalExport.ReadState().Preset=="MKV"&&finalExport.ReadState().Folder==customFolder,"Returning from edit retains export format and destination");
finalExport.GetVisualDescendants().OfType<CheckBox>().Single(choice=>Equals(choice.Content,"仅加入队列")).IsChecked=true;Click(finalExport,"JoinQueueButton");Pump(workflow);
var queued=storage.LoadJobs();Check(queued.Count==3&&queued.All(j=>j.FeatureId=="clip"&&j.State==JobState.Waiting&&j.Options.Format=="mkv"),"Final queue-only confirmation creates the complete waiting batch");
Check(queued[0].Options.Rotation==90&&queued[1].Options.Rotation==180&&queued[2].Inputs.Single()==portrait,"Queue preserves per-segment and per-source edits");
foreach(var job in queued){Pump(Task.Run(()=>engine.Execute(job,_=>{},CancellationToken.None)));outputs.Add(job.Output);}Check(queued.All(job=>Probe(job.Output).HasVideo),"Jobs produced by the main-window workflow export playable videos");
workflow=main.EditQuickClipAsync([source]);PumpUntil(()=>main.OwnedWindows.OfType<EditorWindow>().Any());next=main.OwnedWindows.OfType<EditorWindow>().Single();Pump(next.Ready);Click(next,"EditorCancelButton");Pump(workflow);Check(storage.LoadJobs().Count==3,"Cancel editor adds no jobs");
workflow=main.EditQuickClipAsync([source]);PumpUntil(()=>main.OwnedWindows.OfType<EditorWindow>().Any());next=main.OwnedWindows.OfType<EditorWindow>().Single();Pump(next.Ready);Click(next,"ConfirmButton");PumpUntil(()=>main.OwnedWindows.OfType<ClipExportWindow>().Any());Click(main.OwnedWindows.OfType<ClipExportWindow>().Single(),"ExportCancelButton");Pump(workflow);Check(storage.LoadJobs().Count==3,"Cancel export adds no jobs");main.Close();
var clean=QuickClipWorkflow.PrepareExports([new(source,info,[new(){Start=.5,End=2}])],"Fast Copy",new());Check(clean.Single().Options.CopyStreams,"Simple trims still support final Fast Copy option");
var realDetector=new VideoOrientationDetector(engine);var detection=Task.Run(()=>realDetector.DetectAsync(source,info));Pump(detection);
Check(detection.Result.Rotation is null && detection.Result.SampledFrames>0,"Bundled face detector returns unknown for no-face footage");
var face=Path.Combine(root,"横倒人脸.mp4");
Check(FF("-v","error","-n","-loop","1","-i",Path.Combine(AppContext.BaseDirectory,"Fixtures","astronaut.png"),"-t","1.2","-r","25","-vf","pad=640:512:64:0,transpose=1","-c:v","mpeg4","-q:v","2",face).ExitCode==0,"Create sideways public-domain face fixture");
var faceEditor=new EditorWindow(engine,face,new(),"quick-workflow");faceEditor.Show();Pump(faceEditor.Ready);Click(faceEditor,"DetectDirectionButton");Pump(faceEditor.DirectionReady);
Check(faceEditor.FindControl<Button>("ApplyDirectionButton")!.IsEnabled,"Real face detection produces an actionable suggestion in editor");Click(faceEditor,"ApplyDirectionButton");
Check(faceEditor.ReadDraft().Rotation==270,"Editor applies real sideways-face correction");
var faceRequest=QuickClipWorkflow.PrepareExports([faceEditor.ReadClipEdit()],"MP4",new(){VideoCodec="mpeg4"});faceEditor.Close();
var faceJob=QuickClipBatch.CreateJobs(faceRequest,Path.Combine(root,"face-output")).Single();Pump(Task.Run(()=>engine.Execute(faceJob,_=>{},CancellationToken.None)));outputs.Add(faceJob.Output);
Check(Probe(faceJob.Output) is{Width:640,Height:512},"Detected face correction survives export and returns upright geometry");
Check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hash),"Original media is unchanged");
File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{checks,outputs,editorFirst=true,backRestoresDrafts=true,queueOnlyAfterExport=true},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS: {checks} quick workflow checks / {outputs.Count} actual outputs. {root}");

void SetRange(EditorWindow w,double start,double end){w.FindControl<TextBox>("StartTime")!.Text=TimeSpan.FromSeconds(start).ToString(@"hh\:mm\:ss\.fff");w.FindControl<TextBox>("EndTime")!.Text=TimeSpan.FromSeconds(end).ToString(@"hh\:mm\:ss\.fff");Dispatcher.UIThread.RunJobs();}
void SetCrop(EditorWindow w,int x,int y,int width,int height){w.FindControl<TextBox>("CropX")!.Text=x.ToString();w.FindControl<TextBox>("CropY")!.Text=y.ToString();w.FindControl<TextBox>("CropWidth")!.Text=width.ToString();w.FindControl<TextBox>("CropHeight")!.Text=height.ToString();Dispatcher.UIThread.RunJobs();}
void SetDirection(EditorWindow w,int angle){w.FindControl<ComboBox>("DirectionCombo")!.SelectedIndex=angle/90;Dispatcher.UIThread.RunJobs();}
void Click(Window w,string name)=>ClickButton(w.FindControl<Button>(name)!);
void ClickButton(Button b){b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();}
void Pump(Task task){PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
void PumpUntil(Func<bool> condition){var timeout=DateTime.UtcNow.AddSeconds(60);while(!condition()){Dispatcher.UIThread.RunJobs();if(DateTime.UtcNow>timeout)throw new TimeoutException("UI action did not finish");Thread.Sleep(5);}Dispatcher.UIThread.RunJobs();}
void Capture(Window target,string name,int width,int height){target.Width=width;target.Height=height;target.Measure(new Size(width,height));target.Arrange(new Rect(0,0,width,height));Dispatcher.UIThread.RunJobs();using var bitmap=new RenderTargetBitmap(new PixelSize(width,height),new Vector(96,96));bitmap.Render(target);bitmap.Save(Path.Combine(root,name));}
void AssertVisible(Window target,string name){var control=target.FindControl<Control>(name)!;var p=control.TranslatePoint(new Point(control.Bounds.Width,control.Bounds.Height),target);Check(control.Bounds.Width>0&&control.Bounds.Height>0&&p is{} point&&point.X<=target.Width&&point.Y<=target.Height,$"Visible at minimum size: {name}");}

sealed class ResultDetector(VideoOrientationResult result):IVideoOrientationDetector
{
    public Task<VideoOrientationResult> DetectAsync(string path,MediaInfo info,IProgress<OrientationDetectionProgress>? progress=null,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(result);}
}
sealed class BlockingDetector:IVideoOrientationDetector
{
    private int _started,_cancelled;
    private readonly TaskCompletionSource<VideoOrientationResult> _completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Started=>Volatile.Read(ref _started);
    public int Cancelled=>Volatile.Read(ref _cancelled);
    public void Release()=>_completion.TrySetResult(new(null,OrientationReliability.Unknown,0,0,0,"Completed"));
    public async Task<VideoOrientationResult> DetectAsync(string path,MediaInfo info,IProgress<OrientationDetectionProgress>? progress=null,CancellationToken ct=default)
    {
        Interlocked.Increment(ref _started);
        try{return await _completion.Task.WaitAsync(ct);}
        catch(OperationCanceledException){Interlocked.Increment(ref _cancelled);throw;}
    }
}
