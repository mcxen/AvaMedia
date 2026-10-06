using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using AvaMedia.Core;
using PdfSharp.Pdf.IO;

var root=Path.GetFullPath(Path.Combine("artifacts","verification-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);var engine=new MediaEngine(new());int checks=0;var results=new List<object>();
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
async Task Generate(string path,string size="320x180",bool audio=true,double duration=3)
{
    var args=new List<string>{"-v","error","-n","-f","lavfi","-i","testsrc2=size="+size+":rate=25"};
    if(audio)args.AddRange(["-f","lavfi","-i","sine=frequency=440:sample_rate=44100"]);
    args.AddRange(["-t",MediaEngine.Number(duration),"-c:v","mpeg4","-q:v","3"]);if(audio)args.AddRange(["-c:a","aac"]);args.Add(path);
    var r=await ProcessRunner.Run(engine.FFmpeg,args);Check(r.ExitCode==0,r.Error);
}
var input=Path.Combine(root,"样例 O'Brien space.mp4");var other=Path.Combine(root,"no-audio.mp4");await Generate(input);await Generate(other,"240x320",false,2);
var source=await engine.Probe(input);Check(source.Width==320 && source.Height==180 && source.HasAudio,"源媒体探测失败");
var thumb=await engine.Thumbnail(input,1);Check(thumb.Length>1000 && thumb[0]==137,"预览 PNG 无效");
async Task<Job> Run(string id,string[]? inputs=null,ConversionOptions? options=null,bool folder=false)
{
    var f=Catalog.Find(id);var o=options??new(){Format=f.Format};var job=new Job{FeatureId=id,Inputs=inputs??[input],Options=o,Output=MediaEngine.UniqueOutput(root,id,o.Format,directory:folder)};
    await engine.Execute(job,_=>{},CancellationToken.None);Check(folder?Directory.Exists(job.Output):new FileInfo(job.Output).Length>0,"输出缺失: "+id);results.Add(new {id,output=job.Output});Console.WriteLine("PASS "+id);return job;
}
foreach(var id in new[]{"mp4","crop","audio-clip","audio-mp3","audio-flac","audio-wav","audio-m4a","audio-ogg","audio-opus","audio-ac3","audio-aac","audio-wma","audio-aiff","image-jpg","image-png","image-webp","image-bmp","image-tiff","image-ico","image-avif","image-gif","image-tools"})await Run(id);
foreach(var format in new[]{"mkv","webm","gif","avi","flv","mov","wmv","mpg","ts"})await Run("mp4",options:new(){Format=format});
await Run("video-compress",options:VideoCompression.CreateOptions(new(){Mode=VideoCompressionMode.Percentage,Percentage=95,KeepAudio=false,MaxDimension=0}));
var clipped=await Run("clip",options:new(){Start=.4,End=2.4,CropX=20,CropY=20,CropWidth=200,CropHeight=100,Width=160,Speed=2,FadeIn=.1,FadeOut=.1});var clipInfo=await engine.Probe(clipped.Output);Check(clipInfo.Width==160 && clipInfo.Height==80 && clipInfo.Duration is >.85 and <1.2,"剪辑时长 / 裁剪尺寸错误");
var rotated=await Run("rotate",options:new(){CropX=20,CropY=20,CropWidth=200,CropHeight=100,Rotation=90});var rotatedInfo=await engine.Probe(rotated.Output);Check(rotatedInfo.Width==100&&rotatedInfo.Height==200&&rotatedInfo.HasAudio,"裁剪后旋转的尺寸或音轨错误");
await Run("delogo",options:new(){DelogoX=20,DelogoY=20,DelogoWidth=60,DelogoHeight=40});
var joined=await Run("join",[input,other]);var joinInfo=await engine.Probe(joined.Output);Check(joinInfo.Duration is >4.8 and <5.3 && joinInfo.HasAudio,"不同尺寸 / 缺少音轨合并错误");
var joinedFiltered=await Run("join",[input,other],new(){Width=160,Height=90,Speed=2,FadeIn=.1,Volume=.5});var filteredInfo=await engine.Probe(joinedFiltered.Output);Check(filteredInfo.Width==160 && filteredInfo.Height==90 && filteredInfo.Duration is >2.3 and <2.8,"合并后滤镜无法正确应用");
var audio=await Run("split");Check((await engine.Probe(audio.Output)).HasVideo==false,"音轨提取仍有视频");
await Run("audio-join",[audio.Output,audio.Output]);await Run("audio-mix",[audio.Output,audio.Output]);
await Run("dvd",[input,other]);
var mux=await Run("mux",[other,audio.Output]);Check((await engine.Probe(mux.Output)).HasAudio,"混流缺少音轨");
await Run("extract-video");var frames=await Run("frames",folder:true);Check(Directory.GetFiles(frames.Output,"*.png").Length==3,"导出帧数不符");
await Run("repair",options:new(){Format="mkv",CopyStreams=true});await Run("info");await Run("hash",[input,other]);
var text=Path.Combine(root,"text.txt");await File.WriteAllTextAsync(text,"AvaMedia license-safe document conversion\nHello media tools\n中文测试");
var pdf=await Run("text-pdf",[text]);var merged=await Run("pdf-merge",[pdf.Output,pdf.Output]);using(var doc=PdfReader.Open(merged.Output,PdfDocumentOpenMode.Import))Check(doc.PageCount==2,"PDF 合并页数错误");
var splitPdf=await Run("pdf-split",[merged.Output],folder:true);Check(Directory.GetFiles(splitPdf.Output).Length==2,"PDF 拆分页数错误");
var pdfText=await Run("pdf-text",[pdf.Output]);Check((await File.ReadAllTextAsync(pdfText.Output)).Contains("AvaMedia"),"PDF 文本提取失败");
foreach(var id in new[]{"pdf-docx","pdf-xlsx"}){var office=await Run(id,[pdf.Output]);using var zip=ZipFile.OpenRead(office.Output);foreach(var e in zip.Entries.Where(e=>e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels"))){using var stream=e.Open();XDocument.Load(stream);checks++;}}
var image=results.Select(o=>JsonSerializer.SerializeToElement(o)).First(o=>o.GetProperty("id").GetString()=="image-jpg").GetProperty("output").GetString()!;await Run("images-pdf",[image]);
var allImages=results.Select(o=>JsonSerializer.SerializeToElement(o)).Where(o=>o.GetProperty("id").GetString()!.StartsWith("image-")).Select(o=>o.GetProperty("output").GetString()!).ToArray();var imagePdf=await Run("images-pdf",allImages);using(var doc=PdfReader.Open(imagePdf.Output,PdfDocumentOpenMode.Import))Check(doc.PageCount==allImages.Length,"图片格式转 PDF 页数错误");
await Run("image-compress",[allImages.First(path=>Path.GetExtension(path)==".bmp")]);
var iso=await Run("iso",[text]);Check(File.ReadAllBytes(iso.Output).SequenceEqual(File.ReadAllBytes(text)),"ISO 原始数据复制错误");
var zipped=await Run("zip",[input,text]);var unzipped=await Run("unzip",[zipped.Output],folder:true);Check(Directory.GetFiles(unzipped.Output).Length==2,"ZIP 提取失败");
var hostile=Path.Combine(root,"hostile.zip");using(var zip=ZipFile.Open(hostile,ZipArchiveMode.Create)){using var w=new StreamWriter(zip.CreateEntry("../escape.txt").Open());w.Write("unsafe");}
bool blocked=false;try{await Run("unzip",[hostile],folder:true);}catch(InvalidDataException){blocked=true;}Check(blocked,"ZIP 路径越界未被拒绝");
bool validation=false;try{MediaEngine.Validate(new(){Inputs=[input],Output=input});}catch(ArgumentException){validation=true;}Check(validation,"源文件覆盖未被拒绝");
var storage=new Storage(Path.Combine(root,"state"));var resumable=new Job{Inputs=[input],State=JobState.Running};storage.SaveJobs([resumable]);Check(storage.LoadJobs()[0].State==JobState.Cancelled,"中断队列没有恢复为可重试状态");
var running=new Job{Inputs=[input],Output=Path.Combine(root,"queue.mp4")};var failing=new Job{Inputs=["missing.mp4"],Output=Path.Combine(root,"missing-out.mp4")};var queue=new QueueService(engine);await queue.Run([running,failing],2);Check(running.State==JobState.Completed && failing.State==JobState.Failed,"队列不能隔离失败任务");
using(var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0))
{
    listener.Start();var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;using var serverStop=new CancellationTokenSource();var bytes=await File.ReadAllBytesAsync(input);
    var server=Task.Run(async()=>
    {
        try
        {
            while(!serverStop.IsCancellationRequested)
            {
                using var client=await listener.AcceptTcpClientAsync(serverStop.Token);await using var stream=client.GetStream();var request=new byte[8192];await stream.ReadAsync(request,serverStop.Token);
                var head=System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(head,serverStop.Token);await stream.WriteAsync(bytes,serverStop.Token);
            }
        }catch(OperationCanceledException){}catch(System.IO.IOException){}
    });
    try{var download=await Run("download",[$"http://127.0.0.1:{port}/sample.mp4"]);Check((await engine.Probe(download.Output)).Duration>2.9,"下载结果损坏");}finally{serverStop.Cancel();listener.Stop();await server;}
}
var covered=results.Select(o=>JsonSerializer.SerializeToElement(o).GetProperty("id").GetString()!).Distinct().Order().ToArray();Check(Catalog.All.Where(f=>f.Operation!=Operation.Player).All(f=>covered.Contains(f.Id)),"存在未经实际输出验证的转换入口。");
var report=new{checks,operations=results.Count,coveredFeatures=covered,root,results};await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine($"Verified {checks} assertions / {results.Count} outputs / {covered.Length} conversion entries. {root}");
