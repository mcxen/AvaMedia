using System.Net;
using System.Net.Sockets;
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

var root=Path.GetFullPath("artifacts/download-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(root);
var checks=0;var outputs=new List<string>();var engine=new MediaEngine(new());
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
void Reject(Action action,string message){try{action();}catch(ArgumentException){checks++;return;}throw new Exception(message);}
var shares="分享 https://youtu.be/abc?list=xyz 。\n哔哩哔哩 https://b23.tv/xyz\n抖音 https://v.douyin.com/abc/\n小红书 https://www.xiaohongshu.com/explore/123?xsec_token=a%2Bb&xsec_source=pc_share\nhttps://youtu.be/abc?list=xyz";
var urls=DownloadLinks.Extract(shares);
Check(urls.Count==4,"Share messages deduplicate links");
Check(urls.Select(DownloadLinks.Platform).SequenceEqual(new[]{"YouTube","哔哩哔哩","抖音","小红书"}),"Correct host-based platform recognition");
Check(urls[3].EndsWith("xsec_token=a%2Bb&xsec_source=pc_share"),"Signed XHS query retained");
Check(DownloadLinks.Extract("www.youtube.com/watch?v=abc").Single().StartsWith("https://"),"Bare www link supported");
Check(DownloadLinks.Platform("https://youtube.com.evil.test/video")=="其他网站","Host suffix spoof rejected");
Check(!DownloadLinks.Normalize("https://youtu.be/abc#fragment").Contains('#'),"Fragments removed");
Reject(()=>DownloadLinks.Normalize("file:///C:/private"),"Local URL rejected");Reject(()=>DownloadLinks.Normalize("https://user:secret@example.com/video"),"URL credentials rejected");
Reject(()=>DownloadLinks.Extract(string.Join('\n',Enumerable.Range(0,101).Select(i=>"https://youtu.be/"+i))),"Batch capped");
Reject(()=>new DownloadOptions{Proxy="file:///x"}.Validate(),"Invalid proxy rejected");Reject(()=>new DownloadOptions{CookieBrowser="unknown"}.Validate(),"Unknown browser rejected");
Reject(()=>new DownloadOptions{CookieBrowser="edge",CookieFile="x"}.Validate(),"Conflicting cookie choices rejected");Reject(()=>new DownloadOptions{MaxHeight=-1}.Validate(),"Invalid height rejected");
var settings=new AppSettings();var service=new YtDlpDownloadService(settings);
Check(!service.DownloadArguments(new(),"mp4","media.%(ext)s").Contains("--cookies-from-browser"),"Public videos default to anonymous download without reading a browser");
var mp4=service.DownloadArguments(new(){MaxHeight=720,Proxy="socks5://user:secret@127.0.0.1:7890",CookieBrowser="firefox",Subtitles=true,AutoSubtitles=true},"mp4","media.%(ext)s");
Check(mp4.Contains("--continue")&&mp4.Contains("--no-playlist")&&mp4.Contains("--no-overwrites"),"Downloads resume without expanding or overwriting");
Check(mp4[Array.IndexOf(mp4.ToArray(),"--format")+1].Contains("[height<=?720]"),"Quality ceiling passed to yt-dlp");
Check(mp4.Contains("--write-subs")&&mp4.Contains("--write-auto-subs")&&mp4.Contains("--convert-subs"),"Manual and automatic subtitles requested");
Check(mp4.Contains("firefox")&&mp4.Contains("socks5://user:secret@127.0.0.1:7890"),"Network and selected login parameters passed as discrete arguments");
var audio=service.DownloadArguments(new(),"mp3","media.%(ext)s");Check(audio.Contains("--extract-audio")&&audio.Contains("ba/b")&&!audio.Contains("--remux-video"),"Audio-only extraction selection");
Check(service.DownloadArguments(new(){MaxHeight=0},"mkv","media.%(ext)s").All(a=>!a.Contains("height<=")),"Best quality has no ceiling");
var redacted=DownloadDiagnostics.Redact("Cookie: private\nAuthorization=secret\nhttps://u:p@host/video?xsec_token=secret socks5://user:password@host:7890");
Check(!redacted.Contains("secret")&&!redacted.Contains("password")&&!redacted.Contains("private")&&!redacted.Contains("u:p"),"Credentials and signed query redacted");
Check(DownloadDiagnostics.Explain("DPAPI decrypt failed",urls[0]).Contains("Firefox"),"Cookie failure offers actionable fallback");
var update=DownloadDiagnostics.Progress("AVAMEDIA_PROGRESS: 72.3%|2.1MiB/s|00:12");Check(update is{Percent:72.3}&&update.Value.Detail.Contains("剩余 00:12"),"Machine progress includes speed and remaining time");
Check(DownloadDiagnostics.Progress("AVAMEDIA_PROGRESS:100%|NA|NA") is{Percent:99},"Downloader progress cannot finish queue before postprocessing");
var video=new DownloadVideo(urls[0],"abc","测试视频 🎬","作者",125,"YouTube");
var single=YtDlpDownloadService.ParseInspection("{\"id\":\"id\",\"title\":\"标题\",\"duration\":null,\"url\":\"id\"}",urls[3]);
Check(single.Videos.Single() is{Duration:0,Title:"标题"}&&single.Videos[0].Url==urls[3],"Missing duration and signed source are preserved");
var playlist=YtDlpDownloadService.ParseInspection("{\"playlist_count\":150,\"entries\":[{\"id\":\"abc\",\"title\":\"one\",\"url\":\"abc\"},null,{\"id\":\"def\",\"title\":\"two\",\"url\":\"def\"}]}",urls[0]);
Check(playlist.Truncated&&playlist.Videos.Count==2&&playlist.Videos[1].Url.EndsWith("watch?v=def"),"Flat playlist becomes individual URLs with truncation notice");
var folder=Path.Combine(root,"batch");var reserved=Path.Combine(folder,"测试视频 🎬.mp4");
var batch=DownloadBatch.CreateJobs(new([video,video],folder,"mp4",new(){ExpandPlaylist=true}),[reserved]);
Check(batch[0].Output.EndsWith(" (1).mp4")&&batch[1].Output.EndsWith(" (2).mp4"),"Reserved and repeated titles use unique outputs");
Check(batch.All(j=>j.FeatureId=="download"&&j.State==JobState.Waiting&&j.Name==video.Title&&j.Options.Download is{ExpandPlaylist:false}),"Per-video queue jobs contain title and download snapshot");
Check(!ReferenceEquals(batch[0].Options,batch[1].Options),"Conversion drafts independent");
var devices=DownloadBatch.CreateJobs(new([video with{Title="CON"},video with{Title="a:/b?"},video with{Title=string.Concat(Enumerable.Repeat("🎬",80))}],folder,"m4a",new()));
Check(Path.GetFileName(devices[0].Output)=="_CON.m4a"&&!Path.GetFileName(devices[1].Output).Contains('?')&&Path.GetFileNameWithoutExtension(devices[2].Output).Length==140,"Portable filenames protect device names and Unicode boundaries");
Reject(()=>DownloadBatch.CreateJobs(new([video with{IsLive=true}],folder,"mp4",new())),"Live streams blocked");
var storage=new Storage(Path.Combine(root,"persistence"));storage.SaveJobs(batch);Check(storage.LoadJobs().All(j=>j.DownloadTitle==video.Title&&j.Options.Download is{MaxHeight:1080}),"Queue persists download options and titles");
var cookie=Path.Combine(root,"cookies.txt");File.WriteAllText(cookie,"# Netscape HTTP Cookie File\n.example.test\tTRUE\t/\tFALSE\t0\tname\tvalue\n");var cookieHash=SHA256.HashData(File.ReadAllBytes(cookie));string? lease=null;
var inspectedArgs=new List<string>();
var fake=new YtDlpDownloadService(settings,(_,args,_,_)=>{inspectedArgs=args.ToList();lease=args[Array.IndexOf(args.ToArray(),"--cookies")+1];Check(lease!=cookie&&File.Exists(lease),"Cookies read through isolated temporary file");File.AppendAllText(lease,"modified by downloader");return Task.FromResult(new ProcessResult(0,"{\"id\":\"abc\",\"title\":\"test\",\"webpage_url\":\"https://youtu.be/abc\"}",""));});
var probe=fake.InspectAsync(urls[0],new(){CookieFile=cookie,Proxy="http://127.0.0.1:7890"}).GetAwaiter().GetResult();
Check(!File.Exists(lease)&&SHA256.HashData(File.ReadAllBytes(cookie)).SequenceEqual(cookieHash),"Cookie lease cleaned and original file unchanged");
Check(inspectedArgs.Contains("--no-playlist")&&inspectedArgs.Contains("http://127.0.0.1:7890"),"Metadata inspection uses same login and network options");
var fail=new YtDlpDownloadService(settings,(_,args,_,_)=>{lease=args[Array.IndexOf(args.ToArray(),"--cookies")+1];return Task.FromResult(new ProcessResult(1,"","login required"));});
try{fail.InspectAsync(urls[0],new(){CookieFile=cookie}).GetAwaiter().GetResult();throw new Exception("Failed inspection accepted");}catch(InvalidOperationException){Check(!File.Exists(lease),"Cookie lease cleaned on failure");}
var retryJob=DownloadBatch.CreateJobs(new([video],folder,"mp4",new(){CookieFile=cookie})).Single();var stage=Path.Combine(folder,".avamedia-download-"+retryJob.Id.ToString("N"));
using(var cancel=new CancellationTokenSource())
{
    var stopped=new YtDlpDownloadService(settings,(_,args,ct,_)=>{File.WriteAllText(Path.Combine(stage,"media.mp4.part"),"partial");cancel.Cancel();return Task.FromCanceled<ProcessResult>(ct);});
    try{stopped.ExecuteAsync(retryJob,_=>{},cancel.Token).GetAwaiter().GetResult();throw new Exception("Cancellation failed");}catch(OperationCanceledException){Check(File.Exists(Path.Combine(stage,"media.mp4.part"))&&!File.Exists(retryJob.Output),"Stopping preserves partial download without a final file");}
}
var retry=new YtDlpDownloadService(settings,(_,args,_,callback)=>{Check(args.Contains("--continue")&&File.Exists(Path.Combine(stage,"media.mp4.part")),"Retry uses stable staging and continuation");File.WriteAllText(Path.Combine(stage,"media.mp4"),"complete");File.WriteAllText(Path.Combine(stage,"media.zh.srt"),"字幕");callback?.Invoke("AVAMEDIA_PROGRESS:52%|1MiB/s|00:01");return Task.FromResult(new ProcessResult(0,"https://host/path?token=secret",""));});
retry.ExecuteAsync(retryJob,p=>retryJob.Progress=p,CancellationToken.None).GetAwaiter().GetResult();
Check(File.Exists(retryJob.Output)&&File.Exists(Path.ChangeExtension(retryJob.Output,"zh.srt"))&&!Directory.Exists(stage)&&retryJob.Progress==100,"Retry finishes media and subtitles then cleans staging");
Check(!retryJob.Log.Contains("secret"),"Completed task log excludes signed query");

// Exercise the official bundled downloader against a local HTTP media server, including FFmpeg postprocessing.
var fixture=Path.Combine(root,"fixture.mp4");
var ff=ProcessRunner.Run(engine.FFmpeg,["-v","error","-n","-f","lavfi","-i","testsrc2=size=320x180:rate=25","-f","lavfi","-i","sine=frequency=440:sample_rate=44100","-t","2","-c:v","mpeg4","-q:v","3","-c:a","aac",fixture]).GetAwaiter().GetResult();Check(ff.ExitCode==0,"Fixture generated: "+ff.Error);
using(var server=new VideoServer(File.ReadAllBytes(fixture)))
{
    var real=service.InspectAsync(server.Url,new()).GetAwaiter().GetResult();Check(real.Videos.Count==1,"Real yt-dlp metadata parsing succeeds");
    foreach(var format in new[]{"mp4","mkv","mp3","m4a"})
    {
        var job=DownloadBatch.CreateJobs(new(real.Videos,Path.Combine(root,"actual"),format,new())).Single();
        engine.Execute(job,p=>job.Progress=p,CancellationToken.None).GetAwaiter().GetResult();outputs.Add(job.Output);
        var result=engine.Probe(job.Output).GetAwaiter().GetResult();Check(result.Duration>1.8&&result.Duration<2.3&&result.HasAudio,"Actual "+format+" output contains complete audio");
        Check(format is "mp3" or "m4a" ? !result.HasVideo : result.Width==320&&result.Height==180,"Actual "+format+" output has expected streams");
        Check(job.Progress==100,"Actual "+format+" progresses only after postprocessing");
    }
    Check(server.Requests>=5,"Downloader performed actual HTTP requests");
}

AppBuilder.Configure<App>().UseHeadless(new(){UseHeadlessDrawing=false}).UseSkia().SetupWithoutStarting();Motion.SetReducedMotion(true);
var uiService=new SampleService();var window=new DownloadWindow(settings,folder,service:uiService);window.Show();
Check(window.ReadOptions() is{CookieBrowser:"",CookieFile:""},"GUI defaults to no login state");
window.FindControl<TextBox>("LinksInput")!.Text=shares;Click(window,"InspectButton");Pump(window.Ready);
Check(window.Entries.Count==4&&window.Entries.Count(e=>e.IsReady)==3&&window.Entries.Count(e=>e.HasError)==1,"GUI displays successful and failed platforms independently");
Check(window.ReadRequest().Videos.Count==3&&window.FindControl<Button>("AddDownloadsButton")!.IsEnabled,"Successful videos default selected");
var selectAll=window.FindControl<CheckBox>("SelectAllCheck")!;selectAll.IsChecked=false;Dispatcher.UIThread.RunJobs();Check(window.Entries.All(e=>!e.IsChecked),"Deselect all works without event feedback");
selectAll.IsChecked=true;Dispatcher.UIThread.RunJobs();Check(window.Entries.Count(e=>e.IsChecked)==3,"Select all excludes failed items");
window.Entries[0].IsChecked=false;Check(window.ReadRequest().Videos.Count==2,"Per-item selection honored");
window.FindControl<ComboBox>("DownloadFormat")!.SelectedIndex=2;Dispatcher.UIThread.RunJobs();Check(!window.FindControl<ComboBox>("DownloadQuality")!.IsEnabled&&window.ReadRequest().Format=="mp3","Audio selection disables irrelevant picture quality");
window.FindControl<ComboBox>("DownloadFormat")!.SelectedIndex=0;window.FindControl<ComboBox>("CookieSource")!.SelectedIndex=6;Dispatcher.UIThread.RunJobs();Check(window.FindControl<StackPanel>("CookieFilePanel")!.IsVisible,"Cookie file controls shown only when selected");
window.FindControl<TextBox>("CookieFileInput")!.Text=cookie;window.FindControl<ComboBox>("DownloadSubtitles")!.SelectedIndex=2;
Check(window.ReadOptions() is{CookieFile:var f,Subtitles:true,AutoSubtitles:true}&&f==cookie,"Advanced choices read correctly");window.FindControl<ComboBox>("CookieSource")!.SelectedIndex=0;
window.FindControl<ListBox>("DownloadList")!.SelectedIndex=3;Dispatcher.UIThread.RunJobs();Check(window.FindControl<TextBlock>("PlatformHelp")!.Text!.Contains("xsec_token"),"Platform-specific signed-link guidance");
Capture(window,"download-light.png",1120,860);Application.Current!.RequestedThemeVariant=ThemeVariant.Dark;Capture(window,"download-dark.png",1120,860);
Skin.Apply("MacOS9");Capture(window,"download-macos9.png",1000,720);AssertVisible(window,"AddDownloadsButton");AssertVisible(window,"LinksInput");AssertVisible(window,"DownloadFolder");
Skin.Apply("Light");uiService.AllowDouyin=true;Click(window,"RetryFailedButton");Pump(window.Ready);Check(window.Entries.Count==4&&window.Entries.All(e=>e.IsReady)&&!window.Entries.Single(e=>e.Video!.Platform=="YouTube").IsChecked,"Retry failures retains successful rows and their selections");
Click(window,"ClearLinksButton");Check(window.Entries.Count==0&&!window.FindControl<Button>("AddDownloadsButton")!.IsEnabled&&window.FindControl<StackPanel>("EmptyState")!.IsVisible,"Clear resets ready state");
Capture(window,"download-empty-min.png",1000,720);window.Close();
var blocker=new BlockingService();var canceled=new DownloadWindow(settings,folder,[urls[0],urls[1]],blocker);canceled.Show();var parsing=canceled.InspectAsync();PumpUntil(()=>blocker.Started);Click(canceled,"CancelInspectButton");Pump(parsing);
Check(canceled.Entries.Count(e=>e.IsReady)==1&&canceled.FindControl<Button>("InspectButton")!.IsEnabled,"Cancel restores controls and keeps completed videos");canceled.Close();
var closing=new DownloadWindow(settings,folder,[urls[1]],new BlockingService());closing.Show();parsing=closing.InspectAsync();closing.Close();Pump(parsing);Check(true,"Closing cancels pending inspection");
var queueStorage=new Storage(Path.Combine(root,"ui-queue"));queueStorage.SaveSettings(new(){OutputFolder=folder,AutoDetectGpu=false});var main=new MainWindow(queueStorage);main.Show();
var workflow=main.ConfigureDownloadAsync([urls[0],urls[1]],new SampleService());PumpUntil(()=>main.OwnedWindows.OfType<DownloadWindow>().Any());var dialog=main.OwnedWindows.OfType<DownloadWindow>().Single();Pump(dialog.InspectAsync());
Check(queueStorage.LoadJobs().Count==0,"Parsing creates no queue jobs before confirmation");Click(dialog,"AddDownloadsButton");Pump(workflow);
Check(queueStorage.LoadJobs().Count==2&&queueStorage.LoadJobs().All(j=>j.FeatureId=="download"&&j.State==JobState.Waiting),"Confirmation adds complete waiting batch");
workflow=main.ConfigureDownloadAsync([urls[0]],new SampleService());PumpUntil(()=>main.OwnedWindows.OfType<DownloadWindow>().Any());dialog=main.OwnedWindows.OfType<DownloadWindow>().Single();Pump(dialog.InspectAsync());Click(dialog,"DownloadCancelButton");Pump(workflow);
Check(queueStorage.LoadJobs().Count==2,"Cancel adds no jobs");main.Close();
File.WriteAllText(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{checks,outputs,realDownloader=true,credentialsRedacted=true},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS: {checks} download checks / {outputs.Count} actual outputs. {root}");

void Click(Window w,string name){w.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();}
void Pump(Task task){PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
void PumpUntil(Func<bool> condition){var deadline=DateTime.UtcNow.AddSeconds(60);while(!condition()){Dispatcher.UIThread.RunJobs();if(DateTime.UtcNow>deadline)throw new TimeoutException("UI operation timed out");Thread.Sleep(5);}Dispatcher.UIThread.RunJobs();}
void Capture(Window target,string name,int width,int height){target.Width=width;target.Height=height;target.Measure(new Size(width,height));target.Arrange(new Rect(0,0,width,height));Dispatcher.UIThread.RunJobs();using var bitmap=new RenderTargetBitmap(new PixelSize(width,height),new Vector(96,96));bitmap.Render(target);bitmap.Save(Path.Combine(root,name));}
void AssertVisible(Window target,string name){var control=target.FindControl<Control>(name)!;var p=control.TranslatePoint(new Point(control.Bounds.Width,control.Bounds.Height),target);Check(control.Bounds.Width>0&&control.Bounds.Height>0&&p is{} point&&point.X<=target.Width&&point.Y<=target.Height,"Visible at minimum size: "+name);}

sealed class SampleService:IVideoDownloadService
{
    public bool AllowDouyin{get;set;}
    public Task<DownloadInspection> InspectAsync(string url,DownloadOptions options,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();var platform=DownloadLinks.Platform(url);
        if(platform=="抖音"&&!AllowDouyin)throw new InvalidOperationException("抖音 · 网站要求登录，请选择已登录的浏览器后重试。");
        return Task.FromResult(new DownloadInspection([new(url,"id","示例视频："+platform+" · 旅行记录与剪辑素材","示例作者",125,platform)]));
    }
}
sealed class BlockingService:IVideoDownloadService
{
    public bool Started{get;private set;}
    public async Task<DownloadInspection> InspectAsync(string url,DownloadOptions options,CancellationToken ct=default)
    {if(DownloadLinks.Platform(url)=="YouTube")return new([new(url,"id","completed","author",1,"YouTube")]);Started=true;await Task.Delay(Timeout.Infinite,ct);throw new Exception("unreachable");}
}
sealed class VideoServer:IDisposable
{
    private readonly HttpListener _listener=new();private readonly Task _worker;public string Url{get;}public int Requests{get;private set;}
    public VideoServer(byte[] data)
    {
        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        Url=$"http://127.0.0.1:{port}/fixture.mp4";_listener.Prefixes.Add($"http://127.0.0.1:{port}/");_listener.Start();
        _worker=Task.Run(async()=>
        {
            while(_listener.IsListening)
            {
                HttpListenerContext context;try{context=await _listener.GetContextAsync();}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}
                Requests++;var response=context.Response;response.ContentType="video/mp4";response.Headers["Accept-Ranges"]="bytes";
                var start=0;var end=data.Length-1;var range=context.Request.Headers["Range"];
                if(range is not null&&range.StartsWith("bytes=")){var values=range[6..].Split('-');if(int.TryParse(values[0],out var offset))start=offset;if(values.Length>1&&int.TryParse(values[1],out var limit))end=Math.Min(limit,end);response.StatusCode=206;response.Headers["Content-Range"]=$"bytes {start}-{end}/{data.Length}";}
                response.ContentLength64=end-start+1;try{if(context.Request.HttpMethod!="HEAD")await response.OutputStream.WriteAsync(data.AsMemory(start,end-start+1));}catch(IOException){}finally{response.Close();}
            }
        });
    }
    public void Dispose(){_listener.Close();_worker.GetAwaiter().GetResult();}
}
