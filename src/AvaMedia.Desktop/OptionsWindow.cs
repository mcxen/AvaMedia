using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;
public enum MediaOptionsKind { Video, Audio, Image, VideoOnly, InputVideo, InputAudio, Frames, ClipEdit, ClipExport }
public sealed class OptionsWindow : Window
{
    private readonly bool? _copyMode;
    private readonly MediaOptionsKind _kind;
    private readonly Storage _presets;
    private readonly string _format;
    private readonly bool _allowAllAudioStreams;
    private readonly int? _imageQualityDefault;
    private readonly List<Action<ConversionOptions>> _readers = [];
    private ConversionOptions _draft;
    public OptionsWindow(ConversionOptions options, bool? copyStreamsMode=null, MediaOptionsKind kind=MediaOptionsKind.Video, Storage? presetStorage=null, bool allowAllAudioStreams=true, int? imageQualityDefault=null)
    {
        Title="输出配置";Width=760;Height=660;MinWidth=550;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _draft=options.Clone();_copyMode=copyStreamsMode;_kind=kind;_format=options.Format;_presets=presetStorage??new();_allowAllAudioStreams=allowAllAudioStreams;_imageQualityDefault=imageQualityDefault;Build();
    }
    public ConversionOptions ReadOptions()
    {
        var result=_draft.Clone();foreach(var read in _readers)read(result);
        if(_copyMode.HasValue)result.CopyStreams=_copyMode.Value;
        // QuickClip switches the outer preset to MP4 when a Copy draft gains processing.
        // Validate the resulting encoding mode, rather than blocking that established transition.
        if(_copyMode==true && (MediaEngine.HasFilters(result) || result.SampleRate>0 || result.AudioChannels>0))result.CopyStreams=false;
        if(_kind is MediaOptionsKind.Image or MediaOptionsKind.Frames or MediaOptionsKind.VideoOnly or MediaOptionsKind.InputAudio or MediaOptionsKind.InputVideo || _format=="gif")result.CopyStreams=false;
        if(result.Speed is <.25 or >4 || result.Quality is <1 or >63 || result.Volume<0 || result.FadeIn<0 || result.FadeOut<0 || result.FrameInterval<=0)throw new ArgumentException("参数超出允许范围。");
        if(_copyMode==true){var validation=result.Clone();validation.CopyStreams=false;if(validation.VideoCodec=="copy")validation.VideoCodec="自动";if(validation.AudioCodec=="copy")validation.AudioCodec="自动";MediaEngine.ValidateEncodingOptions(validation);}
        else MediaEngine.ValidateEncodingOptions(result);
        return result;
    }
    private string Prefix=>_kind+"|"+_format+"|";
    private void Build()
    {
        _readers.Clear();var root=new Grid{RowDefinitions=new("Auto,*,Auto"),Margin=new(20)};
        var top=new Grid{ColumnDefinitions=new("70,*,110"),ColumnSpacing=8,Margin=new(0,0,0,16)};top.Children.Add(Ui.Text("预设"));
        var names=_presets.LoadPresets().Keys.Where(k=>k.StartsWith(Prefix,StringComparison.Ordinal)).Select(k=>k[Prefix.Length..]).Order().ToArray();
        var presets=Ui.Combo(new[]{"自定义"}.Concat(names),"自定义");presets.Name="PresetCombo";Grid.SetColumn(presets,1);top.Children.Add(presets);
        presets.SelectionChanged+=(_,_)=>{if(presets.SelectedItem is string name && name!="自定义" && _presets.LoadPresets().TryGetValue(Prefix+name,out var saved)){_draft=saved.Clone();_draft.Format=_format;Build();}};
        var save=new Button{Content="另存为…"};save.Click+=async(_,_)=>
        {
            try
            {
                var options=ReadOptions();var dialog=new Window{Title="保存预设",Width=380,Height=170,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
                var area=new StackPanel{Margin=new(20),Spacing=12};var name=Ui.Input("我的配置");area.Children.Add(name);var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8};
                buttons.Children.Add(Ui.Button("取消",()=>dialog.Close(null)));buttons.Children.Add(Ui.Button("保存",()=>{if(!string.IsNullOrWhiteSpace(name.Text))dialog.Close(name.Text.Trim());}));area.Children.Add(buttons);dialog.Content=area;
                if(await dialog.ShowDialog<string?>(this) is {} key){_presets.SavePreset(Prefix+key,options);_draft=options;Build();}
            }catch(Exception ex){await Ui.Message(this,"保存失败",ex.Message);}
        };Grid.SetColumn(save,2);top.Children.Add(save);root.Children.Add(top);
        var tabs=new TabControl();Grid.SetRow(tabs,1);root.Children.Add(tabs);
        var audioOnly=_kind is MediaOptionsKind.Audio or MediaOptionsKind.InputAudio;
        var input=_kind is MediaOptionsKind.InputAudio or MediaOptionsKind.InputVideo or MediaOptionsKind.ClipEdit;
        var outputOnly=_kind==MediaOptionsKind.ClipExport;
        var image=_kind==MediaOptionsKind.Image;
        var hasAudio=!image && _kind is not (MediaOptionsKind.VideoOnly or MediaOptionsKind.Frames);
        if(!audioOnly)
        {
            var video=Page(image?"图片":"视频");
            if(!image && !outputOnly && _kind!=MediaOptionsKind.ClipEdit)Number(video,"视频轨索引 (从 0 开始)",_draft.VideoStreamIndex,(o,v)=>o.VideoStreamIndex=(int)v,true,0,255);
            if(!image && !input && _kind!=MediaOptionsKind.Frames && _format!="gif")Choice(video,"视频编码器",["copy",..VideoCodecs(_format)],_copyMode==true?"copy":_draft.VideoCodec,(o,v)=>o.VideoCodec=v,_copyMode!=true);
            if(_kind!=MediaOptionsKind.ClipEdit)
            {
                Number(video,"画面宽度 (0 = 原始)",_draft.Width,(o,v)=>o.Width=(int)v,true,0,_format=="ico"?256:32768);
                Number(video,"画面高度 (0 = 原始)",_draft.Height,(o,v)=>o.Height=(int)v,true,0,_format=="ico"?256:32768);
                if(!image && _kind!=MediaOptionsKind.Frames)Number(video,"帧率 (0 = 原始)",_draft.Fps,(o,v)=>o.Fps=v,false,0,240);
            }
            if(image && _format is "jpg" or "webp")
            {
                var legacy=_draft.ImageQuality is null && _draft.Quality!=23;
                var quality=_draft.ImageQuality??(legacy?(int)Math.Round(_format=="jpg"?100-(Math.Clamp(_draft.Quality/4d,2,12)-2)*99/29d:Math.Clamp(100-_draft.Quality*90d/63,10,100)):_imageQualityDefault??90);
                Number(video,_format=="jpg"?"JPG Quality (1 – 100)":"WebP Quality (1 – 100)",quality,(o,v)=>o.ImageQuality=legacy && v==quality?null:(int)v,true,1,100);
            }
            else if(!input && _kind!=MediaOptionsKind.Frames && (image?_format=="avif":_format!="gif"))Number(video,"质量 (值越低质量越高)",_draft.Quality,(o,v)=>o.Quality=(int)v,true,1,63);
            if(!outputOnly)
            {
                Choice(video,"旋转角度",["0","90","180","270"],(_draft.LosslessRotation??_draft.Rotation).ToString(),(o,v)=>
                {if(o.LosslessRotation is not null){o.LosslessRotation=int.Parse(v);o.Rotation=0;}else o.Rotation=int.Parse(v);});Check(video,"镜像","水平镜像",_draft.Flip,(o,v)=>o.Flip=v);
                if(!image && _kind!=MediaOptionsKind.Frames){Number(video,"速度 (0.25 – 4)",_draft.Speed,(o,v)=>o.Speed=v,false,.25,4);Fades(video);}
            }
        }
        if(hasAudio && _format!="gif")
        {
            var audio=Page("音频");
            if(!outputOnly && _kind!=MediaOptionsKind.ClipEdit)Number(audio,"音频轨索引 (从 0 开始)",_draft.AudioStreamIndex,(o,v)=>o.AudioStreamIndex=(int)v,true,0,255);
            if(!input)
            {
                Choice(audio,"音频编码器",["copy",..AudioCodecs(_format)],_copyMode==true?"copy":_draft.AudioCodec,(o,v)=>o.AudioCodec=v,_copyMode!=true);
                Number(audio,"音频码率 (kbps)",_draft.AudioBitrate,(o,v)=>o.AudioBitrate=(int)v,true,16,1536);
                Choice(audio,"音频采样率",_format is "opus" or "webm"?["默认","48000"]:_format is "mpg"?["默认","16000","22050","24000","32000","44100","48000"]:_format is "mp3" or "wma" or "wmv"?["默认","8000","11025","16000","22050","32000","44100","48000"]:["默认","8000","11025","16000","22050","32000","44100","48000","88200","96000"],_draft.SampleRate==0?"默认":_draft.SampleRate.ToString(),(o,v)=>o.SampleRate=v=="默认"?0:int.Parse(v));
                Choice(audio,"声道",_format is "mp3" or "wma" or "wmv"?["默认","1","2"]:["默认","1","2","6"],_draft.AudioChannels==0?"默认":_draft.AudioChannels.ToString(),(o,v)=>o.AudioChannels=v=="默认"?0:int.Parse(v));
            }
            if(!outputOnly)Number(audio,"音量 (%)",_draft.Volume*100,(o,v)=>o.Volume=v/100,false,0,1000);
            if(!audioOnly && !outputOnly)Check(audio,"音频","禁用音频",_draft.Mute,(o,v)=>o.Mute=v);
            if(!input && _allowAllAudioStreams && (VideoFormats.IsTransportStream(_format) || _format is "mp4" or "mkv" or "mov" or "m4a" or "m4v" or "webm" or "avi" or "ogg" or "3gp" or "3g2"))Check(audio,"保留所有源输入流 (音频)","保留全部音轨",_draft.KeepAllAudioStreams,(o,v)=>o.KeepAllAudioStreams=v);
            if(!outputOnly)
            {
            if(audioOnly)Number(audio,"速度 (0.25 – 4)",_draft.Speed,(o,v)=>o.Speed=v,false,.25,4);
            Number(audio,"音频淡入时长 (秒)",_draft.AudioFadeIn??_draft.FadeIn,(o,v)=>o.AudioFadeIn=v,false,0,86400);
            Number(audio,"音频淡出时长 (秒)",_draft.AudioFadeOut??_draft.FadeOut,(o,v)=>o.AudioFadeOut=v,false,0,86400);
            Check(audio,"回声","启用回声",_draft.Echo,(o,v)=>o.Echo=v);
            Check(audio,"降噪","启用降噪",_draft.NoiseReduction,(o,v)=>o.NoiseReduction=v);
            Check(audio,"反向","反向播放所选音频",_draft.ReverseAudio,(o,v)=>o.ReverseAudio=v);
            audio.Children.Add(new TextBlock{Text="音频效果在输出时生效。反向处理会先缓存所选音频区间。",TextWrapping=Avalonia.Media.TextWrapping.Wrap,Classes={"caption"}});
            }
        }
        if(!audioOnly && !image && _kind!=MediaOptionsKind.Frames && !outputOnly)
        {
            var subtitle=Page("字幕");
            string[] modes=input?["关闭","烧录到画面"]:["关闭","烧录到画面","保留源字幕轨","附加外部字幕轨"];
            var mode=Ui.Combo(modes,SubtitleOptions.Mode(_draft) switch{SubtitleMode.BurnIn=>modes[1],SubtitleMode.Preserve when !input=>modes[2],SubtitleMode.ExternalTrack when !input=>modes[3],_=>modes[0]});mode.Name="SubtitleModeCombo";Add(subtitle,"字幕处理",mode);
            _readers.Add(o=>o.SubtitleMode=mode.SelectedIndex switch{1=>SubtitleMode.BurnIn,2=>SubtitleMode.Preserve,3=>SubtitleMode.ExternalTrack,_=>SubtitleMode.None});
            var box=Ui.Input(_draft.Subtitle);box.Name="SubtitlePath";var row=new Grid{ColumnDefinitions=new("*,80"),ColumnSpacing=8};row.Children.Add(box);
            var browse=new Button{Content="浏览…"};browse.Click+=async(_,_)=>{if((await Ui.Pick(this,"选择字幕文件",false)).FirstOrDefault() is {} path){box.Text=path;if(mode.SelectedIndex==0)mode.SelectedIndex=1;}};Grid.SetColumn(browse,1);row.Children.Add(browse);Add(subtitle,"外部字幕文件",row);_readers.Add(o=>o.Subtitle=box.Text?.Trim()??"");
            Number(subtitle,"字幕轨索引 (-1 = 默认/全部)",_draft.SubtitleStreamIndex,(o,v)=>o.SubtitleStreamIndex=(int)v,true,-1,255);
            var language=Ui.Input(_draft.SubtitleLanguage);language.Name="SubtitleLanguage";Add(subtitle,"轨道语言 (如 zho、eng)",language);_readers.Add(o=>o.SubtitleLanguage=language.Text?.Trim()??"");
            var font=Ui.Input(_draft.SubtitleFont);font.Name="SubtitleFont";Add(subtitle,"烧录字体 (留空 = 自动)",font);_readers.Add(o=>o.SubtitleFont=font.Text?.Trim()??"");
            Number(subtitle,"烧录字号 (0 = 字幕默认)",_draft.SubtitleFontSize,(o,v)=>o.SubtitleFontSize=(int)v,true,0,200);
            var color=Ui.Input(_draft.SubtitleColor);color.Name="SubtitleColor";Add(subtitle,"烧录颜色 (#RRGGBB)",color);_readers.Add(o=>o.SubtitleColor=color.Text?.Trim()??"#FFFFFF");
            Choice(subtitle,"烧录位置",["左下","中下","右下","左中","居中","右中","左上","中上","右上"],new[]{"左下","中下","右下","左中","居中","右中","左上","中上","右上"}[_draft.SubtitleAlignment-1],(o,v)=>o.SubtitleAlignment=Array.IndexOf(new[]{"左下","中下","右下","左中","居中","右中","左上","中上","右上"},v)+1);
            Number(subtitle,"烧录垂直边距",_draft.SubtitleMargin,(o,v)=>o.SubtitleMargin=(int)v,true,0,2000);
            subtitle.Children.Add(new TextBlock{Text="烧录时外部文件留空可使用源字幕；-1 选择第一条。保留/附加时 -1 表示全部字幕轨。字体需已安装，样式仅用于文本字幕烧录。",TextWrapping=Avalonia.Media.TextWrapping.Wrap,Classes={"caption"}});
        }
        if(!input)
        {
            var other=Page("其他");
            if(!image && _format!="gif" && _kind is not (MediaOptionsKind.Frames or MediaOptionsKind.VideoOnly))Check(other,"编码方式",_copyMode.HasValue?"流复制 (由输出格式决定)":"流复制 (不重新编码)",_copyMode??_draft.CopyStreams,(o,v)=>o.CopyStreams=v,!_copyMode.HasValue);
            Check(other,"元数据","保留元数据",_draft.KeepMetadata,(o,v)=>o.KeepMetadata=v);
            if(_kind==MediaOptionsKind.Frames)Number(other,"导出帧间隔 (秒)",_draft.FrameInterval,(o,v)=>o.FrameInterval=v,false,.01,86400);
        }
        if(!audioOnly && !outputOnly)
        {
            var watermark=Page("水印");
            Number(watermark,"区域 X",_draft.DelogoX,(o,v)=>o.DelogoX=(int)v,true,0,32768);Number(watermark,"区域 Y",_draft.DelogoY,(o,v)=>o.DelogoY=(int)v,true,0,32768);
            Number(watermark,"区域宽度 (0 = 关闭)",_draft.DelogoWidth,(o,v)=>o.DelogoWidth=(int)v,true,0,32768);Number(watermark,"区域高度 (0 = 关闭)",_draft.DelogoHeight,(o,v)=>o.DelogoHeight=(int)v,true,0,32768);
        }
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=12,Margin=new(0,16,0,0)};
        footer.Children.Add(Ui.DialogButton("默认",()=>{_draft=new(){Format=_format};Build();}));footer.Children.Add(Ui.DialogButton("取消",()=>Close(null)));
        var ok=new Button{Content="确定",Classes={"dialog-action"}};ok.Click+=async(_,_)=>{try{Close(ReadOptions());}catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}};footer.Children.Add(ok);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        StackPanel Page(string title){var panel=new StackPanel{Spacing=12,Margin=new(16)};tabs.Items.Add(new TabItem{Header=title,Content=new ScrollViewer{Content=panel}});return panel;}
        void Fades(StackPanel panel){Number(panel,"淡入时长 (秒)",_draft.FadeIn,(o,v)=>o.FadeIn=v,false,0,86400);Number(panel,"淡出时长 (秒)",_draft.FadeOut,(o,v)=>o.FadeOut=v,false,0,86400);}
    }
    private static string[] AudioCodecs(string format)=>format switch
    {
        "mp3"=>["自动","libmp3lame"],"flac"=>["自动","flac"],"wav"=>["自动","pcm_s16le","pcm_s24le","pcm_f32le"],"aiff"=>["自动","pcm_s16be","pcm_s24be"],
        "ogg"=>["自动","libvorbis"],"opus"=>["自动","libopus"],"webm"=>["自动","libopus","libvorbis"],"aac"=>["自动","aac"],"m4a"=>["自动","aac","alac"],"ac3"=>["自动","ac3"],"wma" or "wmv"=>["自动","wmav2"],"mpg"=>["自动","mp2"],
        "3gp" or "3g2"=>["自动","aac"],"flv"=>["自动","aac","libmp3lame"],"avi"=>["自动","aac","libmp3lame","pcm_s16le"],"ts" or "mts" or "m2ts" or "m2t"=>["自动","aac","mp2","libmp3lame","ac3","eac3"],"mkv"=>["自动","aac","libmp3lame","flac","libvorbis","libopus","pcm_s16le"],_=>["自动","aac","libmp3lame","alac"]
    };
    private static string[] VideoCodecs(string format)
    {
        string[] software=format switch{"ts" or "mts" or "m2ts" or "m2t"=>["自动","libx264","libx265","mpeg2video","mpeg4","h264_mf"],"3gp" or "3g2"=>["自动","mpeg4","h264_mf","libx264"],"webm"=>["自动","libvpx-vp9","libaom-av1"],"wmv"=>["自动","wmv2"],"mpg"=>["自动","mpeg2video"],"flv"=>["自动","flv","h264_mf","libx264"],"avi"=>["自动","mpeg4","h264_mf","libx264"],_=>["自动","mpeg4","h264_mf","libvpx-vp9","libaom-av1","libx264","libx265"]};
        return [..software,..HardwareAcceleration.CompatibleCodecs(format)];
    }
    private static void Add(Panel panel,string label,Control control){var row=new Grid{ColumnDefinitions=new("230,*"),ColumnSpacing=12};row.Children.Add(Ui.Text(label));Grid.SetColumn(control,1);row.Children.Add(control);panel.Children.Add(row);}
    private void Number(Panel panel,string label,double value,Action<ConversionOptions,double> set,bool integer,double min,double max)
    {
        var box=Ui.Input(MediaEngine.Number(value));if(label=="音量 (%)")box.Name="VolumePercent";if(label=="音频淡入时长 (秒)")box.Name="AudioFadeInInput";if(label=="音频淡出时长 (秒)")box.Name="AudioFadeOutInput";Add(panel,label,box);_readers.Add(o=>{var number=double.Parse(box.Text??"",CultureInfo.InvariantCulture);if(!double.IsFinite(number)||number<min||number>max||integer&&number!=Math.Truncate(number))throw new ArgumentException(integer ? Localization.Format($"{Localization.Key(label)}：请输入有效整数。") : Localization.Format($"{Localization.Key(label)}：请输入有效数值。"));set(o,number);});
        if(label.StartsWith("视频轨索引"))box.Name="VideoStreamIndex";if(label.StartsWith("音频轨索引"))box.Name="AudioStreamIndex";if(label.StartsWith("字幕轨索引"))box.Name="SubtitleStreamIndex";if(label.StartsWith("烧录字号"))box.Name="SubtitleFontSize";if(label.StartsWith("JPG Quality") || label.StartsWith("WebP Quality"))box.Name="ImageQualityInput";
    }
    private void Choice(Panel panel,string label,string[] items,string value,Action<ConversionOptions,string> set,bool enabled=true)
    {
        var control=Ui.Combo(items,value);control.IsEnabled=enabled;
        if(label=="视频编码器")control.Name="VideoCodecCombo";if(label=="音频编码器")control.Name="AudioCodecCombo";if(label=="音频采样率")control.Name="AudioSampleRateCombo";if(label=="声道")control.Name="AudioChannelsCombo";
        Add(panel,label,control);if(enabled)_readers.Add(o=>set(o,(string)control.SelectedItem!));
    }
    private void Check(Panel panel,string label,string text,bool value,Action<ConversionOptions,bool> set,bool enabled=true){var check=new CheckBox{Content=text,IsChecked=value,IsEnabled=enabled};Add(panel,label,check);_readers.Add(o=>set(o,check.IsChecked==true));}
}
