using System.Collections.Concurrent;
using System.Diagnostics;
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

var root=Path.GetFullPath("artifacts/progress-"+DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var checks=new List<string>();var outputs=new List<string>();
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks.Add(message);Console.WriteLine("PASS "+message);}
ProgressEstimate At(ProgressEstimator estimator,double percent,double seconds)=>estimator.Update(percent,TimeSpan.FromSeconds(seconds));
var estimate=new ProgressEstimator();
Check(At(estimate,0,0).Text=="估算中"&&At(estimate,1,1).Remaining is null,"Warmup reports insufficient data instead of a fabricated countdown");
var result=At(estimate,20,10);Check(result.State==ProgressEstimateState.Available&&Math.Abs(result.Remaining!.Value.TotalSeconds-40)<.0001,"Ten seconds to process twenty percent estimates forty seconds remaining");
Check(result.Text=="预计剩余 00:00:40","The estimate is labelled and displayed as remaining wall time");
Check(At(estimate,20,16).State==ProgressEstimateState.Stalled,"Unchanged progress expires the numeric estimate after a stall");
Check(At(estimate,40,17).State==ProgressEstimateState.Available,"Progress resuming produces a new estimate");
Check(At(estimate,0,18).Text=="估算中"&&At(estimate,10,19).Remaining is null,"Progress reset starts a fresh warmup after an encoder retry");
Check(At(estimate,20,22).Remaining is {} retryTime&&Math.Abs(retryTime.TotalSeconds-16)<.001,"A retry does not retain the previous attempt's speed or remaining time");
Check(At(estimate,99.9,23).Text=="正在收尾"&&At(estimate,100,24).Remaining is null,"Finalization avoids claiming zero seconds while the output is still being closed");
Check(At(estimate,50,0).Text=="估算中","A restarted monotonic clock invalidates its earlier samples");
var stable=At(estimate,60,3);Check(At(estimate,double.NaN,4)==stable&&At(estimate,double.PositiveInfinity,5)==stable,"Nonfinite backend samples cannot poison the estimate");
estimate=new();At(estimate,0,0);At(estimate,30,3);At(estimate,60,6);for(var second=7;second<=21;second++)result=At(estimate,60+second-6,second);
Check(result.Remaining is {} slower&&slower.TotalSeconds>20,"Recent slower processing replaces the initially faster throughput");
estimate=new();At(estimate,0,0);for(var second=1;second<=21;second++)result=At(estimate,second,second);
Check(result.Remaining is {} constantRate&&Math.Abs(constantRate.TotalSeconds-79)<.001,"Steady throughput remains stable after the rolling window advances");
Check(new ProgressEstimate(ProgressEstimateState.Available,TimeSpan.FromSeconds(.1)).Text.EndsWith("00:00:01"),"Subsecond estimates never display a premature zero");
Check(new ProgressEstimate(ProgressEstimateState.Available,TimeSpan.FromHours(25)+TimeSpan.FromSeconds(1)).Text.EndsWith("25:00:01"),"Long conversions preserve total hours beyond a day");

var clock=new ManualClock();var controlled=new ControlledExecutor();var queue=new QueueService(controlled,clock);var jobs=new[]{new Job{Progress=80},new Job()};var run=queue.Run(jobs,2);
Check(jobs.All(j=>j.Progress==0&&j.RemainingTimeText=="估算中"),"Each actual queue start clears stale progress and starts its own estimate");
clock.Advance(10);controlled.Report(jobs[0],20);controlled.Report(jobs[1],50);
Check(jobs[0].RemainingTimeText.EndsWith("00:00:40")&&jobs[1].RemainingTimeText.EndsWith("00:00:10"),"Parallel jobs own independent speed samples and remaining times");
var propertyNames=new List<string>();jobs[0].PropertyChanged+=(_,args)=>propertyNames.Add(args.PropertyName!);clock.Advance(1);controlled.Report(jobs[0],30);
Check(propertyNames.Contains(nameof(Job.Status))&&jobs[0].Status.Contains("预计剩余"),"Estimate changes notify the status binding shown in the task list");
var serialized=JsonSerializer.Serialize(jobs[0]);Check(!serialized.Contains("预计剩余")&&!serialized.Contains("Estimate")&&!serialized.Contains("RemainingTimeText"),"Runtime estimates are excluded from saved queue data");
clock.Advance(6);controlled.Report(jobs[0],30);Check(jobs[0].Estimate?.State==ProgressEstimateState.Stalled,"A running queue exposes stalled progress without an obsolete ETA");
controlled.Report(jobs[0],0);Check(jobs[0].RemainingTimeText=="估算中","An actual backend progress rollback resets that job's estimate");
controlled.Complete();await run;Check(jobs.All(j=>j.State==JobState.Completed&&j.Progress==100&&j.Estimate is null),"Completed jobs clear remaining time and finish at one hundred percent");
controlled.Report(jobs[0],2);Check(jobs[0].Progress==100&&jobs[0].Estimate is null,"Delayed backend callbacks cannot change a completed job");

