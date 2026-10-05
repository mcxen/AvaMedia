using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;
public partial class EditorWindow : Window
{
    private readonly MediaEngine _engine;
    private readonly string _path;
    private ConversionOptions _options;
    private MediaInfo? _info;
    private readonly Playback _player;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _seek;
    private CancellationTokenSource? _thumbs;
    private readonly TaskCompletionSource _ready=new();
    private bool _updating;
    private bool _closed;
    private bool _regionDelogo;
    private bool _playBusy;
    private double _position;
    private readonly string _mode;
    private double[] _speedValues=[],_fadeInValues=[],_fadeOutValues=[];
    private Task _audioReady=Task.CompletedTask;
    private bool AudioEditing=>MediaEngine.IsAudio(_options.Format)||_mode=="input-audio";
    public Task Ready=>_ready.Task;
    internal async Task<object> VerifyPreview()
    {
        await Ready;
        if(_info?.HasVideo!=true || PreviewImage.Source is null || StartImage.Source is null || EndImage.Source is null)throw new InvalidOperationException("视频及边界帧预览没有就绪。");
        SeekBar.Value=.4;await Task.Delay(400);SetStartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        SeekBar.Value=Math.Min(2.4,_info.Duration);await Task.Delay(400);SetEndButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if(Math.Abs(_options.Start-.4)>.01 || Math.Abs(_options.End-Math.Min(2.4,_info.Duration))>.01)throw new InvalidOperationException("播放位置没有正确写入剪辑区间。");
        EditTabs.SelectedIndex=1;if(!CropLayer.Enabled)throw new InvalidOperationException("裁剪选区没有启用。");EditTabs.SelectedIndex=0;
        SeekBar.Value=.1;await Task.Delay(300);PlayButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await Task.Delay(1000);
        await _player.Stop();PlayButton.Content="▶";if(_player.DecodedFrames<2)throw new InvalidOperationException("播放器没有解码视频帧。");
        return new{source=_path,width=_info.Width,height=_info.Height,decodedFrames=_player.DecodedFrames,trimStart=_options.Start,trimEnd=_options.End,thumbnails=true,cropOverlay=true};
    }
    public EditorWindow() : this(new MediaEngine(new()),"",new()) { }
    public EditorWindow(MediaEngine engine,string path,ConversionOptions options,string mode="")
    {
        InitializeComponent();_engine=engine;_path=path;_mode=mode;_options=options.Clone();_player=new(engine,path);Title=path;RefreshOptionControls();PlayButton.IsEnabled=false;SoundButton.IsEnabled=false;
        PrecisionCombo.ItemsSource=new[]{"0.01 s","0.1 s","1 s"};PrecisionCombo.SelectedIndex=1;
        TrimBar.Changed+=(s,e)=>{_options.Start=s;_options.End=e;UpdateTimes();ScheduleThumbs();};
        SeekBar.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty && !_updating && _info is not null)Seek(SeekBar.Value);};
        _player.Updated+=p=>{if(_closed)return;_position=p;_updating=true;SeekBar.Value=Math.Min(p,SeekBar.Maximum);_updating=false;CurrentTime.Text=ShortTime(p);PreviewImage.Source=_player.Frame;PreviewImage.InvalidateVisual();};
        _player.Finished+=async()=>{if(_closed)return;await _player.Stop();PlayButton.Content="▶";_position=_info?.Duration??0;_updating=true;SeekBar.Value=_position;_updating=false;CurrentTime.Text=ShortTime(_position);};
        _player.Error+=message=>{if(!_closed){PreviewStatus.Text=message;PreviewStatus.IsVisible=true;}};
        CropLayer.Changed+=rect=>{CropX.Text=((int)rect.X).ToString();CropY.Text=((int)rect.Y).ToString();CropWidth.Text=((int)rect.Width/2*2).ToString();CropHeight.Text=((int)rect.Height/2*2).ToString();};
        Opened+=async(_,_)=>{await Load();if(mode is "crop" or "delogo"){EditTabs.SelectedIndex=1;if(mode=="delogo")DelogoMode.IsChecked=true;}};
        Closed+=(_,_)=>{_closed=true;_lifetime.Cancel();_seek?.Cancel();_thumbs?.Cancel();_player.Dispose();foreach(var image in new[]{PreviewImage,StartImage,EndImage})if(image.Source is Bitmap b && b!=_player.Frame)b.Dispose();_lifetime.Dispose();};
    }
    private async Task Load()
    {
        try
        {
            _info=await _engine.Probe(_path,_lifetime.Token,_options.VideoStreamIndex,_options.AudioStreamIndex);if(_closed)return;_player.SetStreams(_options.VideoStreamIndex,_options.AudioStreamIndex);if(_info.HasVideo)_player.SetVideoSize(_info.Width,_info.Height);TrimBar.Duration=Math.Max(.01,_info.Duration);TrimBar.Start=_options.Start;TrimBar.End=_options.End>0?Math.Min(_options.End,TrimBar.Duration):TrimBar.Duration;_options.End=TrimBar.End;SeekBar.Maximum=Math.Max(.01,_info.Duration);TotalTime.Text=ShortTime(_info.Duration);UpdateTimes();
            CropLayer.SourceWidth=Math.Max(1,_info.Width);CropLayer.SourceHeight=Math.Max(1,_info.Height);
            CropX.Text=_options.CropX.ToString();CropY.Text=_options.CropY.ToString();CropWidth.Text=(_options.CropWidth>0?_options.CropWidth:_info.Width).ToString();CropHeight.Text=(_options.CropHeight>0?_options.CropHeight:_info.Height).ToString();UpdateCropLayer();
            MediaDescription.Text=$"{Path.GetFileName(_path)}\n时长: {TimeText(_info.Duration)}    画面: {_info.Width} × {_info.Height}\n视频: {_info.VideoCodec}    音频: {_info.AudioCodec}";
            if(_info.HasVideo){await Frame(PreviewImage,_options.Start,_lifetime.Token);await Thumbnails(_lifetime.Token);PreviewStatus.IsVisible=false;}else PreviewStatus.Text="♫ 音频预览";
            SoundButton.IsEnabled=_info.HasAudio;PlayButton.IsEnabled=_info.Duration>0;((TabItem)EditTabs.Items[0]!).IsVisible=_info.Duration>0;((TabItem)EditTabs.Items[1]!).IsVisible=_info.HasVideo&&!AudioEditing;if(_info.Duration<=0)EditTabs.SelectedIndex=1;
            if(_info.HasAudio)_audioReady=PrepareAudio();else AudioStatus.Text="此文件不含音轨";
            _ready.TrySetResult();await _audioReady;
            async Task PrepareAudio()
            {
                try{await _player.PrepareAudio(_lifetime.Token);if(!_closed)AudioStatus.Text="音频预览已就绪";}catch(OperationCanceledException){}catch(Exception e){if(!_closed)AudioStatus.Text="音频预览不可用: "+e.Message;}
            }
        }
        catch(OperationCanceledException){_ready.TrySetResult();}
        catch(Exception e){PreviewStatus.Text="媒体读取失败："+e.Message;_ready.TrySetResult();if(!_closed)await Ui.Message(this,"媒体读取失败",e.Message);}
    }
    private async Task Frame(Image target,double seconds,CancellationToken ct)
    {
        if(_info?.HasVideo!=true)return;var data=await _engine.Thumbnail(_path,Math.Max(0,Math.Min(seconds,_info.Duration-.08)),960,540,ct,pad:false,videoStreamIndex:_options.VideoStreamIndex);ct.ThrowIfCancellationRequested();if(_closed)return;
        var old=target.Source as Bitmap;target.Source=new Bitmap(new MemoryStream(data));if(old!=_player.Frame)old?.Dispose();
    }
    private void Seek(double seconds)
    {
        _position=seconds;CurrentTime.Text=ShortTime(seconds);_seek?.Cancel();_seek=new();var token=_seek.Token;
        async Task Run()
        {
            try{await _player.Stop();PlayButton.Content="▶";await Task.Delay(120,token);await Frame(PreviewImage,seconds,token);}catch(OperationCanceledException){}catch(Exception e){if(!_closed)PreviewStatus.Text=e.Message;}
        }
        _=Run();
    }
    private async Task Thumbnails(CancellationToken ct)
    {if(_info?.HasVideo==true){await Frame(StartImage,_options.Start,ct);await Frame(EndImage,_options.End,ct);}}
    private void ScheduleThumbs(){_thumbs?.Cancel();_thumbs=new();var token=_thumbs.Token;async Task Run(){try{await Task.Delay(180,token);await Thumbnails(token);}catch(OperationCanceledException){}catch(Exception){}}_=Run();}
    private void UpdateTimes(){StartTime.Text=TimeText(_options.Start);EndTime.Text=TimeText(_options.End);SelectionDuration.Text=ShortTime(Math.Max(0,_options.End-_options.Start));TrimBar.Start=_options.Start;TrimBar.End=_options.End;TrimBar.InvalidateVisual();}
    private static string ShortTime(double s)=>TimeSpan.FromSeconds(Math.Max(0,s)).ToString(s>=3600?@"hh\:mm\:ss":@"mm\:ss");
    private static string TimeText(double s)=>TimeSpan.FromSeconds(Math.Max(0,s)).ToString(@"hh\:mm\:ss\.fff");
    private double Precision=>PrecisionCombo.SelectedIndex switch{0=>.01,2=>1,_=>.1};
    private void ShiftStart(double delta){if(_info is null)return;_options.Start=Math.Clamp(_options.Start+delta,0,Math.Max(0,_options.End-.01));UpdateTimes();ScheduleThumbs();}
    private void ShiftEnd(double delta){if(_info is null)return;_options.End=Math.Clamp(_options.End+delta,_options.Start+.01,Math.Max(_options.Start+.01,_info.Duration));UpdateTimes();ScheduleThumbs();}
    private void StartMinus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftStart(-Precision);
    private void StartPlus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftStart(Precision);
    private void EndMinus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftEnd(-Precision);
    private void EndPlus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftEnd(Precision);
    private void SetStartClick(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftStart(_position-_options.Start);
    private void SetEndClick(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftEnd(_position-_options.End);
    private void TimeEdited(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {if(_info is null)return;if(TimeSpan.TryParse(StartTime.Text,CultureInfo.InvariantCulture,out var start)&&TimeSpan.TryParse(EndTime.Text,CultureInfo.InvariantCulture,out var end)&&start.TotalSeconds>=0 && end>start && end.TotalSeconds<=_info.Duration+.001){_options.Start=start.TotalSeconds;_options.End=end.TotalSeconds;UpdateTimes();ScheduleThumbs();}}
    private async void PlayClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {if(_info is null || _playBusy)return;_playBusy=true;try{if(_player.IsPlaying){await _player.Stop();PlayButton.Content="▶";return;}_seek?.Cancel();await _audioReady;if(_closed)return;if(_position>=_info.Duration-.04)_position=0;await _player.Play(_position,_info.HasVideo);if(_info.HasVideo)PreviewImage.Source=_player.Frame;PlayButton.Content="Ⅱ";}finally{_playBusy=false;}}
    private async void StopClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){await _player.Stop();PlayButton.Content="▶";SeekBar.Value=0;Seek(0);}
    private void BackwardClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SeekBar.Value=Math.Max(0,SeekBar.Value-Precision);
    private void ForwardClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SeekBar.Value=Math.Min(SeekBar.Maximum,SeekBar.Value+Precision);
    private void SoundClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){_player.Muted=!_player.Muted;SoundButton.Content=_player.Muted?"♪ ×":"♫";}
    private void TabChanged(object? sender,SelectionChangedEventArgs e){if(CropLayer is null)return;CropLayer.Enabled=EditTabs.SelectedIndex==1;CropLayer.InvalidateVisual();}
    private void DelogoChanged(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(_info is null)return;
        if(int.TryParse(CropX.Text,out var x)&&int.TryParse(CropY.Text,out var y)&&int.TryParse(CropWidth.Text,out var w)&&int.TryParse(CropHeight.Text,out var h))
        {if(_regionDelogo){_options.DelogoX=x;_options.DelogoY=y;_options.DelogoWidth=w;_options.DelogoHeight=h;}else{_options.CropX=x;_options.CropY=y;_options.CropWidth=x==0&&y==0&&w==_info.Width&&h==_info.Height?0:w;_options.CropHeight=_options.CropWidth==0?0:h;}}
        _regionDelogo=DelogoMode.IsChecked==true;LoadRegion();
    }
    private void LoadRegion()
    {
        if(_info is null)return;
        CropX.Text=(_regionDelogo?_options.DelogoX:_options.CropX).ToString();CropY.Text=(_regionDelogo?_options.DelogoY:_options.CropY).ToString();
        CropWidth.Text=(_regionDelogo?_options.DelogoWidth:_options.CropWidth>0?_options.CropWidth:_info.Width).ToString();CropHeight.Text=(_regionDelogo?_options.DelogoHeight:_options.CropHeight>0?_options.CropHeight:_info.Height).ToString();UpdateCropLayer();
    }
    private void UpdateCropLayer(){if(int.TryParse(CropX.Text,out var x)&&int.TryParse(CropY.Text,out var y)&&int.TryParse(CropWidth.Text,out var w)&&int.TryParse(CropHeight.Text,out var h)){CropLayer.Selection=new Rect(Math.Max(0,x),Math.Max(0,y),Math.Max(0,w),Math.Max(0,h));CropLayer.InvalidateVisual();}}
    private void ApplyCropClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>UpdateCropLayer();
    private void ResetCropClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(_info is null)return;if(_regionDelogo){_options.DelogoX=_options.DelogoY=_options.DelogoWidth=_options.DelogoHeight=0;}else{_options.CropX=_options.CropY=_options.CropWidth=_options.CropHeight=0;}LoadRegion();}
    private async void OptionsClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var draft=ReadDraft();var kind=_mode is "input" or "input-audio"?(_info?.HasVideo==true&&!AudioEditing?MediaOptionsKind.InputVideo:MediaOptionsKind.InputAudio):_mode=="frames"?MediaOptionsKind.Frames:MediaEngine.IsAudio(draft.Format)?MediaOptionsKind.Audio:MediaEngine.IsImage(draft.Format)?MediaOptionsKind.Image:MediaOptionsKind.Video;
            var result=await new OptionsWindow(draft,kind:kind).ShowDialog<ConversionOptions?>(this);
            if(result is not null){var tracksChanged=result.VideoStreamIndex!=_options.VideoStreamIndex || result.AudioStreamIndex!=_options.AudioStreamIndex;_options=result;if(tracksChanged){await _player.Stop();await _audioReady;await Load();}if(_info is not null && _options.End==0)_options.End=_info.Duration;RefreshOptionControls();UpdateTimes();LoadRegion();}
        }catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
    }
    private void RefreshOptionControls()
    {
        _speedValues=new[]{1,.25,.5,.75,1.25,1.5,2,4}.Append(_options.Speed).Distinct().ToArray();SpeedCombo.ItemsSource=_speedValues.Select(v=>v==1?"默认":MediaEngine.Number(v)+"×").ToArray();SpeedCombo.SelectedIndex=Array.IndexOf(_speedValues,_options.Speed);
        var fadeIn=AudioEditing?_options.AudioFadeIn??_options.FadeIn:_options.FadeIn;var fadeOut=AudioEditing?_options.AudioFadeOut??_options.FadeOut:_options.FadeOut;
        _fadeInValues=new[]{0,.5,1,2,3}.Append(fadeIn).Distinct().ToArray();_fadeOutValues=new[]{0,.5,1,2,3}.Append(fadeOut).Distinct().ToArray();FadeInCombo.ItemsSource=_fadeInValues.Select(v=>v==0?"关闭":MediaEngine.Number(v)+" s").ToArray();FadeOutCombo.ItemsSource=_fadeOutValues.Select(v=>v==0?"关闭":MediaEngine.Number(v)+" s").ToArray();FadeInCombo.SelectedIndex=Array.IndexOf(_fadeInValues,fadeIn);FadeOutCombo.SelectedIndex=Array.IndexOf(_fadeOutValues,fadeOut);
    }
    public ConversionOptions ReadDraft()
    {
        if(_info is null)throw new InvalidOperationException("媒体尚未就绪。");var draft=_options.Clone();
        if(_info.Duration>0){if(!TimeSpan.TryParse(StartTime.Text,CultureInfo.InvariantCulture,out var start)||!TimeSpan.TryParse(EndTime.Text,CultureInfo.InvariantCulture,out var end)||start.TotalSeconds<0||end<=start||end.TotalSeconds>_info.Duration+.001)throw new ArgumentException("开始和结束时间无效。");draft.Start=start.TotalSeconds;draft.End=end.TotalSeconds>=_info.Duration-.001?0:end.TotalSeconds;}
        else{draft.Start=0;draft.End=0;}
        draft.Speed=_speedValues[Math.Max(0,SpeedCombo.SelectedIndex)];var fadeIn=_fadeInValues[Math.Max(0,FadeInCombo.SelectedIndex)];var fadeOut=_fadeOutValues[Math.Max(0,FadeOutCombo.SelectedIndex)];if(AudioEditing){draft.AudioFadeIn=fadeIn;draft.AudioFadeOut=fadeOut;}else{draft.FadeIn=fadeIn;draft.FadeOut=fadeOut;}
        if(_info.HasVideo&&!AudioEditing)
        {
            int x=int.Parse(CropX.Text??"0"),y=int.Parse(CropY.Text??"0"),w=int.Parse(CropWidth.Text??"0"),h=int.Parse(CropHeight.Text??"0");
            if(DelogoMode.IsChecked==true){draft.DelogoX=x;draft.DelogoY=y;draft.DelogoWidth=w;draft.DelogoHeight=h;}
            else{draft.CropX=x;draft.CropY=y;draft.CropWidth=w;draft.CropHeight=h;if(x==0&&y==0&&w==_info.Width&&h==_info.Height){draft.CropWidth=0;draft.CropHeight=0;}}
        }
        return draft;
    }
    private void CancelClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>Close(null);
    private async void ConfirmClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var draft=ReadDraft();MediaEngine.Validate(new(){FeatureId="mp4",Inputs=[_path],Output=Path.Combine(Path.GetTempPath(),"validate-output.mp4"),Options=draft});Close(draft);
        }
        catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
    }
}
