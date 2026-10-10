using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;
public sealed class MediaEngine : IMediaEngine
{
    public AppSettings Settings { get; }
    private readonly Func<string,CancellationToken,Task<IReadOnlyList<HardwareEncoderResult>>> _hardwareTest;
    public MediaEngine(AppSettings settings,Func<string,CancellationToken,Task<IReadOnlyList<HardwareEncoderResult>>>? hardwareTest=null)
    {Settings=settings;_hardwareTest=hardwareTest??((path,token)=>HardwareAcceleration.TestAsync(path,token,refresh:false));}
    public string FFmpeg => Resolve(Settings.FFmpegPath,"ffmpeg");
    public string FFprobe => Resolve(Settings.FFprobePath,"ffprobe");
    public static string Number(double n) => n.ToString("0.######",CultureInfo.InvariantCulture);
    private static IEnumerable<string> BundledToolFolders()
    {
        yield return Path.Combine(AppContext.BaseDirectory,"tools");
        if(OperatingSystem.IsMacOS())
            yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Resources","tools"));
    }
    public static bool UsesBundledTools => BundledToolFolders().Any(folder =>
        File.Exists(Path.Combine(folder,"download-tools.json")) || File.Exists(Path.Combine(folder,"ffmpeg-bundle.json")));
    public static string Resolve(string configured,string tool)
    {
        var name=tool+(OperatingSystem.IsWindows()?".exe":"");
        foreach(var folder in BundledToolFolders())
        {
            var bundled=Path.Combine(folder,name);
            if(File.Exists(bundled))return bundled;
        }
        if(UsesBundledTools)throw new FileNotFoundException($"应用缺少内置 {tool}，请重新安装完整版本。",name);
        if (!string.IsNullOrWhiteSpace(configured)){if(File.Exists(configured))return Path.GetFullPath(configured);throw new FileNotFoundException($"配置的 {tool} 路径不存在。",configured);}
        var env=Environment.GetEnvironmentVariable("AVAMEDIA_"+tool.ToUpperInvariant());
        if(!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
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
        if(OperatingSystem.IsMacOS())foreach(var folder in new[]{"/opt/homebrew/bin","/usr/local/bin"}){var candidate=Path.Combine(folder,name);if(File.Exists(candidate))return candidate;}
        foreach(var root in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)) {var file=Path.Combine(root,name);if(File.Exists(file)) return file;}
        throw new FileNotFoundException($"未找到 {tool}。请在“选项 → 外部工具”中指定路径。",name);
    }
    public async Task<MediaInfo> Probe(string path,CancellationToken ct=default,int videoStreamIndex=0,int audioStreamIndex=0)
    {
        List<string> probeArgs=["-v","error","-show_format","-show_streams","-show_chapters"];
        if(HeifImage.Supports(path))probeArgs.Add("-show_stream_groups");
        probeArgs.AddRange(["-of","json",path]);
        var r=await ProcessRunner.Run(FFprobe,probeArgs,ct,maximumOutputChars:int.MaxValue).ConfigureAwait(false);
        if(r.ExitCode!=0) throw new InvalidDataException(r.Error);
        using var json=JsonDocument.Parse(r.Output);var root=json.RootElement;
        var streams=root.GetProperty("streams").EnumerateArray().ToArray();
        var videos=streams.Where(s=>s.GetProperty("codec_type").GetString()=="video").ToArray();
        var audios=streams.Where(s=>s.GetProperty("codec_type").GetString()=="audio").ToArray();
        if(videoStreamIndex<0 || audioStreamIndex<0 || videoStreamIndex>0 && videoStreamIndex>=videos.Length || audioStreamIndex>0 && audioStreamIndex>=audios.Length)throw new ArgumentException("源文件不包含所选视频或音频轨。");
        videoStreamIndex=MediaStreams.VideoIndex(videos,videoStreamIndex);
        var video=videos.ElementAtOrDefault(videoStreamIndex);
        if(video.ValueKind!=JsonValueKind.Undefined && !MediaStreams.IsContentVideo(video))video=default;
        var audio=audios.ElementAtOrDefault(audioStreamIndex);
        var duration=MediaStreams.Duration(root,streams);
        int width=video.ValueKind==JsonValueKind.Undefined?0:video.GetProperty("width").GetInt32(),height=video.ValueKind==JsonValueKind.Undefined?0:video.GetProperty("height").GetInt32();
        if(video.ValueKind!=JsonValueKind.Undefined && video.TryGetProperty("side_data_list",out var sideData))
            foreach(var side in sideData.EnumerateArray())if(side.TryGetProperty("rotation",out var rotation) && MediaStreams.IsQuarterTurn(rotation.GetDouble())){(width,height)=(height,width);break;}
        if(HeifImage.Supports(path))
        {
            var primary=HeifImage.Read(root);width=primary.Width;height=primary.Height;
            videoStreamIndex=primary.VideoIndex;video=videos[videoStreamIndex];
        }
        int rate=0,channels=0;if(audio.ValueKind!=JsonValueKind.Undefined){if(audio.TryGetProperty("sample_rate",out var sample))int.TryParse(sample.GetString(),out rate);if(audio.TryGetProperty("channels",out var ch))channels=ch.GetInt32();}
        var frameRate=MediaStreams.FrameRate(video);
        return new(duration,width,height,audio.ValueKind!=JsonValueKind.Undefined,video.ValueKind!=JsonValueKind.Undefined,r.Output,Codec(video),Codec(audio),rate,channels,videoStreamIndex,audioStreamIndex,frameRate);
        static string Codec(JsonElement e) => e.ValueKind==JsonValueKind.Undefined?"":e.GetProperty("codec_name").GetString()??"";
    }
    public async Task<byte[]> Thumbnail(string input,double seconds,int width=640,int height=360,CancellationToken ct=default,bool pad=true,int videoStreamIndex=0,bool endExclusive=false)
    {
        if(!double.IsFinite(seconds) || seconds<0 || width<1 || height<1 || videoStreamIndex<0)throw new ArgumentException("逐帧定位参数无效。");
        if(!pad && AppleImageIO.Supports(input))return await AppleImageIO.ThumbnailAsync(input,width,height,ct).ConfigureAwait(false);
        if(ImageFormats.Supports(input))return await ImageViewerCodec.ThumbnailAsync(input,width,height,pad,ct).ConfigureAwait(false);
        if(endExclusive && seconds>0)
        {
            var media=await Probe(input,ct,videoStreamIndex);var origin=TimelineOrigin(media);
            videoStreamIndex=media.VideoStreamIndex;
            var lookback=1d;double? last=null;
            while(last is null)
            {
                var fromTime=Math.Max(0,seconds-lookback);
                foreach(var time in await FrameTimes(input,fromTime,seconds,origin,videoStreamIndex,ct))
                    if(time>=0 && time<seconds-.0000001)last=last is null?time:Math.Max(last.Value,time);
                if(last is not null || fromTime==0)break;
                lookback*=2;
            }
            if(last is null)throw new InvalidDataException("结束时间之前没有可预览的视频帧。");
            seconds=Math.Max(0,last.Value-.000001);
        }
        var from=Math.Max(0,seconds);
        var map="0:"+MediaStreams.VideoSpecifier(videoStreamIndex);
        var prefix="";
        List<string> args=["-v","error","-nostdin","-filter_complex_threads","1"];
        if(HeifImage.Supports(input))
        {
            var primary=HeifImage.Parse((await Probe(input,ct).ConfigureAwait(false)).RawJson);map=primary.Map();
            if(primary.PreFilter.Length>0){args.Add("-noautorotate");prefix=primary.PreFilter+",";}
        }
        else args.AddRange(["-ss",Number(from)]);
        args.AddRange(["-i",input]);
        HeifImage.AppendVideo(args,map,prefix+$"scale={width}:{height}:force_original_aspect_ratio=decrease"+(pad?$",pad={width}:{height}:(ow-iw)/2:(oh-ih)/2":""));
        args.AddRange(["-frames:v","1","-f","image2pipe","-c:v","png","pipe:1"]);
        using var p=await ProcessRunner.StartAsync(FFmpeg,args,ct).ConfigureAwait(false);
        using var reg=ct.Register(()=>{try{p.Kill(true);}catch(InvalidOperationException){}});
        var error=p.StandardError.ReadToEndAsync();using var output=new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(output,ct);await p.WaitForExitAsync(ct);
        if(p.ExitCode!=0) throw new InvalidDataException(await error);
        return output.ToArray();
    }
    public async Task<double> AdjacentFrameTime(string input,double seconds,int direction,CancellationToken ct=default,int videoStreamIndex=0)
    {
        if(!double.IsFinite(seconds) || seconds<0 || direction is not (-1 or 1))throw new ArgumentException("逐帧定位参数无效。");
        var media=await Probe(input,ct,videoStreamIndex);
        if(!media.HasVideo || media.Duration<=0)throw new ArgumentException("文件没有可定位的视频帧。");
        videoStreamIndex=media.VideoStreamIndex;
        seconds=Math.Min(seconds,media.Duration);var origin=TimelineOrigin(media);var span=1d;
        while(true)
        {
            var from=direction<0?Math.Max(0,seconds-span):Math.Max(0,seconds-.1);
            var to=direction<0?seconds:Math.Min(media.Duration,seconds+span);
            var candidates=(await FrameTimes(input,from,to,origin,videoStreamIndex,ct)).Where(t=>t>=0 && t<=media.Duration && (direction<0?t<seconds-.0000001:t>seconds+.0000001)).ToArray();
            if(candidates.Length>0)return direction<0?candidates.Max():candidates.Min();
            if(direction<0 && from==0)return 0;
            if(direction>0 && to>=media.Duration)return media.Duration;
            span*=2;
        }
    }
    private static double TimelineOrigin(MediaInfo info)
    {
        using var json=JsonDocument.Parse(info.RawJson);
        return json.RootElement.TryGetProperty("format",out var format) && format.TryGetProperty("start_time",out var start) && double.TryParse(start.GetString(),CultureInfo.InvariantCulture,out var value) && double.IsFinite(value)?value:0;
    }
    private async Task<double[]> FrameTimes(string input,double from,double to,double origin,int videoStreamIndex,CancellationToken ct)
    {
        // Long GOPs and high frame rates can exceed the bounded process log. Parse timestamps
        // as they arrive instead of parsing a JSON document whose beginning may be discarded.
        var times=new List<double>();
        var result=await ProcessRunner.Run(FFprobe,["-v","error","-select_streams",MediaStreams.VideoSpecifier(videoStreamIndex),"-read_intervals",Number(origin+from)+"%"+Number(origin+to),"-show_frames","-show_entries","frame=best_effort_timestamp_time","-of","csv=p=0",input],ct,line=>
        {
            var value=line.Split(',')[0];
            if(double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var time) && double.IsFinite(time))times.Add(time-origin);
        },maximumOutputChars:4096);
        if(result.ExitCode!=0)throw new InvalidDataException(result.Error);
        return times.ToArray();
    }
    public static bool IsAudio(string format) => new[]{"mp3","flac","wav","m4a","ogg","aac","ac3","wma","opus","aiff"}.Contains(format);
    public static bool IsImage(string format) => format is "jpg" or "jpeg" or "png" or "webp" or "bmp" or "tif" or "tiff" or "ico" or "avif" or "heic" or "heif";
    public static void Validate(Job job)
    {
        var feature=Catalog.Find(job.FeatureId);var o=job.Options;
        if(AutomationTasks.Supports(job)){AutomationTasks.Validate(job);return;}
        if(feature.Operation==Operation.FolderClassify){FolderClassificationJobService.Validate(job);return;}
        if(job.Inputs.Length==0) throw new ArgumentException("请添加文件。");
        if(feature.Operation is not (Operation.Download or Operation.IsoCopy)) foreach(var path in job.Inputs) if(!File.Exists(path)) throw new FileNotFoundException("源文件不存在",path);
        if(string.IsNullOrWhiteSpace(job.Output)) throw new ArgumentException("输出路径不能为空。");
        if(feature.Operation is not (Operation.Download or Operation.IsoCopy) && job.Inputs.Any(p=>string.Equals(Path.GetFullPath(p),Path.GetFullPath(job.Output),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))) throw new ArgumentException("输出不能覆盖源文件。");
        if(!double.IsFinite(o.Start) || !double.IsFinite(o.End) || !double.IsFinite(o.Speed) || !double.IsFinite(o.Volume) || !double.IsFinite(o.Fps) || !double.IsFinite(o.FadeIn) || !double.IsFinite(o.FadeOut) || !double.IsFinite(o.FrameInterval))throw new ArgumentException("参数必须是有限数值。");
        if(o.Start<0 || o.End<0 || (o.End>0 && o.End<=o.Start)) throw new ArgumentException("结束时间必须晚于开始时间。");
        if(o.Speed is <0.25 or >4) throw new ArgumentException("速度必须在 0.25 到 4 倍之间。");
        if(o.CropWidth<0 || o.CropHeight<0 || o.CropX<0 || o.CropY<0 || o.Width<0 || o.Height<0 || o.DelogoX<0 || o.DelogoY<0 || o.DelogoWidth<0 || o.DelogoHeight<0) throw new ArgumentException("尺寸及坐标不能小于零。");
        if((o.CropWidth>0)!=(o.CropHeight>0)) throw new ArgumentException("裁剪宽度和高度必须同时设置。");
        if((o.DelogoWidth>0)!=(o.DelogoHeight>0)) throw new ArgumentException("水印区域宽度和高度必须同时设置。");
        if(o.FadeIn<0 || o.FadeOut<0 || o.Volume<0 || o.AudioBitrate<16 || o.Fps<0 || o.FrameInterval<=0) throw new ArgumentException("参数超出允许范围。");
        if(feature.Operation==Operation.Download){if(job.Inputs.Length!=1)throw new ArgumentException("每个下载任务须包含一个视频链接。");_=DownloadLinks.Normalize(job.Inputs[0]);(o.Download??new()).Validate();}
        if(feature.Operation==Operation.IsoCopy && (job.Inputs.Length!=1 || string.IsNullOrWhiteSpace(job.Inputs[0])))throw new ArgumentException("光盘复制任务须包含一个设备路径。");
        if(feature.Operation is Operation.PdfAge or Operation.PdfCompress || PdfTools.Supports(feature.Operation) && o.Pdf is not null)PdfTools.Validate(job);
        if(feature.Operation==Operation.VideoCompress)VideoCompression.ValidateJob(job);
        if(feature.Operation==Operation.VideoSlim){VideoSlimming.ValidateJob(job);return;}
        if(feature.Operation==Operation.ImageCompress){if(job.Inputs.Length!=1)throw new ArgumentException("每个图片压缩任务处理一张图片。");(o.ImageCompression??new ImageCompressionOptions{Format=o.Format}).Validate();}
        if(feature.Operation==Operation.Mux && job.Inputs.Length!=2) throw new ArgumentException("混流需要一个视频文件和一个音频文件。");
        if(feature.Operation==Operation.AudioMix && job.Inputs.Length<2) throw new ArgumentException("混音需要至少两个文件。");
        if(feature.Operation==Operation.Transcribe){SpeechSubtitleService.Validate(job);return;}
        if(feature.Operation==Operation.VideoSummary){VideoSummaryService.Validate(job);return;}
        if(feature.Operation==Operation.PersonClip){PersonClipService.Validate(job);return;}
        if(feature.Operation==Operation.MediaTag){MediaTagJobService.Validate(job);return;}
        ValidateEncodingOptions(o);
        SourceVideoExport.ValidateJob(job);
        SourceClipCopy.Validate(job);
        var copyJoined = SourceClipCopy.IsJoined(job);
        if(job.InputOptions is not null)
        {
            if(job.InputOptions.Count!=job.Inputs.Length)throw new ArgumentException("输入文件与独立参数数量不一致。");
            for(int index=0;index<job.Inputs.Length;index++)Validate(new(){FeatureId="mp4",Inputs=[job.Inputs[index]],Output=job.Output,Options=job.InputOptions[index]});
        }
        if(SubtitleOptions.Mode(o) is SubtitleMode.Preserve or SubtitleMode.ExternalTrack && feature.Operation is Operation.Join or Operation.AudioMix && !copyJoined)throw new ArgumentException("合并和混音暂不支持输出独立字幕轨；视频合并可在每个输入中烧录字幕。");
        if(feature.Operation==Operation.Join && job.Inputs.Length>1 && SubtitleOptions.Mode(o)==SubtitleMode.BurnIn && string.IsNullOrWhiteSpace(o.Subtitle))throw new ArgumentException("源字幕需在每个合并输入的选项中分别选择烧录。");
        if(o.CopyStreams && (feature.Operation==Operation.AudioMix || feature.Operation==Operation.Join && !copyJoined && (job.Inputs.Length>1 || job.InputOptions is not null)))throw new ArgumentException("合并编辑和混音需要重新编码。");
        if((o.VideoCodec=="copy" || o.AudioCodec=="copy") && (feature.Operation==Operation.AudioMix || feature.Operation==Operation.Join && !copyJoined && (job.Inputs.Length>1 || job.InputOptions is not null)))throw new ArgumentException("合并编辑和混音的输出编码器需要重新编码，不能选择 copy。");
        if(feature.Operation==Operation.Mux && job.InputOptions is not null)
        {
            var video=job.InputOptions[0];var audio=job.InputOptions[1];var outputTrim=o.Start>0 || o.End>0;
            if((o.CopyStreams || o.VideoCodec=="copy") && (outputTrim || video.Start>0 || video.End>0 || HasVideoFilters(video)))throw new ArgumentException("混流的视频输入或输出区间需要画面处理，请选择视频编码器。");
            if(!o.Mute && !audio.Mute && (o.CopyStreams || o.AudioCodec=="copy") && (outputTrim || audio.Start>0 || audio.End>0 || HasAudioFilters(audio)))throw new ArgumentException("混流的音频输入或输出区间需要音频处理，请选择音频编码器。");
        }
        if(o.KeepAllAudioStreams && feature.Operation is Operation.Join or Operation.AudioMix or Operation.SplitVideo && !copyJoined)throw new ArgumentException("当前合并、混音和视频流提取不支持保留独立的所有音频流。");
    }
    public static bool HasVideoFilters(ConversionOptions o) => o.CropWidth>0 || o.DelogoWidth>0 || o.Speed!=1 || o.Width>0 || o.Height>0 || o.Fps>0 || o.Rotation!=0 || o.Flip || o.FadeIn>0 || o.FadeOut>0 || SubtitleOptions.Mode(o)==SubtitleMode.BurnIn;
    public static bool HasAudioFilters(ConversionOptions o) => o.Speed!=1 || o.Volume!=1 || (o.AudioFadeIn??o.FadeIn)>0 || (o.AudioFadeOut??o.FadeOut)>0 || o.Echo || o.NoiseReduction || o.VoiceEnhancement || o.ReverseAudio;
    public static bool HasFilters(ConversionOptions o) => HasVideoFilters(o) || HasAudioFilters(o);
    public static void ValidateEncodingOptions(ConversionOptions o)
    {
        VideoEncoding.Validate(o);
        SourceVideoExport.ValidateOptions(o);
        if(!o.CopyStreams && HardwareTranscoding.Encoder(o.VideoCodec) is {} hardwareEncoder && !HardwareTranscoding.Compatible(o.Format,hardwareEncoder.Format))
            throw new ArgumentException("所选硬件编码器与输出格式不兼容，请使用自动编码或选择兼容格式。");
        if(o.Threads is <0 or >16)throw new ArgumentException("编码线程数必须在 0 到 16 之间。");
        if(o.VoiceEnhancementStrength is <1 or >100)throw new ArgumentException("人声增强强度必须在 1 到 100 之间。");
        if(o.ImageQuality is <1 or >100)throw new ArgumentException("图片质量必须在 1 到 100 之间。");
        if(o.Quality is <1 or >63 || o.Rotation is not (0 or 90 or 180 or 270))throw new ArgumentException("参数超出允许范围。");
        SubtitleOptions.Validate(o);
        VideoFormats.ValidateMobileOutput(o);
        VideoFormats.ValidateTransportOutput(o);
        foreach(var fade in new[]{o.AudioFadeIn,o.AudioFadeOut})if(fade is {} value && (!double.IsFinite(value) || value<0))throw new ArgumentException("音频淡入淡出时长必须为有限的非负数。");
        if(o.SampleRate<0 || o.SampleRate>192000 || o.SampleRate is >0 and <8000 || o.AudioChannels<0 || o.AudioChannels>8)throw new ArgumentException("采样率或声道超出允许范围。");
        if(o.CopyStreams && HasFilters(o))throw new ArgumentException("流复制不能同时使用画面或音频滤镜，请选择 MP4 / MKV 重新编码，或关闭流复制。");
        if(o.VideoCodec=="copy" && !o.CopyStreams && HasVideoFilters(o))throw new ArgumentException("视频 copy 不能使用画面滤镜，请选择视频编码器。");
        if((o.CopyStreams || o.AudioCodec=="copy") && !o.Mute && (HasAudioFilters(o) || o.SampleRate>0 || o.AudioChannels>0))throw new ArgumentException("音频 copy 不能使用音效、采样率或声道转换，请选择音频编码器。");
        if(o.KeepAllAudioStreams && !VideoFormats.IsTransportStream(o.Format) && o.Format is not ("mp4" or "mkv" or "mov" or "m4a" or "m4v" or "webm" or "avi" or "asf" or "wmv" or "3gp" or "3g2" or "ogg" or "ogv"))throw new ArgumentException("此输出格式不能保留多个独立音轨，请选择 MP4、MKV 等多音轨容器。");
    }
    public static double ValidateEdits(Job job,IReadOnlyList<MediaInfo> infos)
    {
        Validate(job);var feature=Catalog.Find(job.FeatureId);var options=job.Options;
        if(infos.Count!=job.Inputs.Length || infos.Count==0)throw new ArgumentException("媒体信息与输入文件数量不一致。");
        var lengths=new List<double>();
        for(int index=0;index<infos.Count;index++)
        {
            if(!double.IsFinite(infos[index].Duration) || infos[index].Duration<0)throw new ArgumentException("合并与混流输入须有有效媒体时长。");
            var edit=job.InputOptions?.ElementAtOrDefault(index)??new ConversionOptions();
            if(job.InputOptions is not null)MediaEditValidation.Validate(infos[index],edit);
            if(feature.Operation is Operation.Join or Operation.AudioMix or Operation.Mux && infos[index].Duration<=0)throw new ArgumentException("合并与混流输入须有有效媒体时长。");
            if(feature.Operation==Operation.Join && !IsAudio(options.Format) && !infos[index].HasVideo)throw new ArgumentException("视频合并需要所有输入含视频画面。");
            if(feature.Operation==Operation.Join && IsAudio(options.Format) && !infos[index].HasAudio && !edit.Mute && !options.Mute || feature.Operation==Operation.AudioMix && !infos[index].HasAudio)throw new ArgumentException("音频合并或混音输入必须包含音轨。");
            lengths.Add(MediaFilters.Duration(infos[index],edit));
        }
        if(feature.Operation is Operation.Frames or Operation.SplitVideo && !infos[0].HasVideo)throw new ArgumentException("此文件不包含视频画面。");
        if(feature.Operation==Operation.Mux && (!infos[0].HasVideo || !infos[1].HasAudio))throw new ArgumentException("第一个文件需包含视频，第二个文件需包含音频。");
        var fullDuration=feature.Operation switch{Operation.Join=>lengths.Sum(),Operation.AudioMix=>lengths.Max(),Operation.Mux when !options.Mute && job.InputOptions?.ElementAtOrDefault(1)?.Mute!=true=>lengths.Min(),_=>lengths[0]};
        if(!double.IsFinite(fullDuration))throw new ArgumentException("合并与混流输入须有有效媒体时长。");
        MediaEditValidation.Validate(infos[0] with{Duration=fullDuration},options);
        return ((options.End>0?Math.Min(options.End,fullDuration):fullDuration)-options.Start)/options.Speed;
    }
    public async Task Execute(Job job,Action<double> progress,CancellationToken ct)
    {
        Validate(job);var f=Catalog.Find(job.FeatureId);
        if(AutomationTasks.Supports(job)){await AutomationTasks.ExecuteAsync(this,job,progress,ct).ConfigureAwait(false);return;}
        if(f.Operation==Operation.FolderClassify)
        {await new FolderClassificationJobService(this).ExecuteAsync(job,progress,ct).ConfigureAwait(false);return;}
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.Output))!);
        if(File.Exists(job.Output) || Directory.Exists(job.Output)) throw new IOException("输出已存在，请重试以生成新的名称。");
        if(f.Operation==Operation.VideoSlim)
        {await new VideoSlimming(this).ExecuteAsync(job,progress,ct).ConfigureAwait(false);return;}
        if(f.Operation==Operation.Transcribe)
        {await new SpeechSubtitleService(this).ExecuteAsync(job,progress,ct).ConfigureAwait(false);return;}
        if(f.Operation==Operation.VideoSummary)
        {await new VideoSummaryService(this).ExecuteAsync(job,progress,ct).ConfigureAwait(false);return;}
        if(f.Operation==Operation.PersonClip)
        {await new PersonClipService(this).ExecuteAsync(job,progress,ct).ConfigureAwait(false);return;}
        if(f.Operation==Operation.MediaTag)
        {await new MediaTagJobService(this).ExecuteAsync(job,progress,ct).ConfigureAwait(false);return;}
        if(f.Operation==Operation.ImagesPdf)
        {
            var temporary=new List<string>();var inputs=new List<string>();
            try
            {
                foreach(var path in job.Inputs)
                {
                    if(Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png"){inputs.Add(path);continue;}
                    var png=Path.Combine(Path.GetTempPath(),"AvaMedia-pdf-"+Guid.NewGuid()+".png");temporary.Add(png);
                    if(HeifImage.Supports(path) && AppleImageIO.Supports(path))
                    {
                        var photo=await AppleImageIO.InspectAsync(path,ct).ConfigureAwait(false);
                        await File.WriteAllBytesAsync(png,await AppleImageIO.ThumbnailAsync(path,photo.Width,photo.Height,ct).ConfigureAwait(false),ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var primary=HeifImage.Supports(path)?HeifImage.Parse((await Probe(path,ct).ConfigureAwait(false)).RawJson):null;
                        List<string> args=["-v","error","-nostdin","-n","-filter_complex_threads","1"];
                        if(primary?.PreFilter.Length>0)args.Add("-noautorotate");args.AddRange(["-i",path]);
                        HeifImage.AppendVideo(args,primary?.Map()??"0:v:0",primary?.PreFilter is {Length:>0} filter?filter:"null");
                        args.AddRange(["-frames:v","1","-c:v","png",png]);
                        var converted=await ProcessRunner.Run(FFmpeg,args,ct).ConfigureAwait(false);
                        if(converted.ExitCode!=0)throw new InvalidDataException(converted.Error);
                    }
                    inputs.Add(png);
                }
                var documentJob=new Job{FeatureId=job.FeatureId,Inputs=inputs.ToArray(),Output=job.Output,Options=job.Options};await Task.Run(()=>DocumentEngine.Execute(documentJob,progress,ct),ct);
            }
            finally{foreach(var path in temporary)if(File.Exists(path))File.Delete(path);}
            return;
        }
        if(f.Operation is Operation.PdfMerge or Operation.PdfSplit or Operation.PdfText or Operation.PdfDocx or Operation.PdfXlsx or Operation.TextPdf or Operation.Zip or Operation.Unzip or Operation.PdfAge or Operation.PdfCompress)
        {
            await Task.Run(()=>DocumentEngine.Execute(job,progress,ct),ct);
            if(f.Operation==Operation.PdfCompress)
            {
                var before=new FileInfo(job.Inputs[0]).Length;var after=new FileInfo(job.Output).Length;
                job.ProgressDetail=after<before?$"节省 {(before-after)*100d/before:0.##}%":"体积未减小，保留原文档。";
                job.Log=$"原文档 {before} B → 输出 {after} B。\n"+job.ProgressDetail;
            }
            return;
        }
        if(f.Operation==Operation.IsoCopy)
        {
            var source=job.Inputs[0];if(source.Length==2 && source[1]==':') source="\\\\.\\"+source;
            await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite,1024*1024,true);
            var temporary=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!,".AvaMedia-iso-"+Guid.NewGuid()+".iso");
            try
            {
                await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,1024*1024,true))
                {
                    var buffer=new byte[1024*1024];int read;while((read=await input.ReadAsync(buffer,ct))>0){await output.WriteAsync(buffer.AsMemory(0,read),ct);progress(0);}
                }
                ct.ThrowIfCancellationRequested();File.Move(temporary,job.Output);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
            progress(100);return;
        }
        if(f.Operation==Operation.Info) {var info=await Probe(job.Inputs[0],ct);await File.WriteAllTextAsync(job.Output,info.RawJson,ct);progress(100);return;}
        if(f.Operation==Operation.Download)
        {
            await new VideoDownloadService(Settings).ExecuteAsync(job,progress,ct);return;
        }
        if(f.Operation==Operation.ImageCompress)
        {
            progress(0);
            var imageResult=await new FfmpegImageCompressor(this).CompressAsync(job.Inputs[0],job.Output,
                job.Options.ImageCompression??new ImageCompressionOptions{Format=job.Options.Format},ct);
            job.Log=$"原图 {imageResult.SourceBytes} B → 压缩后 {imageResult.OutputBytes} B；节省 {imageResult.SavedPercent:0.##}%；{imageResult.Width} × {imageResult.Height}。";
            job.ProgressDetail=$"节省 {imageResult.SavedPercent:0.##}%";progress(100);return;
        }
        var infos=new List<MediaInfo>();
        for(int i=0;i<job.Inputs.Length;i++)
        {
            var edit=job.InputOptions?.ElementAtOrDefault(i)??job.Options;
            var vi=f.Operation==Operation.Mux && i==1?0:edit.VideoStreamIndex;
            var ai=f.Operation==Operation.Mux && i==0?0:edit.AudioStreamIndex;
            infos.Add(await Probe(job.Inputs[i],ct,vi,edit.KeepAllAudioStreams?0:ai));
            if(job.InputOptions is not null)SubtitleOptions.ValidateSource(edit,infos[^1]);
        }
        if(infos.Count>0)SubtitleOptions.ValidateSource(job.Options,infos[0]);
        if(job.Options.VoiceEnhancement && !infos.Any(info=>info.HasAudio))throw new ArgumentException("文件没有可增强的人声音轨。");
        if(job.Options.VoiceEnhancement || job.InputOptions?.Any(option=>option.VoiceEnhancement)==true)
        {
            var filters=await ProcessRunner.Run(FFmpeg,["-hide_banner","-filters"],ct).ConfigureAwait(false);
            foreach(var name in new[]{"arnndn","highpass","loudnorm"})
                if(filters.ExitCode!=0 || !System.Text.RegularExpressions.Regex.IsMatch(filters.Output+filters.Error,@"\b"+name+@"\b"))
                    throw new InvalidOperationException("当前 FFmpeg 不支持人声增强，请使用应用内置版本。");
        }
        if(SubtitleOptions.Mode(job.Options) is SubtitleMode.ExternalTrack or SubtitleMode.BurnIn && !string.IsNullOrWhiteSpace(job.Options.Subtitle))
        {
            var sub=await Probe(job.Options.Subtitle,ct);
            var selected=job.Options.Clone();selected.Subtitle="";selected.SubtitleMode=SubtitleOptions.Mode(job.Options)==SubtitleMode.ExternalTrack?SubtitleMode.Preserve:SubtitleMode.BurnIn;SubtitleOptions.ValidateSource(selected,sub);
        }
        job.Duration=ValidateEdits(job,infos);
        if(SourceClipCopy.IsJoined(job))
        {
            await SourceClipCopy.ExecuteAsync(this,job,infos,progress,ct);
            return;
        }
        var effective=SettingsPolicy.Resolve(job.Options,Settings);
        effective.VideoStreamIndex=infos[0].VideoStreamIndex;
        VideoCompressionPlan? compressionPlan=null;
        VideoCompressionColor? compressionColor=null;
        if(f.Operation==Operation.VideoCompress)
        {
            compressionPlan=VideoCompression.Plan(new FileInfo(job.Inputs[0]).Length,infos[0],job.Options.VideoCompression??new());
            effective=VideoCompression.Resolve(effective,infos[0],compressionPlan);
            compressionColor=VideoCompressionColor.Inspect(infos[0]);
            if(compressionColor.ToneMap)
            {
                var filters=await ProcessRunner.Run(FFmpeg,["-hide_banner","-filters"],ct);
                if(filters.ExitCode!=0)throw new InvalidOperationException("无法读取 HDR 压缩所需的滤镜。"+filters.Error);
                compressionColor.ValidateFilters(filters.Output+filters.Error);
            }
        }
        string? sourceEncoderListing=null;
        if(effective.PreserveSourceAttributes)
        {
            var listing=await ProcessRunner.Run(FFmpeg,["-hide_banner","-encoders"],ct);
            if(listing.ExitCode!=0)throw new InvalidOperationException("无法读取原编码所需的编码器。"+listing.Error);
            sourceEncoderListing=listing.Output+listing.Error;
        }
        var effectiveJob=new Job{FeatureId=job.FeatureId,Inputs=job.Inputs,InputOptions=job.InputOptions?.Select(option=>option.Clone()).ToList(),Options=effective,Output=job.Output,Duration=job.Duration};
        using var subtitlePositioning=await SubtitlePositioning.PrepareAsync(this,effectiveJob,ct).ConfigureAwait(false);
        IReadOnlyList<string> hardware=[];
        if(Settings.AutoDetectGpu && effective.VideoCompression?.PreferGpu!=false && !effective.CopyStreams && effective.VideoCodec=="自动" &&
            infos.Any(i=>i.HasVideo) && HardwareAcceleration.CompatibleCodecs(effective.Format).Count>0)
            hardware=HardwareAcceleration.Candidates(effective.Format,await _hardwareTest(FFmpeg,ct),infos.FirstOrDefault(info=>info.HasVideo)?.VideoCodec??"");
        var explicitHardware=!effective.CopyStreams && HardwareTranscoding.Backend(effective.VideoCodec) is not null;
        if(explicitHardware)hardware=[effective.VideoCodec];
        if(effective.VideoCompression is {} compression)
            hardware=hardware.Where(codec=>HardwareTranscoding.VideoFormat(compression.Codec)==HardwareTranscoding.Encoder(codec)?.Format).ToArray();
        if(effective.PreserveSourceAttributes)
            hardware=hardware.Where(codec=>SourceVideoGpu.CanEncode(codec,infos[0],effective.VideoStreamIndex)).ToArray();
        ProcessResult? result=null;var hardwareLog=new StringBuilder();
        if(!effective.CopyStreams && effective.VideoCodec!="copy" && effective.VideoCompression is null && VideoFormats.OriginalOutputExtensions.Contains(effective.Format))
        {
            var bitrate=VideoEncoding.TargetBitrate(effectiveJob,infos);
            hardwareLog.AppendLine(bitrate is {} bits?$"{(effective.VideoRateMode==VideoRateMode.Source?"参考源视频码率":"自定义视频码率")}：{bits/1000d:0.###} kbps，峰值受限。":$"按质量编码：质量 {effective.Quality}，不限制输出体积。");
        }
        if(effective.PreserveSourceAttributes && hardware.Count==0)
            hardwareLog.AppendLine(Settings.AutoDetectGpu?"没有可用且能保留源编码、位深与色度采样的 GPU 编码器，使用原编码的软件实现。":"自动 GPU 已关闭，使用原编码的软件实现。");
        var failedDecoders=new HashSet<string>(StringComparer.Ordinal);
        foreach(var codec in hardware)
        {
            ct.ThrowIfCancellationRequested();
            var temporary=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!,".AvaMedia-gpu-"+Guid.NewGuid()+Path.GetExtension(job.Output));
            try
            {
                job.ProgressDetail="GPU 编码 · "+codec;progress(0);
                effective.VideoCodec=codec;effectiveJob.Output=temporary;result=await EncodeWithDecoding(effectiveJob);
                if(result.ExitCode==0)
                {
                    if(!File.Exists(temporary) || new FileInfo(temporary).Length==0)throw new InvalidDataException("编码未生成有效文件。");
                    ct.ThrowIfCancellationRequested();File.Move(temporary,job.Output);hardwareLog.AppendLine("使用硬件编码 "+codec+"（"+HardwareTranscoding.Backend(codec)!.Name+"）。");break;
                }
                else
                {
                    hardwareLog.AppendLine(explicitHardware?$"指定硬件编码 {codec} 失败。":$"自动硬件编码 {codec} 失败，尝试其他可用编码器。").AppendLine(result.Error);
                    job.Log=hardwareLog.ToString();progress(0);
                }
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        if(!explicitHardware && (result is null || result.ExitCode!=0))
        {
            if(hardware.Count>0)hardwareLog.AppendLine("可用硬件编码器均失败，回退软件编码。");
            effective.VideoCodec=effective.PreserveSourceAttributes?SourceVideoExport.Encoder(infos[0],sourceEncoderListing!):job.Options.VideoCodec;
            if(effective.VideoCodec=="自动" && effective.VideoRateMode==VideoRateMode.Source && effective.VideoCompression is null && VideoFormats.OriginalOutputExtensions.Contains(effective.Format))
            {
                var encoders=await ProcessRunner.Run(FFmpeg,["-hide_banner","-encoders"],ct);
                if(encoders.ExitCode!=0)throw new InvalidOperationException("无法读取视频编码器。"+encoders.Error);
                effective.VideoCodec=VideoEncoding.SoftwareEncoder(effective.Format,infos.FirstOrDefault(info=>info.HasVideo)?.VideoCodec??"",encoders.Output+encoders.Error);
            }
            if(effective.VideoCompression is {} fallbackCompression)
            {
                var encoders=await ProcessRunner.Run(FFmpeg,["-hide_banner","-encoders"],ct);
                effective.VideoCodec=VideoCompression.SoftwareEncoder(fallbackCompression.Codec,encoders.Output+encoders.Error,effective.Width,effective.Height);
            }
            job.ProgressDetail=effective.VideoCodec=="自动"?"软件自动编码":"软件编码 · "+effective.VideoCodec;
            if(effective.PreserveSourceAttributes)hardwareLog.AppendLine("使用软件编码 "+effective.VideoCodec+"。");
            // Keep incomplete single-file exports private, including cancellation and software
            // fallback failures. File.Move never overwrites a concurrently created destination.
            var temporary=Catalog.DirectoryOutput(f.Operation)?null:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.Output))!,".AvaMedia-encode-"+Guid.NewGuid()+Path.GetExtension(job.Output));
            try
            {
                progress(0);effectiveJob.Output=temporary??job.Output;result=await Encode(effectiveJob,null);
                if(result.ExitCode==0 && temporary is not null)
                {
                    if(!File.Exists(temporary) || new FileInfo(temporary).Length==0)throw new InvalidDataException("编码未生成有效文件。");
                    ct.ThrowIfCancellationRequested();File.Move(temporary,job.Output);
                }
            }
            finally{if(temporary is not null && File.Exists(temporary))File.Delete(temporary);}
        }
        var completed=result!;
        job.Log=hardwareLog+completed.Error;
        if(completed.ExitCode!=0) throw new InvalidOperationException(completed.Error);
        if(compressionPlan is not null)
        {
            var outputBytes=new FileInfo(job.Output).Length;
            var saved=(1-(double)outputBytes/compressionPlan.SourceBytes)*100;
            job.ProgressDetail=outputBytes<compressionPlan.SourceBytes?$"节省 {saved:0.#}%":"输出未缩小";
            var intent=compressionPlan.QualityDriven?$"质量档 {compressionPlan.Quality}，体积由内容决定":compressionPlan.TargetBytes is {} target?$"目标 {target} B":$"视频码率 {compressionPlan.VideoBitrate} kbps";
            job.AppendLog($"视频压缩：原视频 {compressionPlan.SourceBytes} B，{intent}，实际 {outputBytes} B；节省 {saved:0.#}%。");
            if(compressionColor?.ToneMap==true)job.AppendLog($"{compressionColor.SourceLabel} → SDR（BT.709）：浮点色调映射，输出 8 位；不保留 HDR / Dolby Vision 动态元数据。");
            if(outputBytes<=0)throw new InvalidOperationException("视频压缩未生成有效文件。");
            if(outputBytes>=compressionPlan.SourceBytes)
                throw new InvalidOperationException("未能缩小：输出不小于源视频。请降低目标体积或调整压缩参数；输出文件已保留。");
        }
        progress(100);
        async Task<ProcessResult> EncodeWithDecoding(Job draft)
        {
            var plans=HardwareTranscoding.DecodePlans(draft,infos).Where(plan=>!failedDecoders.Contains(plan.Value.Method)).ToDictionary(plan=>plan.Key,plan=>plan.Value);
            var encoded=await Encode(draft,plans);
            if(encoded.ExitCode!=0 && plans.Count>0)
            {
                foreach(var method in plans.Values.Select(plan=>plan.Method))failedDecoders.Add(method);
                hardwareLog.AppendLine("硬件解码链路失败，保留当前硬件编码器并改用软件解码。").AppendLine(encoded.Error);
                if(File.Exists(draft.Output))File.Delete(draft.Output);
                progress(0);encoded=await Encode(draft,null);
                if(encoded.ExitCode==0)hardwareLog.AppendLine("使用软件解码与硬件编码。");
            }
            else if(encoded.ExitCode==0 && plans.Count>0)
                hardwareLog.AppendLine("已请求硬件解码："+string.Join("、",plans.Values.Select(plan=>plan.Method).Distinct())+"；"+
                    (plans.Values.Any(plan=>plan.EncoderPixelFormat is not null)?"画面直接传入硬件编码器。":"画面下载后使用现有滤镜与硬件编码器。"));
            return encoded;
        }
        Task<ProcessResult> Encode(Job draft,IReadOnlyDictionary<int,HardwareDecodePlan>? decoding)=>ProcessRunner.Run(FFmpeg,BuildArguments(draft,infos,decoding),ct,line=>
        {
            if(line.StartsWith("out_time_us=") && long.TryParse(line[12..],out var us) && job.Duration>0)progress(Math.Clamp(us/1000000d/job.Duration*100,0,99.9));
        });
    }
    public static List<string> BuildArguments(Job job,IReadOnlyList<MediaInfo> infos,IReadOnlyDictionary<int,HardwareDecodePlan>? hardwareDecoding=null)
    {
        if(job.Options.PreserveSourceAttributes || job.Options.LosslessRotation is not null)return SourceVideoGpu.BuildArguments(job,infos);
        var f=Catalog.Find(job.FeatureId);var o=job.Options;List<string> a=["-hide_banner","-nostdin","-n","-progress","pipe:1","-nostats"];
        var hardwareBackend=o.CopyStreams?null:HardwareTranscoding.Backend(o.VideoCodec);
        if(hardwareBackend is not null)a.AddRange(hardwareBackend.InitializationArguments);
        if(f.Operation==Operation.Mux && job.InputOptions?.ElementAtOrDefault(1)?.Mute==true){o=o.Clone();o.Mute=true;}
        if(o.Threads>0)a.AddRange(["-filter_threads",o.Threads.ToString(),"-filter_complex_threads",o.Threads.ToString()]);
        var combined=f.Operation==Operation.AudioMix || f.Operation==Operation.Join && (job.Inputs.Length>1 || job.InputOptions is not null) || f.Operation==Operation.Mux && job.InputOptions is not null;
        var audioCopy=!combined && job.Inputs.Length==1 && IsAudio(o.Format) && (o.CopyStreams || o.AudioCodec=="copy");
        var videoFilters=MediaFilters.Video(o,job.Duration,"out",combined,job.Inputs.FirstOrDefault());var audioFilters=MediaFilters.Audio(o,job.Duration,combined);
        var image=HeifImage.Supports(job.Inputs[0])?HeifImage.Parse(infos[0].RawJson):null;
        if(image?.PreFilter.Length>0)videoFilters.Insert(0,image.PreFilter);
        var compressionColor=o.VideoCompression is not null?VideoCompressionColor.Inspect(infos[0]):null;
        if(compressionColor is not null)videoFilters.InsertRange(0,compressionColor.Filters());
        for(var inputIndex=0;inputIndex<job.Inputs.Length;inputIndex++)
        {
            if(!combined && !audioCopy && o.Start>0)a.AddRange(["-ss",Number(o.Start)]);
            if(o.Threads>0)a.AddRange(["-threads",o.Threads.ToString()]);
            if(hardwareDecoding?.TryGetValue(inputIndex,out var decoding)==true)a.AddRange(decoding.InputArguments);
            if(inputIndex==0 && image?.PreFilter.Length>0)a.Add("-noautorotate");
            a.AddRange(["-i",job.Inputs[inputIndex]]);
        }
        if(SubtitleOptions.Mode(o)==SubtitleMode.ExternalTrack){if(o.Start>0)a.AddRange(["-ss",Number(o.Start)]);a.AddRange(["-i",o.Subtitle]);}
        // Some M4A files mark only sparse audio packets as keyframes. Output seeking with
        // copyinkf keeps the requested audio boundary without waiting for the next such packet.
        if(audioCopy && o.Start>0)a.AddRange(["-ss",Number(o.Start),"-copyinkf"]);
        if(o.End>0) a.AddRange(["-t",Number((o.End-o.Start)/o.Speed)]);
        if(f.Operation==Operation.Join && combined)
        {
            bool audioOnly=IsAudio(o.Format),withAudio=!o.Mute && infos.Where((info,index)=>job.InputOptions?.ElementAtOrDefault(index)?.Mute!=true).Any(info=>info.HasAudio);var graph=new StringBuilder();var parts=new StringBuilder();
            for(int i=0;i<infos.Count;i++)
            {
                if(!audioOnly)
                {
                    if(!infos[i].HasVideo) throw new ArgumentException("视频合并需要所有输入含视频画面。");
                    int w=Math.Max(2,infos[0].Width/2*2),h=Math.Max(2,infos[0].Height/2*2);
                    var edit=job.InputOptions?.ElementAtOrDefault(i)??new ConversionOptions();
                    var vf=MediaFilters.Video(edit,MediaFilters.Duration(infos[i],edit),"i"+i,true,job.Inputs[i]);
                    vf.AddRange([$"scale={w}:{h}:force_original_aspect_ratio=decrease",$"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2","setsar=1","fps="+Number(o.Fps>0?o.Fps:infos[0].FrameRate>0?infos[0].FrameRate:25),"setpts=PTS-STARTPTS"]);
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
                var v=job.InputOptions[0];var vf=MediaFilters.Video(v,MediaFilters.Duration(infos[0],v),"mux",true,job.Inputs[0]);var graph=new StringBuilder();
                if(vf.Count>0 || videoFilters.Count>0){vf.Add("null");graph.Append($"[0:v:{infos[0].VideoStreamIndex}]"+string.Join(",",vf)+"[vout]");a.AddRange(["-map","[vout]"]);}
                else a.AddRange(["-map",$"0:v:{infos[0].VideoStreamIndex}"]);
                if(!o.Mute)
                {
                    using var json=JsonDocument.Parse(infos[1].RawJson);var count=o.KeepAllAudioStreams?json.RootElement.GetProperty("streams").EnumerateArray().Count(s=>s.GetProperty("codec_type").GetString()=="audio"):1;
                    for(int index=0;index<count;index++)
                    {
                        var au=job.InputOptions[1];var af=MediaFilters.Audio(au,MediaFilters.Duration(infos[1],au),true);af.AddRange(audioFilters);var stream=o.KeepAllAudioStreams?index:infos[1].AudioStreamIndex;
                        if(af.Count==0)a.AddRange(["-map",$"1:a:{stream}"]);
                        else{af.Add("anull");var label="[muxaudio"+index+"]";if(graph.Length>0)graph.Append(';');graph.Append($"[1:a:{stream}]"+string.Join(",",af)+label);a.AddRange(["-map",label]);}
                    }
                    audioFilters.Clear();a.Add("-shortest");
                }
                if(graph.Length>0)a.AddRange(["-filter_complex",graph.ToString()]);
            }
            else{a.AddRange(["-map",$"0:v:{infos[0].VideoStreamIndex}"]);if(!o.Mute)a.AddRange(["-map",o.KeepAllAudioStreams?"1:a?":$"1:a:{o.AudioStreamIndex}","-shortest"]);}
        }
        else if(f.Operation==Operation.SplitAudio) a.AddRange(["-map",o.KeepAllAudioStreams?"0:a?":$"0:a:{o.AudioStreamIndex}","-vn"]);
        else if(f.Operation==Operation.SplitVideo) a.AddRange(["-map",$"0:v:{infos[0].VideoStreamIndex}","-an"]);
        else
        {
            if(!IsAudio(o.Format))
            {
                var map=image?.Map() ?? "0:"+MediaStreams.VideoSpecifier(infos[0].VideoStreamIndex)+"?";
                if(map.StartsWith('['))a.AddRange(["-filter_complex",$"{map}null[vout]","-map","[vout]"]);
                else a.AddRange(["-map",map]);
            }
            else a.Add("-vn");
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
        if(o.CopyStreams)a.AddRange(["-c","copy"]);
        else if(f.Operation==Operation.Frames || IsImage(o.Format))
        {
            if(f.Operation!=Operation.Frames)a.AddRange(["-frames:v","1"]);
            if(o.Format=="jpg")a.AddRange(["-q:v",Number(o.ImageQuality is {} jq?2+(100-jq)*29/99d:Math.Clamp(o.Quality/4d,2,12))]);
            if(o.Format=="webp")
            {
                var quality=Number(o.ImageQuality is {} wq?wq:Math.Clamp(100-o.Quality*90d/63,10,100));
                // libwebp uses global_quality when present, overriding its private quality option.
                a.AddRange(["-c:v","libwebp","-lossless","0","-quality",quality,"-q:v",quality]);
            }
            if(o.Format=="avif")a.AddRange(["-c:v","libaom-av1","-still-picture","1","-crf",o.Quality.ToString(),"-cpu-used","6"]);
        }
        else if(o.Format!="gif")
        {
            if(!IsAudio(o.Format))
            {
                string codec=o.VideoCodec=="自动"?o.Format switch {"webm"=>"libvpx-vp9","avi"=>"mpeg4","wmv"=>"wmv2","flv"=>"flv","mpg"=>"mpeg2video","ts" or "mts" or "m2ts" or "m2t"=>"libx264",_=>"mpeg4"}:o.VideoCodec;
                a.AddRange(["-c:v",codec]);if(codec!="copy")a.AddRange(["-pix_fmt",hardwareDecoding?.Values.FirstOrDefault(plan=>plan.EncoderPixelFormat is not null)?.EncoderPixelFormat??(hardwareBackend is null?"yuv420p":"nv12")]);
                if((codec.StartsWith("hevc_",StringComparison.Ordinal) || codec is "libx265" or "libkvazaar") && o.Format is "mp4" or "mov" or "m4v")a.AddRange(["-tag:v","hvc1"]);
                if(o.VideoCompression is not null)a.AddRange(["-metadata:s:v:0","rotate=0"]);
                if(compressionColor?.ToneMap==true)a.AddRange(["-colorspace","bt709","-color_trc","bt709","-color_primaries","bt709","-color_range","tv"]);
                if(o.VideoCompression is not null)a.AddRange(VideoCompression.EncodingArguments(codec,o));
                else a.AddRange(VideoEncoding.EncodingArguments(codec,o,infos,codec=="copy"?null:VideoEncoding.TargetBitrate(job,infos)));
                if(o.Fps>0)a.AddRange(["-r",Number(o.Fps)]);
                else if(!combined && codec!="copy")
                {
                    a.AddRange(["-fps_mode:v:0","vfr"]);
                    if(codec is "libx264" or "libx265" or "libaom-av1" or "libvpx-vp9")a.AddRange(["-enc_time_base:v:0","demux"]);
                }
            }
            string ac=o.AudioCodec=="自动"?o.Format switch {"mp3"=>"libmp3lame","flac"=>"flac","wav"=>"pcm_s16le","aiff"=>"pcm_s16be","ogg"=>"libvorbis","opus" or "webm"=>"libopus","ac3"=>"ac3","wma" or "wmv"=>"wmav2","mpg"=>"mp2",_=>"aac"}:o.AudioCodec;
            if(!o.Mute && f.Operation!=Operation.SplitVideo){a.AddRange(["-c:a",ac]);if(ac!="copy"){if(ac is not ("flac" or "pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm_f32le" or "pcm_s24be" or "alac"))a.AddRange(["-b:a",o.AudioBitrate+"k"]);if(ac=="libopus" || o.SampleRate>0)a.AddRange(["-ar",(ac=="libopus"?48000:o.SampleRate).ToString()]);if(o.AudioChannels>0)a.AddRange(["-ac",o.AudioChannels.ToString()]);}}
            if(!o.Mute && f.Operation!=Operation.SplitVideo && ac is not ("copy" or "flac" or "pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm_f32le" or "pcm_s24be" or "alac"))
            {
                var rates=VideoEncoding.AudioBitrates(job,infos,ac);
                for(var index=0;index<rates.Count;index++)a.AddRange(["-b:a:"+index,rates[index].ToString(CultureInfo.InvariantCulture)]);
            }
            if(o.Format is "mp4" or "mov" or "m4v" or "m4a" or "3gp" or "3g2") a.AddRange(["-movflags","+faststart"]);
        }
        if(o.Threads>0 && !o.CopyStreams)a.AddRange(["-threads",o.Threads.ToString()]);
        SubtitleOptions.Map(a,o,job.Inputs.Length);
        VideoFormats.AppendMuxerArguments(a,o.Format);
        a.Add(f.Operation==Operation.Frames?Path.Combine(job.Output,"frame-%06d.png"):job.Output);return a;
    }
    public static string UniqueOutput(string folder,string name,string format,IEnumerable<string>? reserved=null,bool directory=false)
    {
        Directory.CreateDirectory(folder);var safe=string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));if(string.IsNullOrWhiteSpace(safe))safe="output";
        var set=new HashSet<string>(reserved??[],OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        for(int i=0;;i++) {var path=Path.Combine(folder,safe+(i==0?"":" ("+i+")")+(directory?"":"."+format));if(!File.Exists(path)&&!Directory.Exists(path)&&!set.Contains(path))return path;}
    }
}