controlled=new();queue=new(controlled,clock);var stopped=new Job();run=queue.Run([stopped],1);clock.Advance(10);controlled.Report(stopped,20);queue.Stop();await run;controlled.Report(stopped,80);
Check(stopped.State==JobState.Cancelled&&stopped.Estimate is null&&stopped.Progress==20,"Stopping clears the estimate and rejects late progress callbacks");
stopped.State=JobState.Waiting;controlled=new();queue=new(controlled,clock);run=queue.Run([stopped],1);Check(stopped.Progress==0&&stopped.RemainingTimeText=="估算中","Retrying a stopped job does not reuse its old countdown");controlled.Complete();await run;
await new QueueService(new FailingExecutor()).Run([stopped=new()],1);Check(stopped.State==JobState.Failed&&stopped.RemainingTimeText==""&&stopped.Error=="codec failure","Failures retain the error and clear their estimates");
controlled=new();queue=new(controlled,clock);var downloading=new Job{FeatureId="download"};run=queue.Run([downloading],1);clock.Advance(10);var downloadDetail=typeof(Job).GetProperty("ProgressDetail");downloadDetail?.SetValue(downloading,"2 MiB/s · 剩余 00:40");controlled.Report(downloading,20);
Check(downloading.Estimate is null&&!downloading.Status.Contains("预计剩余")&&(downloadDetail is null||downloading.Status.Contains("剩余 00:40")),"Downloads keep the downloader's own ETA without a duplicate conversion estimate");controlled.Complete();await run;
controlled=new();queue=new(controlled,clock);var waiting=new[]{new Job(),new Job()};run=queue.Run(waiting,1);Check(waiting[1].State==JobState.Waiting&&waiting[1].Estimate is null,"Semaphore waiting time does not produce a task estimate");queue.Stop();await run;Check(waiting[1].State==JobState.Waiting,"Stopping preserves unstarted queue jobs");

var engine=new MediaEngine(new(){AutoDetectGpu=false});var source=Path.Combine(root,"视频 sample.mp4");
var fixture=await ProcessRunner.Run(engine.FFmpeg,["-v","error","-n","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-t","6","-c:v","mpeg4",source]);Check(fixture.ExitCode==0,"Create actual media for the queue progress integration check");
var job=new Job{Inputs=[source],Output=Path.Combine(root,"trim-speed-crop.mp4"),Options=new(){Start=1,End=5,Speed=2,CropWidth=160,CropHeight=100,VideoCodec="mpeg4"}};
var measurements=new List<object>();var numeric=false;var finalizing=false;queue=new(new RealtimeMediaExecutor(engine));queue.Changed+=j=>{if(j.Estimate?.State==ProgressEstimateState.Available)numeric=true;if(j.Estimate?.State==ProgressEstimateState.Finalizing)finalizing=true;measurements.Add(new{j.Progress,j.RemainingTimeText});};
await queue.Run([job],1);outputs.Add(job.Output);var media=await engine.Probe(job.Output);
Check(numeric&&finalizing&&job.State==JobState.Completed&&job.Estimate is null,"Real FFmpeg progress drives numeric ETA, finalization and completion through the queue");
Check(media is{Width:160,Height:100}&&Math.Abs(media.Duration-2)<.06,"ETA tracking preserves actual clip, crop and speed output behavior");
var ordinary=new Job{Inputs=[source],Output=Path.Combine(root,"ordinary.mp4"),Options=new(){VideoCodec="mpeg4"}};await new QueueService(engine).Run([ordinary],1);outputs.Add(ordinary.Output);
Check(ordinary.State==JobState.Completed&&ordinary.Estimate is null&&(await engine.Probe(ordinary.Output)).Duration>5.9,"The production engine still completes and clears ETA on a normal conversion");

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();Motion.SetReducedMotion(true);
void Pump(Task? task=null){var deadline=DateTime.UtcNow.AddSeconds(20);do{Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();if(task is null||task.IsCompleted)break;if(DateTime.UtcNow>deadline)throw new TimeoutException("UI completion timed out");Thread.Sleep(5);}while(true);task?.GetAwaiter().GetResult();}
var uiStore=new Storage(Path.Combine(root,"ui-state"));uiStore.SaveSettings(new(){NotifyComplete=false,MultiThread=true,ParallelJobs=2});uiStore.SaveJobs([new(){Inputs=[source]},new(){Inputs=[source]}]);
var uiExecutor=new UiExecutor(engine);var main=new MainWindow(uiStore,uiExecutor);main.Show();Pump();main.FindControl<Button>("StartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
var uiJobs=main.FindControl<ListBox>("JobList")!.Items.Cast<Job>().ToArray();
Check(main.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Text?.Contains("估算中")==true)==2,"The actual task-list bindings show warmup for both running tasks");
Pump(Task.Delay(2100));uiExecutor.Report(uiJobs[0],20);uiExecutor.Report(uiJobs[1],40);Pump();
Check(main.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Text?.Contains("预计剩余")==true)==2,"The actual task list displays each parallel job's estimated remaining time");
bool EstimatesVisible()=>main.GetVisualDescendants().OfType<TextBlock>().Where(t=>t.Text?.Contains("预计剩余")==true).All(t=>t.Bounds.Width>0&&t.TranslatePoint(new Point(),main) is {} point&&point.X>=0&&point.X+t.Bounds.Width<=main.ClientSize.Width+.5);
Check(EstimatesVisible(),"Long source paths cannot push the estimate beyond the visible task columns");
main.CaptureRenderedFrame()!.Save(Path.Combine(root,"eta-light.png"));Application.Current!.RequestedThemeVariant=ThemeVariant.Dark;Pump();main.CaptureRenderedFrame()!.Save(Path.Combine(root,"eta-dark.png"));
main.Width=main.MinWidth;Pump();Check(EstimatesVisible(),"Estimated remaining time stays inside the smallest supported main window");main.CaptureRenderedFrame()!.Save(Path.Combine(root,"eta-minimum.png"));
var quietDeadline=DateTime.UtcNow.AddSeconds(9);while(main.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Text?.Contains("进度暂未更新")==true)!=2){Pump();Thread.Sleep(10);if(DateTime.UtcNow>quietDeadline)throw new TimeoutException("Silent backend estimates did not expire in the task-list bindings");}
Check(main.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Text?.Contains("进度暂未更新")==true)==2,"The queue timer expires numeric ETA even when the backend stops reporting entirely");
uiExecutor.Complete();var deadline=DateTime.UtcNow.AddSeconds(5);while(uiJobs.Any(j=>j.State==JobState.Running)){Pump();Thread.Sleep(5);if(DateTime.UtcNow>deadline)throw new TimeoutException("UI queue did not complete");}Pump();
Check(main.GetVisualDescendants().OfType<TextBlock>().All(t=>t.Text?.Contains("预计剩余")!=true),"Completed task rows remove their countdowns through the same UI binding");main.Close();Pump();
File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{checks=checks.Count,results=checks,outputs,measurements,macOSRuntimeVerified=false},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Verified {checks.Count} progress checks / {outputs.Count} actual outputs. {root}");

