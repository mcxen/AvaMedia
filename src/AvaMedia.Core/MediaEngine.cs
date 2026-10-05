using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;
public sealed class MediaEngine
{
    public AppSettings Settings { get; }
    public MediaEngine(AppSettings settings) => Settings=settings;
    public string FFmpeg => Resolve(Settings.FFmpegPath,"ffmpeg");
    public string FFprobe => Resolve(Settings.FFprobePath,"ffprobe");
    public static string Number(double n) => n.ToString("0.######",CultureInfo.InvariantCulture);
    public static string Resolve(string configured,string tool)
    {
        if (!string.IsNullOrWhiteSpace(configured)){if(File.Exists(configured))return Path.GetFullPath(configured);throw new FileNotFoundException($"配置的 {tool} 路径不存在。",configured);}
        var env=Environment.GetEnvironmentVariable("AVAMEDIA_"+tool.ToUpperInvariant());
        if(!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var name=tool+(OperatingSystem.IsWindows()?".exe":"");
        if(OperatingSystem.IsMacOS())foreach(var folder in new[]{Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Resources","tools")),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Application Support","AvaMedia","tools"),"/opt/homebrew/bin","/usr/local/bin"}){var candidate=Path.Combine(folder,name);if(File.Exists(candidate))return candidate;}
        foreach(var root in new[]{AppContext.BaseDirectory,Environment.CurrentDirectory})
        {
            var dir=new DirectoryInfo(root);
            for(var i=0;dir is not null && i<7;i++,dir=dir.Parent)
            {
                foreach(var path in new[]{Path.Combine(dir.FullName,"tools",name),Path.Combine(dir.FullName,".tools",name)}) if(File.Exists(path)) return path;
                var tools=Path.Combine(dir.FullName,".tools",tool is "ffmpeg" or "ffprobe"?"ffmpeg":tool);
                if(Directory.Exists(tools)) {var found=Directory.EnumerateFiles(tools,name,SearchOption.AllDirectories).FirstOrDefault();if(found is not null) return found;}
            }
        }
        foreach(var root in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)) {var file=Path.Combine(root,name);if(File.Exists(file)) return file;}
        throw new FileNotFoundException($"未找到 {tool}。请在“选项 → 外部工具”中指定路径。",name);
    }
    public async Task<MediaInfo> Probe(string path,CancellationToken ct=default,int videoStreamIndex=0,int audioStreamIndex=0)
    {
        var r=await ProcessRunner.Run(FFprobe,["-v","error","-show_format","-show_streams","-of","json",path],ct);
        if(r.ExitCode!=0) throw new InvalidDataException(r.Error);
        using var json=JsonDocument.Parse(r.Output);var root=json.RootElement;
        var streams=root.GetProperty("streams").EnumerateArray().ToArray();
        var videos=streams.Where(s=>s.GetProperty("codec_type").GetString()=="video").ToArray();
        var audios=streams.Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
        if(videoStreamIndex<0 || audioStreamIndex<0 || videoStreamIndex>0 && videoStreamIndex>=videos.Length || audioStreamIndex>0 && audioStreamIndex>=audios.Length)throw new ArgumentException("源文件不包含所选视频或音频轨。");
        var video=videos.ElementAtOrDefault(videoStreamIndex);
        var audio=audios.ElementAtOrDefault(audioStreamIndex);
        var duration=0d;
        if(root.TryGetProperty("format",out var fmt) && fmt.TryGetProperty("duration",out var d)) double.TryParse(d.GetString(),CultureInfo.InvariantCulture,out duration);
        int width=video.ValueKind==JsonValueKind.Undefined?0:video.GetProperty("width").GetInt32(),height=video.ValueKind==JsonValueKind.Undefined?0:video.GetProperty("height").GetInt32();
        if(video.ValueKind!=JsonValueKind.Undefined && video.TryGetProperty("side_data_list",out var sideData))
            foreach(var side in sideData.EnumerateArray())if(side.TryGetProperty("rotation",out var rotation) && Math.Abs(rotation.GetDouble())%180==90)(width,height)=(height,width);
        int rate=0,channels=0;if(audio.ValueKind!=JsonValueKind.Undefined){if(audio.TryGetProperty("sample_rate",out var sample))int.TryParse(sample.GetString(),out rate);if(audio.TryGetProperty("channels",out var ch))channels=ch.GetInt32();}
        return new(duration,width,height,audio.ValueKind!=JsonValueKind.Undefined,video.ValueKind!=JsonValueKind.Undefined,r.Output,Codec(video),Codec(audio),rate,channels,videoStreamIndex,audioStreamIndex);
        static string Codec(JsonElement e) => e.ValueKind==JsonValueKind.Undefined?"":e.GetProperty("codec_name").GetString()??"";
    }
    public async Task<byte[]> Thumbnail(string input,double seconds,int width=640,int height=360,CancellationToken ct=default,bool pad=true,int videoStreamIndex=0)
    {
        using var p=ProcessRunner.Start(FFmpeg,["-v","error","-ss",Number(Math.Max(0,seconds)),"-i",input,"-map",$"0:v:{videoStreamIndex}","-frames:v","1","-vf",$"scale={width}:{height}:force_original_aspect_ratio=decrease"+(pad?$",pad={width}:{height}:(ow-iw)/2:(oh-ih)/2":""),"-f","image2pipe","-c:v","png","pipe:1"]);
        using var reg=ct.Register(()=>{try{p.Kill(true);}catch(InvalidOperationException){}});
        var error=p.StandardError.ReadToEndAsync();using var output=new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(output,ct);await p.WaitForExitAsync(ct);
        if(p.ExitCode!=0) throw new InvalidDataException(await error);
        return output.ToArray();
    }
    public static bool IsAudio(string format) => new[]{"mp3","flac","wav","m4a","ogg","aac","ac3","wma","opus","aiff"}.Contains(format);
    public static bool IsImage(string format) => new[]{"jpg","png","webp","bmp","tiff","ico","avif"}.Contains(format);
    public static void Validate(Job job)
    {
        var feature=Catalog.Find(job.FeatureId);var o=job.Options;
        if(job.Inputs.Length==0 && feature.Operation!=Operation.Record) throw new ArgumentException("请添加文件。");
        if(feature.Operation is not (Operation.Record or Operation.Download or Operation.IsoCopy)) foreach(var path in job.Inputs) if(!File.Exists(path)) throw new FileNotFoundException("源文件不存在",path);
        if(string.IsNullOrWhiteSpace(job.Output)) throw new ArgumentException("输出路径不能为空。");
        if(feature.Operation is not (Operation.Download or Operation.Record or Operation.IsoCopy) && job.Inputs.Any(p=>string.Equals(Path.GetFullPath(p),Path.GetFullPath(job.Output),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))) throw new ArgumentException("输出不能覆盖源文件。");
        if(!double.IsFinite(o.Start) || !double.IsFinite(o.End) || !double.IsFinite(o.Speed) || !double.IsFinite(o.Volume) || !double.IsFinite(o.Fps) || !double.IsFinite(o.FadeIn) || !double.IsFinite(o.FadeOut) || !double.IsFinite(o.FrameInterval) || !double.IsFinite(o.RecordSeconds))throw new ArgumentException("参数必须是有限数值。");
        if(o.Start<0 || o.End<0 || (o.End>0 && o.End<=o.Start)) throw new ArgumentException("结束时间必须晚于开始时间。");
        if(o.Speed is <0.25 or >4) throw new ArgumentException("速度必须在 0.25 到 4 倍之间。");
        if(o.CropWidth<0 || o.CropHeight<0 || o.CropX<0 || o.CropY<0 || o.Width<0 || o.Height<0 || o.DelogoX<0 || o.DelogoY<0) throw new ArgumentException("尺寸及坐标不能小于零。");
        if((o.CropWidth>0)!=(o.CropHeight>0)) throw new ArgumentException("裁剪宽度和高度必须同时设置。");
        if((o.DelogoWidth>0)!=(o.DelogoHeight>0)) throw new ArgumentException("水印区域宽度和高度必须同时设置。");
        if(o.FadeIn<0 || o.FadeOut<0 || o.Volume<0 || o.AudioBitrate<16 || o.Fps<0 || o.FrameInterval<=0) throw new ArgumentException("参数超出允许范围。");
        if(feature.Operation==Operation.Record && (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) || o.RecordSeconds<=0)) throw new ArgumentException("录屏支持 Windows 和 macOS，且时长必须大于零。");
        if(feature.Operation==Operation.Download && (!Uri.TryCreate(job.Inputs[0],UriKind.Absolute,out var uri) || uri.Scheme is not ("https" or "http"))) throw new ArgumentException("请输入有效的 HTTP / HTTPS 链接。");
        if(feature.Operation==Operation.Mux && job.Inputs.Length!=2) throw new ArgumentException("混流需要一个视频文件和一个音频文件。");
        if(feature.Operation==Operation.AudioMix && job.Inputs.Length<2) throw new ArgumentException("混音需要至少两个文件。");
        ValidateEncodingOptions(o);
        if(SubtitleOptions.Mode(o) is SubtitleMode.Preserve or SubtitleMode.ExternalTrack && feature.Operation is Operation.Join or Operation.AudioMix or Operation.Record)throw new ArgumentException("合并、混音和录制暂不支持输出独立字幕轨；视频合并可在每个输入中烧录字幕。");
        if(feature.Operation==Operation.Join && job.Inputs.Length>1 && SubtitleOptions.Mode(o)==SubtitleMode.BurnIn && string.IsNullOrWhiteSpace(o.Subtitle))throw new ArgumentException("源字幕需在每个合并输入的选项中分别选择烧录。");
        if(o.CopyStreams && (feature.Operation==Operation.AudioMix || feature.Operation==Operation.Join && (job.Inputs.Length>1 || job.InputOptions is not null)))throw new ArgumentException("合并编辑和混音需要重新编码。");
        if((o.VideoCodec=="copy" || o.AudioCodec=="copy") && (feature.Operation==Operation.AudioMix || feature.Operation==Operation.Join && (job.Inputs.Length>1 || job.InputOptions is not null)))throw new ArgumentException("合并编辑和混音的输出编码器需要重新编码，不能选择 copy。");
        if(feature.Operation==Operation.Record && (o.CopyStreams || o.VideoCodec=="copy"))throw new ArgumentException("屏幕采集需要视频编码器，不能直接复制原始画面流。");
        if(o.KeepAllAudioStreams && feature.Operation is Operation.Join or Operation.AudioMix or Operation.SplitVideo)throw new ArgumentException("当前合并、混音和视频流提取不支持保留独立的所有音频流。");
    }
    public static bool HasVideoFilters(ConversionOptions o) => o.CropWidth>0 || o.DelogoWidth>0 || o.Speed!=1 || o.Width>0 || o.Height>0 || o.Fps>0 || o.Rotation!=0 || o.Flip || o.FadeIn>0 || o.FadeOut>0 || SubtitleOptions.Mode(o)==SubtitleMode.BurnIn;
    public static bool HasAudioFilters(ConversionOptions o) => o.Speed!=1 || o.Volume!=1 || (o.AudioFadeIn??o.FadeIn)>0 || (o.AudioFadeOut??o.FadeOut)>0 || o.Echo || o.NoiseReduction || o.ReverseAudio;
    public static bool HasFilters(ConversionOptions o) => HasVideoFilters(o) || HasAudioFilters(o);
    public static void ValidateEncodingOptions(ConversionOptions o)
    {
        SubtitleOptions.Validate(o);
        foreach(var fade in new[]{o.AudioFadeIn,o.AudioFadeOut})if(fade is {} value && (!double.IsFinite(value) || value<0))throw new ArgumentException("音频淡入淡出时长必须为有限的非负数。");
        if(o.SampleRate<0 || o.SampleRate>192000 || o.SampleRate is >0 and <8000 || o.AudioChannels<0 || o.AudioChannels>8)throw new ArgumentException("采样率或声道超出允许范围。");
        if(o.CopyStreams && HasFilters(o))throw new ArgumentException("流复制不能同时使用画面或音频滤镜，请选择 MP4 / MKV 重新编码，或关闭流复制。");
        if(o.VideoCodec=="copy" && !o.CopyStreams && HasVideoFilters(o))throw new ArgumentException("视频 copy 不能使用画面滤镜，请选择视频编码器。");
        if((o.CopyStreams || o.AudioCodec=="copy") && !o.Mute && (HasAudioFilters(o) || o.SampleRate>0 || o.AudioChannels>0))throw new ArgumentException("音频 copy 不能使用音效、采样率或声道转换，请选择音频编码器。");
        if(o.KeepAllAudioStreams && o.Format is not ("mp4" or "mkv" or "mov" or "m4a" or "m4v" or "webm" or "avi" or "ts" or "mts" or "m2ts" or "asf" or "wmv" or "3gp" or "3g2" or "ogg" or "ogv"))throw new ArgumentException("此输出格式不能保留多个独立音轨，请选择 MP4、MKV 等多音轨容器。");
    }
    public async Task Execute(Job job,Action<double> progress,CancellationToken ct)
    {
        Validate(job);var f=Catalog.Find(job.FeatureId);
        if(f.Operation==Operation.Record && OperatingSystem.IsMacOS())
        {
            var devices=await ProcessRunner.Run(FFmpeg,["-hide_banner","-f","avfoundation","-list_devices","true","-i",""],ct);
            var screens=ScreenCapture.MacScreens(devices.Error);
            if(screens.Count==0)throw new InvalidOperationException(ScreenCapture.MacPermissionMessage("未找到 AVFoundation 屏幕设备。\n"+devices.Error));
            var selected=job.Options.RecordSource;
            if(selected=="desktop")selected=screens[0].Index+":none";
            if(!screens.Any(s=>selected==s.Index+":none"))throw new ArgumentException("所选屏幕设备不可用。请重新选择或使用自动屏幕。");
            var resolved=job.Options.Clone();resolved.RecordSource=selected;job.Options=resolved;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output))!);
        if(File.Exists(job.Output) || Directory.Exists(job.Output)) throw new IOException("输出已存在，请重试以生成新的名称。");
        if(f.Operation==Operation.ImagesPdf)
        {
            var temporary=new List<string>();var inputs=new List<string>();
            try
            {
                foreach(var path in job.Inputs)
                {
                    if(Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png"){inputs.Add(path);continue;}
                    var png=Path.Combine(Path.GetTempPath(),"AvaMedia-pdf-"+Guid.NewGuid()+".png");temporary.Add(png);
                    var converted=await ProcessRunner.Run(FFmpeg,["-v","error","-n","-i",path,"-frames:v","1","-c:v","png",png],ct);if(converted.ExitCode!=0)throw new InvalidDataException(converted.Error);inputs.Add(png);
                }
                var prepared=new Job{FeatureId=job.FeatureId,Inputs=inputs.ToArray(),Output=job.Output,Options=job.Options};await Task.Run(()=>DocumentEngine.Execute(prepared,progress,ct),ct);
            }
            finally{foreach(var path in temporary)if(File.Exists(path))File.Delete(path);}
            return;
        }
        if(f.Operation is Operation.PdfMerge or Operation.PdfSplit or Operation.PdfText or Operation.PdfDocx or Operation.PdfXlsx or Operation.TextPdf or Operation.Zip or Operation.Unzip) {await Task.Run(()=>DocumentEngine.Execute(job,progress,ct),ct);return;}
        if(f.Operation==Operation.Hash)
        {
            var lines=new List<string>();foreach(var path in job.Inputs) {ct.ThrowIfCancellationRequested();await using var stream=File.OpenRead(path);lines.Add($"{Convert.ToHexString(await SHA256.HashDataAsync(stream,ct))}  {Path.GetFileName(path)}");}
            await File.WriteAllLinesAsync(job.Output,lines,ct);progress(100);return;
        }
        if(f.Operation==Operation.IsoCopy)
        {
            var source=job.Inputs[0];if(source.Length==2 && source[1]==':') source="\\\\.\\"+source;
            await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite,1024*1024,true);
            await using var output=new FileStream(job.Output,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true);
            var buffer=new byte[1024*1024];int read;while((read=await input.ReadAsync(buffer,ct))>0) {await output.WriteAsync(buffer.AsMemory(0,read),ct);progress(0);}progress(100);return;
        }
        if(f.Operation==Operation.Info) {var info=await Probe(job.Inputs[0],ct);await File.WriteAllTextAsync(job.Output,info.RawJson,ct);progress(100);return;}
        if(f.Operation==Operation.Download)
        {
            var executable=Resolve(Settings.YtDlpPath,"yt-dlp");
            var r=await ProcessRunner.Run(executable,["--no-playlist","--no-overwrites","--newline","--no-config","--ffmpeg-location",Path.GetDirectoryName(FFmpeg)!,"--merge-output-format","mp4","--remux-video","mp4","-o",job.Output,"--",job.Inputs[0]],ct,line=>
            { var m=System.Text.RegularExpressions.Regex.Match(line,@"(\d+(?:\.\d+)?)%");if(m.Success)progress(double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture));});
            job.Log=r.Output+"\n"+r.Error;if(r.ExitCode!=0)throw new InvalidOperationException(r.Error);progress(100);return;
        }
        var infos=new List<MediaInfo>();
        if(f.Operation!=Operation.Record)for(int i=0;i<job.Inputs.Length;i++)
        {
            var edit=job.InputOptions?.ElementAtOrDefault(i)??job.Options;
            var vi=f.Operation==Operation.Mux && i==1?0:edit.VideoStreamIndex;
            var ai=f.Operation==Operation.Mux && i==0?0:edit.AudioStreamIndex;
            infos.Add(await Probe(job.Inputs[i],ct,vi,edit.KeepAllAudioStreams?0:ai));
            if(job.InputOptions is not null)SubtitleOptions.ValidateSource(edit,infos[^1]);
        }
        if(infos.Count>0)SubtitleOptions.ValidateSource(job.Options,infos[0]);
        if(SubtitleOptions.Mode(job.Options) is SubtitleMode.ExternalTrack or SubtitleMode.BurnIn && !string.IsNullOrWhiteSpace(job.Options.Subtitle))
        {
            var sub=await Probe(job.Options.Subtitle,ct);
            var selected=job.Options.Clone();selected.Subtitle="";selected.SubtitleMode=SubtitleOptions.Mode(job.Options)==SubtitleMode.ExternalTrack?SubtitleMode.Preserve:SubtitleMode.BurnIn;SubtitleOptions.ValidateSource(selected,sub);
        }
        if(infos.Count>0)
        {
            var info=infos[0];var o=job.Options;var lengths=new List<double>();
            for(int index=0;index<infos.Count;index++)
            {
                var edit=job.InputOptions?.ElementAtOrDefault(index)??new ConversionOptions();
                if(job.InputOptions is not null){Validate(new(){FeatureId="mp4",Inputs=[job.Inputs[index]],Output=job.Output,Options=edit});Spatial(infos[index],edit);}
                lengths.Add(MediaFilters.Duration(infos[index],edit));
            }
            var fullDuration=f.Operation switch{Operation.Join=>lengths.Sum(),Operation.AudioMix=>lengths.Max(),Operation.Mux when !o.Mute=>lengths.Min(),_=>lengths[0]};
            if(o.Start>=fullDuration && fullDuration>0)throw new ArgumentException("开始时间超出媒体时长。");
            Spatial(info,o);
            job.Duration=((o.End>0?Math.Min(o.End,fullDuration):fullDuration)-o.Start)/o.Speed;
            static void Spatial(MediaInfo media,ConversionOptions option)
            {
                if(option.CropWidth>0 && (option.CropX+option.CropWidth>media.Width || option.CropY+option.CropHeight>media.Height))throw new ArgumentException("裁剪区域超出画面。");
                if(option.DelogoWidth>0 && (option.DelogoX+option.DelogoWidth>media.Width || option.DelogoY+option.DelogoHeight>media.Height))throw new ArgumentException("水印区域超出画面。");
            }
        }
        else job.Duration=job.Options.RecordSeconds;
        var args=BuildArguments(job,infos);
        var result=await ProcessRunner.Run(FFmpeg,args,ct,line=>
        {
            if(line.StartsWith("out_time_us=") && long.TryParse(line[12..],out var us) && job.Duration>0) progress(Math.Clamp(us/1000000d/job.Duration*100,0,99.9));
        });
        job.Log=result.Error;
        if(result.ExitCode!=0) throw new InvalidOperationException(f.Operation==Operation.Record && OperatingSystem.IsMacOS()?ScreenCapture.MacPermissionMessage(result.Error):result.Error);
        progress(100);
    }
    public static List<string> BuildArguments(Job job,IReadOnlyList<MediaInfo> infos)
    {
        var f=Catalog.Find(job.FeatureId);var o=job.Options;List<string> a=["-hide_banner","-nostdin","-n","-progress","pipe:1","-nostats"];
        var combined=f.Operation==Operation.AudioMix || f.Operation==Operation.Join && (job.Inputs.Length>1 || job.InputOptions is not null) || f.Operation==Operation.Mux && job.InputOptions is not null;
        var videoFilters=MediaFilters.Video(o,job.Duration,"out",combined,job.Inputs.FirstOrDefault());var audioFilters=MediaFilters.Audio(o,job.Duration,combined);
        if(f.Operation==Operation.Record) a.AddRange(ScreenCapture.InputArguments(o,OperatingSystem.IsMacOS()));
        else
        {
            foreach(var path in job.Inputs){if(!combined && o.Start>0)a.AddRange(["-ss",Number(o.Start)]);a.AddRange(["-i",path]);}
            if(SubtitleOptions.Mode(o)==SubtitleMode.ExternalTrack){if(o.Start>0)a.AddRange(["-ss",Number(o.Start)]);a.AddRange(["-i",o.Subtitle]);}
            if(o.End>0) a.AddRange(["-t",Number((o.End-o.Start)/o.Speed)]);
        }
        if(f.Operation==Operation.Join && combined)
        {
            bool audioOnly=IsAudio(o.Format),withAudio=!o.Mute;var graph=new StringBuilder();var parts=new StringBuilder();
            for(int i=0;i<infos.Count;i++)
            {
                if(!audioOnly)
                {
                    if(!infos[i].HasVideo) throw new ArgumentException("视频合并需要所有输入含视频画面。");
                    int w=Math.Max(2,infos[0].Width/2*2),h=Math.Max(2,infos[0].Height/2*2);
                    var edit=job.InputOptions?.ElementAtOrDefault(i)??new ConversionOptions();
                    var vf=MediaFilters.Video(edit,MediaFilters.Duration(infos[i],edit),"i"+i,true,job.Inputs[i]);
                    vf.AddRange([$"scale={w}:{h}:force_original_aspect_ratio=decrease",$"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2","setsar=1","fps="+Number(o.Fps>0?o.Fps:25),"setpts=PTS-STARTPTS"]);
                    graph.Append($"[{i}:v:{infos[i].VideoStreamIndex}]"+string.Join(",",vf)+$"[v{i}];");parts.Append($"[v{i}]");
                }
                if(withAudio)
                {
                    var edit=job.InputOptions?.ElementAtOrDefault(i)??new ConversionOptions();
                    if(infos[i].HasAudio && !edit.Mute)
                    {
                        var af=MediaFilters.Audio(edit,MediaFilters.Duration(infos[i],edit),true);af.AddRange(["aresample=44100","aformat=sample_fmts=fltp:channel_layouts=stereo","asetpts=PTS-STARTPTS"]);
                        graph.Append($"[{i}:a:{infos[i].AudioStreamIndex}]"+string.Join(",",af)+$"[a{i}];");
                    }
                    else {if(audioOnly && !edit.Mute) throw new ArgumentException("音频合并需要所有输入包含音轨。");graph.Append($"anullsrc=r=44100:cl=stereo,atrim=duration={Number(MediaFilters.Duration(infos[i],edit))},asetpts=PTS-STARTPTS[a{i}];");}
                    parts.Append($"[a{i}]");
                }
            }
            graph.Append(parts).Append($"concat=n={infos.Count}:v={(audioOnly?0:1)}:a={(withAudio?1:0)}").Append((audioOnly?"":"[vout]")+(withAudio?"[aout]":""));
            a.AddRange(["-filter_complex",graph.ToString()]);if(!audioOnly)a.AddRange(["-map","[vout]"]);if(withAudio)a.AddRange(["-map","[aout]"]);
        }
        else if(f.Operation==Operation.AudioMix)
        {
            if(infos.Any(i=>!i.HasAudio))throw new ArgumentException("混音输入必须包含音轨。");
            var graph=new StringBuilder();for(int i=0;i<infos.Count;i++){var edit=job.InputOptions?.ElementAtOrDefault(i)??new();var filters=MediaFilters.Audio(edit,MediaFilters.Duration(infos[i],edit),true);filters.Add(edit.Mute?"volume=0":"anull");graph.Append($"[{i}:a:{infos[i].AudioStreamIndex}]"+string.Join(",",filters)+$"[mix{i}];");}
            graph.Append(string.Concat(Enumerable.Range(0,infos.Count).Select(i=>$"[mix{i}]"))+$"amix=inputs={infos.Count}:duration=longest:normalize=1[aout]");
            a.AddRange(["-filter_complex",graph.ToString(),"-map","[aout]"]);
        }
        else if(f.Operation==Operation.Mux)
        {
            if(!infos[0].HasVideo || !infos[1].HasAudio) throw new ArgumentException("第一个文件需包含视频，第二个文件需包含音频。");
            if(job.InputOptions is not null)
            {
                var v=job.InputOptions.ElementAtOrDefault(0)??new();var vf=MediaFilters.Video(v,MediaFilters.Duration(infos[0],v),"mux",true,job.Inputs[0]);vf.Add("null");
                var graph=$"[0:v:{infos[0].VideoStreamIndex}]"+string.Join(",",vf)+"[vout]";var labels=new List<string>();
                if(!o.Mute)
                {
                    using var json=JsonDocument.Parse(infos[1].RawJson);var count=o.KeepAllAudioStreams?json.RootElement.GetProperty("streams").EnumerateArray().Count(s=>s.GetProperty("codec_type").GetString()=="audio"):1;
                    for(int index=0;index<count;index++){var au=job.InputOptions.ElementAtOrDefault(1)??new();var af=MediaFilters.Audio(au,MediaFilters.Duration(infos[1],au),true);af.AddRange(audioFilters);af.Add("anull");var label="[muxaudio"+index+"]";graph+=$";[1:a:{(o.KeepAllAudioStreams?index:infos[1].AudioStreamIndex)}]"+string.Join(",",af)+label;labels.Add(label);}
                    audioFilters.Clear();
                }
                a.AddRange(["-filter_complex",graph,"-map","[vout]"]);foreach(var label in labels)a.AddRange(["-map",label]);if(!o.Mute)a.Add("-shortest");
            }
            else{a.AddRange(["-map",$"0:v:{o.VideoStreamIndex}"]);if(!o.Mute)a.AddRange(["-map",o.KeepAllAudioStreams?"1:a?":$"1:a:{o.AudioStreamIndex}","-shortest"]);}
        }
        else if(f.Operation==Operation.SplitAudio) a.AddRange(["-map",o.KeepAllAudioStreams?"0:a?":$"0:a:{o.AudioStreamIndex}","-vn"]);
        else if(f.Operation==Operation.SplitVideo) a.AddRange(["-map",$"0:v:{o.VideoStreamIndex}","-an"]);
        else
        {
            if(!IsAudio(o.Format))a.AddRange(["-map",$"0:v:{o.VideoStreamIndex}?"]);else a.Add("-vn");
            if(!o.Mute && !IsImage(o.Format) && o.Format!="gif" && f.Operation!=Operation.Frames)a.AddRange(["-map",o.KeepAllAudioStreams?"0:a?":$"0:a:{o.AudioStreamIndex}?"]);
        }
        if(o.CopyStreams && f.Id=="clip")a.AddRange(["-avoid_negative_ts","make_zero"]);
        if(f.Operation==Operation.Frames) {videoFilters.Add("fps=1/"+Number(o.FrameInterval));Directory.CreateDirectory(job.Output);}
        if(o.Format=="ico"){int w=o.Width>0?o.Width:o.Height>0?o.Height:256,h=o.Height>0?o.Height:w;videoFilters.Add($"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2");}
        if(o.Format=="gif") {if(o.Fps>0)videoFilters.Add("fps="+Number(o.Fps));videoFilters.Add("split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse");}
        int complex=a.IndexOf("-filter_complex");
        void Filters(List<string> filters,string source,string target,string option)
        {
            int mapping=a.IndexOf(source);
            if(complex>=0 && mapping>=0){a[complex+1]+=";"+source+string.Join(",",filters)+target;a[mapping]=target;}
            else a.AddRange([option,string.Join(",",filters)]);
        }
        if(videoFilters.Count>0 && !IsAudio(o.Format))Filters(videoFilters,"[vout]","[vedited]","-vf");
        if(audioFilters.Count>0 && !o.Mute && infos.Any(i=>i.HasAudio) && !IsImage(o.Format) && o.Format!="gif" && f.Operation!=Operation.SplitVideo)Filters(audioFilters,"[aout]","[aedited]","-af");
        if(o.Mute || IsImage(o.Format) || o.Format=="gif" || f.Operation==Operation.Frames)a.Add("-an");
        if(!o.KeepMetadata)a.AddRange(["-map_metadata","-1"]);
        if(o.CopyStreams){a.AddRange(["-c","copy"]);if(o.Format=="m4v")a.AddRange(["-f","mp4"]);}
        else if(f.Operation==Operation.Frames || IsImage(o.Format)) {if(f.Operation!=Operation.Frames)a.AddRange(["-frames:v","1"]);if(o.Format=="jpg")a.AddRange(["-q:v",Number(Math.Clamp(o.Quality/4d,2,12))]);if(o.Format=="webp")a.AddRange(["-quality",Number(Math.Clamp(100-o.Quality*90d/63,10,100))]);if(o.Format=="avif")a.AddRange(["-c:v","libaom-av1","-still-picture","1","-crf",o.Quality.ToString(),"-cpu-used","6"]);}
        else if(o.Format!="gif")
        {
            if(!IsAudio(o.Format))
            {
                string codec=o.VideoCodec=="自动"?o.Format switch {"webm"=>"libvpx-vp9","avi"=>"mpeg4","wmv"=>"wmv2","flv"=>"flv","mpg"=>"mpeg2video",_=>"mpeg4"}:o.VideoCodec;
                a.AddRange(["-c:v",codec]);if(codec!="copy")a.AddRange(["-pix_fmt","yuv420p"]);
                if(codec is "mpeg4" or "wmv2" or "flv" or "mpeg2video")a.AddRange(["-q:v",Number(Math.Clamp(o.Quality/4d,2,12))]);
                else if(codec=="libvpx-vp9")a.AddRange(["-crf",o.Quality.ToString(),"-b:v","0","-deadline","good","-cpu-used","4"]);
                else if(codec is "libx264" or "libx265")a.AddRange(["-crf",o.Quality.ToString(),"-preset","medium"]);
                else if(codec=="libaom-av1")a.AddRange(["-crf",o.Quality.ToString(),"-b:v","0","-cpu-used","6"]);
                else if(codec.EndsWith("_nvenc"))a.AddRange(["-rc","vbr","-cq",Math.Min(51,o.Quality).ToString(),"-b:v","0"]);
                else if(codec.EndsWith("_qsv"))a.AddRange(["-global_quality",Math.Min(51,o.Quality).ToString()]);
                else if(codec.EndsWith("_amf"))a.AddRange(["-rc","cqp","-qp_i",Math.Min(51,o.Quality).ToString(),"-qp_p",Math.Min(51,o.Quality).ToString()]);
                else if(codec=="h264_mf")a.AddRange(["-rate_control","quality","-quality",Math.Clamp(100-o.Quality*100/63,1,100).ToString()]);
                if(o.Fps>0)a.AddRange(["-r",Number(o.Fps)]);
            }
            string ac=o.AudioCodec=="自动"?o.Format switch {"mp3"=>"libmp3lame","flac"=>"flac","wav"=>"pcm_s16le","aiff"=>"pcm_s16be","ogg"=>"libvorbis","opus" or "webm"=>"libopus","ac3"=>"ac3","wma" or "wmv"=>"wmav2","mpg"=>"mp2",_=>"aac"}:o.AudioCodec;
            if(!o.Mute && f.Operation!=Operation.SplitVideo){a.AddRange(["-c:a",ac]);if(ac!="copy"){if(ac is not ("flac" or "pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm_f32le" or "pcm_s24be" or "alac"))a.AddRange(["-b:a",o.AudioBitrate+"k"]);if(ac=="libopus" || o.SampleRate>0)a.AddRange(["-ar",(ac=="libopus"?48000:o.SampleRate).ToString()]);if(o.AudioChannels>0)a.AddRange(["-ac",o.AudioChannels.ToString()]);}}
            if(o.Format is "mp4" or "mov" or "m4a") a.AddRange(["-movflags",f.Operation==Operation.Record?"frag_keyframe+empty_moov":"+faststart"]);
        }
        SubtitleOptions.Map(a,o,job.Inputs.Length);
        a.Add(f.Operation==Operation.Frames?Path.Combine(job.Output,"frame-%06d.png"):job.Output);return a;
    }
    public static string UniqueOutput(string folder,string name,string format,IEnumerable<string>? reserved=null,bool directory=false)
    {
        Directory.CreateDirectory(folder);var safe=string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));if(string.IsNullOrWhiteSpace(safe))safe="output";
        var set=new HashSet<string>(reserved??[],OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        for(int i=0;;i++) {var path=Path.Combine(folder,safe+(i==0?"":" ("+i+")")+(directory?"":"."+format));if(!File.Exists(path)&&!Directory.Exists(path)&&!set.Contains(path))return path;}
    }
}