sealed class ManualClock:TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>Interlocked.Read(ref _ticks);
    public void Advance(double seconds)=>Interlocked.Add(ref _ticks,TimeSpan.FromSeconds(seconds).Ticks);
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)=>new NoTimer();
    private sealed class NoTimer:ITimer {public bool Change(TimeSpan dueTime,TimeSpan period)=>true;public void Dispose(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
}
class ControlledExecutor:IJobExecutor
{
    private readonly ConcurrentDictionary<Guid,Action<double>> _callbacks=new();
    private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Execute(Job job,Action<double> progress,CancellationToken ct){_callbacks[job.Id]=progress;await _release.Task.WaitAsync(ct);}
    public void Report(Job job,double value)=>_callbacks[job.Id](value);
    public void Complete()=>_release.TrySetResult();
}
sealed class FailingExecutor:IJobExecutor {public Task Execute(Job job,Action<double> progress,CancellationToken ct)=>throw new IOException("codec failure");}
sealed class RealtimeMediaExecutor(MediaEngine engine):IJobExecutor
{
    public async Task Execute(Job job,Action<double> progress,CancellationToken ct)
    {
        var info=await engine.Probe(job.Inputs[0],ct);job.Duration=MediaEngine.ValidateEdits(job,[info]);var arguments=MediaEngine.BuildArguments(job,[info]);arguments.Insert(arguments.IndexOf("-i"),"-re");
        var result=await ProcessRunner.Run(engine.FFmpeg,arguments,ct,line=>{if(line.StartsWith("out_time_us=")&&long.TryParse(line[12..],out var time))progress(Math.Clamp(time/1000000d/job.Duration*100,0,99.9));});
        if(result.ExitCode!=0)throw new IOException(result.Error);progress(100);
    }
}
sealed class UiExecutor(MediaEngine engine):ControlledExecutor,IMediaEngine
{
    public AppSettings Settings=>engine.Settings;public string FFmpeg=>engine.FFmpeg;public string FFprobe=>engine.FFprobe;
    public Task<MediaInfo> Probe(string path,CancellationToken ct=default,int videoStreamIndex=0,int audioStreamIndex=0)=>engine.Probe(path,ct,videoStreamIndex,audioStreamIndex);
    public Task<byte[]> Thumbnail(string input,double seconds,int width=640,int height=360,CancellationToken ct=default,bool pad=true,int videoStreamIndex=0,bool endExclusive=false)=>engine.Thumbnail(input,seconds,width,height,ct,pad,videoStreamIndex,endExclusive);
    public Task<double> AdjacentFrameTime(string input,double seconds,int direction,CancellationToken ct=default,int videoStreamIndex=0)=>engine.AdjacentFrameTime(input,seconds,direction,ct,videoStreamIndex);
}
