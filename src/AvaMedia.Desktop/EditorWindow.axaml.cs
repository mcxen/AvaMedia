using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;
public partial class EditorWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly IMediaPreview _previewFrames;
    private readonly string _path;
    private ConversionOptions _options;
    private MediaInfo? _info;
    private readonly Playback _player;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _seek;
    private CancellationTokenSource? _thumbs;
    private CancellationTokenSource? _frameStep;
    private readonly TaskCompletionSource _ready=new();
    private bool _updating;
    private bool _closed;
    private bool _regionDelogo;
    private bool _playBusy;
    private double? _previewEnd;
    private bool _stoppingSelection;
    private int _previewRevision;
    private int _thumbnailRevision;
    private int _playRevision=-1;
    private Task _previewReady=Task.CompletedTask;
    private Task _playbackReady=Task.CompletedTask;
    private Task _thumbnailsReady=Task.CompletedTask;
    private Task _positionReady=Task.CompletedTask;
    private double _position;
    private readonly string _mode;
    private double[] _speedValues=[],_fadeInValues=[],_fadeOutValues=[];
    private Task _audioReady=Task.CompletedTask;
    private bool AudioEditing=>MediaEngine.IsAudio(_options.Format)||_mode=="input-audio";
    public Task Ready=>_ready.Task;
    public Task ThumbnailsReady=>_thumbnailsReady;
    public Task PositionReady=>_positionReady;
    public Task PreviewReady=>_previewReady;
    public Task PlaybackReady=>_playbackReady;
    public bool IsPreviewPlaying=>_player.IsPlaying;
    internal async Task<object> VerifyPreview()
    {
        await Ready;
        if(_info?.HasVideo!=true || PreviewImage.Source is null || StartImage.Source is null || EndImage.Source is null)throw new InvalidOperationException("视频及边界帧预览没有就绪。");
        SeekBar.Value=.4;await Task.Delay(400);SetStartButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        SeekBar.Value=Math.Min(2.4,_info.Duration);await Task.Delay(400);SetEndButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if(Math.Abs(_options.Start-.4)>.01 || Math.Abs(_options.End-Math.Min(2.4,_info.Duration))>.01)throw new InvalidOperationException("播放位置没有正确写入剪辑区间。");
        EditTabs.SelectedIndex=1;if(!CropLayer.Enabled)throw new InvalidOperationException("裁剪选区没有启用。");EditTabs.SelectedIndex=0;
        SeekBar.Value=.1;await Task.Delay(300);PlayButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));await Task.Delay(1000);
        await _player.Stop();SetPlaybackButton(false);if(_player.DecodedFrames<2)throw new InvalidOperationException("播放器没有解码视频帧。");
        return new{source=_path,width=_info.Width,height=_info.Height,decodedFrames=_player.DecodedFrames,trimStart=_options.Start,trimEnd=_options.End,thumbnails=true,cropOverlay=true};
    }
    public EditorWindow() : this(new MediaEngine(new()),"",new()) { }
    public EditorWindow(IMediaEngine engine,string path,ConversionOptions options,string mode="", IReadOnlyList<ConversionOptions>? segments=null, IVideoOrientationDetector? orientationDetector=null, IMediaPreview? previewFrames=null)
    {
        InitializeComponent();_engine=engine;_previewFrames=previewFrames??engine;_path=path;_mode=mode;_options=options.Clone();_player=new(engine,path);Title=path;RefreshOptionControls();PlayButton.IsEnabled=false;SoundButton.IsEnabled=false;
        WindowArtwork.SetKind(this, Catalog.All.FirstOrDefault(f => f.Id == mode)?.Icon ?? (mode == "input-audio" ? "audio" : "clip"));
        InitializeQuickWorkflow(segments,orientationDetector);
        PrecisionCombo.ItemsSource=new[]{"0.01 s","0.1 s","1 s","1 帧"};PrecisionCombo.SelectedIndex=1;PrecisionCombo.SelectionChanged+=(_,_)=>TrimBar.Step=PrecisionCombo.SelectedIndex==3?0:Precision;
        CropRatio.ItemsSource=new[]{"自由选区","原画面比例","16:9","4:3","1:1","9:16"};CropRatio.SelectedIndex=0;
        foreach(var box in new[]{StartTime,EndTime,CropX,CropY,CropWidth,CropHeight})box.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty && !_updating){if(ReferenceEquals(box,StartTime)||ReferenceEquals(box,EndTime))_frameStep?.Cancel();ValidateInputs();}};
        SpeedCombo.SelectionChanged+=(_,_)=>{if(SpeedCombo.SelectedIndex>=0 && SpeedCombo.SelectedIndex<_speedValues.Length){_options.Speed=_speedValues[SpeedCombo.SelectedIndex];UpdateTimes();}};
        TrimBar.Changed+=(s,e)=>{_frameStep?.Cancel();_options.Start=s;_options.End=e;UpdateTimes();ScheduleThumbs();};
        SeekBar.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty && !_updating && _info is not null)Seek(SeekBar.Value);};
        _player.Updated+=p=>{var revision=_playRevision;if(_closed||revision!=_previewRevision)return;SetPosition(p);PreviewImage.Source=_player.Frame;PreviewImage.InvalidateVisual();if(_previewEnd is {} end && p>=end && !_stoppingSelection)_previewReady=FinishSelection(end,revision);};
        _player.Finished+=()=>{var revision=_playRevision;if(_closed||revision!=_previewRevision)return;if(_previewEnd is {} end){if(!_stoppingSelection)_previewReady=FinishSelection(end,revision);}else _previewReady=FinishPlayback(revision);};
        _player.Error+=message=>{if(!_closed){PreviewStatus.Text=message;PreviewStatus.IsVisible=true;}};
        CropLayer.Changed+=rect=>{_updating=true;CropX.Text=((int)rect.X).ToString();CropY.Text=((int)rect.Y).ToString();CropWidth.Text=((int)rect.Width).ToString();CropHeight.Text=((int)rect.Height).ToString();_updating=false;ValidateInputs();};
        Opened+=async(_,_)=>{await Load();if(mode is "crop" or "delogo"){EditTabs.SelectedIndex=1;if(mode=="delogo")DelogoMode.IsChecked=true;}};
        Closed+=(_,_)=>{_closed=true;_previewRevision++;_thumbnailRevision++;_playRevision=-1;_lifetime.Cancel();_seek?.Cancel();_thumbs?.Cancel();_frameStep?.Cancel();_player.Dispose();foreach(var image in new[]{PreviewImage,StartImage,EndImage})if(image.Source is Bitmap b && b!=_player.Frame)b.Dispose();_seek?.Dispose();_thumbs?.Dispose();_frameStep?.Dispose();_lifetime.Dispose();};
    }
    private async Task Load()
    {
        try
        {
            _info=await _engine.Probe(_path,_lifetime.Token,_options.VideoStreamIndex,_options.AudioStreamIndex);if(_closed)return;_player.SetStreams(_options.VideoStreamIndex,_options.AudioStreamIndex);
            var oldPreview=PreviewImage.Source as Bitmap;PreviewImage.Source=null;if(oldPreview!=_player.Frame)oldPreview?.Dispose();_player.Configure(_info);
            TrimBar.Duration=Math.Max(0,_info.Duration);if(_options.End==0)_options.End=TrimBar.Duration;_updating=true;SeekBar.Maximum=Math.Max(0,_info.Duration);SetPosition(double.IsFinite(_options.Start)?Math.Clamp(_options.Start,0,SeekBar.Maximum):0);TotalTime.Text=ShortTime(_info.Duration);UpdateTimes();
            CropLayer.SourceWidth=Math.Max(1,_info.Width);CropLayer.SourceHeight=Math.Max(1,_info.Height);ApplyRatio();
            CropLayer.PixelStep=MediaEngine.IsImage(_options.Format)?1:2;
            CropX.Text=_options.CropX.ToString();CropY.Text=_options.CropY.ToString();CropWidth.Text=(_options.CropWidth>0?_options.CropWidth:_info.Width).ToString();CropHeight.Text=(_options.CropHeight>0?_options.CropHeight:_info.Height).ToString();UpdateCropLayer();
            Localization.SetText(MediaDescription,$"{Path.GetFileName(_path)}\n时长: {TimeText(_info.Duration)}    画面: {_info.Width} × {_info.Height}\n视频: {_info.VideoCodec}    音频: {_info.AudioCodec}");
            if(_info.HasVideo){_previewReady=Frame(PreviewImage,_options.Start,_lifetime.Token);await _previewReady;_thumbnailsReady=Thumbnails(_lifetime.Token);await _thumbnailsReady;PreviewStatus.IsVisible=false;}else PreviewStatus.Text="♫ 音频预览";
            SoundButton.IsEnabled=_info.HasAudio;PlayButton.IsEnabled=_info.Duration>0;((TabItem)EditTabs.Items[0]!).IsVisible=_info.Duration>0;((TabItem)EditTabs.Items[1]!).IsVisible=_info.HasVideo&&!AudioEditing;if(_info.Duration<=0)EditTabs.SelectedIndex=1;
            if(_info.HasAudio)_audioReady=PrepareAudio();else AudioStatus.Text="此文件不含音轨";
            ValidateInputs();TrimBar.Step=PrecisionCombo.SelectedIndex==3?0:Precision;_ready.TrySetResult();await _audioReady;
            async Task PrepareAudio()
            {
                try{await _player.PrepareAudio(_lifetime.Token);if(!_closed)AudioStatus.Text="音频预览已就绪";}catch(OperationCanceledException){}catch(Exception e){if(!_closed)AudioStatus.Text=Localization.Format($"音频预览不可用: {e.Message}");}
            }
        }
        catch(OperationCanceledException){_ready.TrySetResult();}
        catch(Exception e){PreviewStatus.Text=Localization.Format($"媒体读取失败：{e.Message}");_ready.TrySetResult();if(!_closed)await Ui.Message(this,"媒体读取失败",e.Message);}
    }
    private async Task Frame(Image target,double seconds,CancellationToken ct,bool endExclusive=false,int? revision=null,int? thumbnailRevision=null)
    {
        var info=_info;if(info?.HasVideo!=true)return;var expectedRevision=revision??_previewRevision;var expectedThumbnailRevision=thumbnailRevision??_thumbnailRevision;var streamIndex=_options.VideoStreamIndex;var time=info.Duration>0&&double.IsFinite(seconds)?Math.Clamp(seconds,0,info.Duration):0;var data=await _previewFrames.Thumbnail(_path,time,960,540,ct,pad:false,videoStreamIndex:streamIndex,endExclusive:endExclusive || info.Duration>0 && time>=info.Duration);ct.ThrowIfCancellationRequested();if(_closed||!ReferenceEquals(info,_info)||streamIndex!=_options.VideoStreamIndex||(ReferenceEquals(target,PreviewImage)?expectedRevision!=_previewRevision:expectedThumbnailRevision!=_thumbnailRevision))return;
        var old=target.Source as Bitmap;using var stream=new MemoryStream(data);target.Source=new Bitmap(stream);if(old!=_player.Frame)old?.Dispose();
    }
    private (int Revision,CancellationToken Token) BeginPreview()
    {
        _seek?.Cancel();_seek?.Dispose();_seek=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _frameStep?.Cancel();_playRevision=-1;_previewEnd=null;_stoppingSelection=false;
        return (++_previewRevision,_seek.Token);
    }
    private bool CurrentPreview(int revision)=>!_closed&&revision==_previewRevision;
    private void SetPosition(double seconds)
    {_position=Math.Clamp(seconds,0,SeekBar.Maximum);_updating=true;SeekBar.Value=_position;_updating=false;CurrentTime.Text=TimeText(_position);}
    private void Seek(double seconds)
    {
        if(_closed||_info is null)return;var (revision,token)=BeginPreview();SetPosition(seconds);
        async Task Run()
        {
            try{await _player.Stop();token.ThrowIfCancellationRequested();if(!CurrentPreview(revision))return;SetPlaybackButton(false);await Task.Delay(120,token);await Frame(PreviewImage,seconds,token,revision:revision);}catch(OperationCanceledException){}catch(Exception e){if(CurrentPreview(revision)){PreviewStatus.Text=e.Message;PreviewStatus.IsVisible=true;}}
        }
        _previewReady=Run();
    }
    private Task Thumbnails(CancellationToken ct)=>Thumbnails(_options.Clone(),++_thumbnailRevision,ct);
    private async Task Thumbnails(ConversionOptions options,int revision,CancellationToken ct)
    {if(_info?.HasVideo==true){await Frame(StartImage,options.Start,ct,thumbnailRevision:revision);await Frame(EndImage,options.End,ct,endExclusive:options.End>0,thumbnailRevision:revision);}}
    private void ScheduleThumbs(){_thumbs?.Cancel();_thumbs?.Dispose();_thumbs=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_thumbs.Token;var revision=++_thumbnailRevision;var options=_options.Clone();async Task Run(){try{await Task.Delay(180,token);await Thumbnails(options,revision,token);}catch(OperationCanceledException){}catch(Exception e){if(!_closed&&revision==_thumbnailRevision){PreviewStatus.Text=Localization.Format($"边界预览失败：{e.Message}");PreviewStatus.IsVisible=true;}}}_thumbnailsReady=Run();}
    private void UpdateTimes(){_updating=true;StartTime.Text=TimeText(_options.Start);EndTime.Text=TimeText(_options.End);_updating=false;SelectionDuration.Text=TimeText(Math.Max(0,_options.End-_options.Start));OutputDuration.Text=TimeText(Math.Max(0,_options.End-_options.Start)/_options.Speed);TrimBar.Start=double.IsFinite(_options.Start)?Math.Clamp(_options.Start,0,TrimBar.Duration):0;TrimBar.End=double.IsFinite(_options.End)?Math.Clamp(_options.End,TrimBar.Start,TrimBar.Duration):TrimBar.Duration;TrimBar.InvalidateVisual();ValidateInputs();}
    private void SetPlaybackButton(bool playing)
    {
        PlayIcon.Kind=playing?"pause":"play";
        ToolTip.SetTip(PlayButton,playing?"暂停":"播放");
        Avalonia.Automation.AutomationProperties.SetName(PlayButton,playing?"暂停":"播放");
    }
    private static string ShortTime(double s)=>EditorTime.Format(s);
    private static string TimeText(double s)=>EditorTime.Format(s);
    private double Precision=>PrecisionCombo.SelectedIndex switch{0=>.01,2=>1,3=>1/(_info?.FrameRate>0?_info.FrameRate:25),_=>.1};
    private void ShiftStart(double delta){if(_info is null)return;_frameStep?.Cancel();_options.Start=Math.Clamp(_options.Start+delta,0,Math.Max(0,_options.End-Math.Min(.01,_info.Duration)));UpdateTimes();ScheduleThumbs();}
    private void ShiftEnd(double delta){if(_info is null)return;_frameStep?.Cancel();_options.End=Math.Clamp(_options.End+delta,Math.Min(_info.Duration,_options.Start+Math.Min(.01,_info.Duration)),_info.Duration);UpdateTimes();ScheduleThumbs();}
    private void StartMinus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(true,-1);
    private void StartPlus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(true,1);
    private void EndMinus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(false,-1);
    private void EndPlus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(false,1);
    private async Task StepBoundary(bool start,int direction)
    {
        if(_info is null)return;
        if(PrecisionCombo.SelectedIndex!=3 || !_info.HasVideo){if(start)ShiftStart(direction*Precision);else ShiftEnd(direction*Precision);return;}
        _frameStep?.Cancel();_frameStep?.Dispose();_frameStep=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_frameStep.Token;
        try
        {
            var range=ReadTimes();var value=await _previewFrames.AdjacentFrameTime(_path,start?range.Start:range.End,direction,token,_options.VideoStreamIndex);
            if(_closed || token.IsCancellationRequested || start && value>=range.End || !start && value<=range.Start)return;
            _options.Start=start?value:range.Start;_options.End=start?range.End:value;UpdateTimes();ScheduleThumbs();
        }
        catch(OperationCanceledException){}catch(ArgumentException){ValidateInputs();}catch(Exception ex){if(!_closed){PreviewStatus.Text=Localization.Format($"逐帧定位失败：{ex.Message}");PreviewStatus.IsVisible=true;}}
    }
    private void SetStartClick(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftStart(_position-_options.Start);
    private void SetEndClick(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>ShiftEnd(_position-_options.End);
    private void TimeEdited(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {if(_info is null || _info.Duration<=0)return;try{var (start,end)=ReadTimes();_options.Start=start;_options.End=end;UpdateTimes();ScheduleThumbs();}catch(ArgumentException){ValidateInputs();}}
    private void PlayClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_playbackReady=TogglePlayback();
    private async Task TogglePlayback()
    {
        if (_player.IsPlaying) { _player.Pause(); SetPlaybackButton(false); return; }
        if (_player.IsPaused) { _player.Resume(); SetPlaybackButton(true); return; }
        if(_info is null||_playBusy)return;var wasPlaying=_player.IsPlaying;var (revision,token)=BeginPreview();_playBusy=true;
        try{await _player.Stop();token.ThrowIfCancellationRequested();if(!CurrentPreview(revision))return;SetPlaybackButton(false);if(wasPlaying)return;await _audioReady.WaitAsync(token);if(!CurrentPreview(revision))return;if(_position>=_info.Duration-.04)SetPosition(0);_playRevision=revision;await _player.Play(_position,_info.HasVideo,_info.Duration);if(CurrentPreview(revision))SetPlaybackButton(true);}
        catch(OperationCanceledException){}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=Localization.Format($"播放失败：{ex.Message}");PreviewStatus.IsVisible=true;}}finally{_playBusy=false;}
    }
    private void PlaySelectionClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_playbackReady=PlaySelection();
    private async Task PlaySelection()
    {
        if(_info is null||_playBusy)return;_playBusy=true;var revision=_previewRevision;
        try{var (start,end)=ReadTimes();_options.Start=start;_options.End=end;UpdateTimes();ScheduleThumbs();var request=BeginPreview();revision=request.Revision;await _player.Stop();request.Token.ThrowIfCancellationRequested();await _audioReady.WaitAsync(request.Token);if(!CurrentPreview(revision))return;SetPosition(start);_previewEnd=end;_playRevision=revision;await _player.Play(start,_info.HasVideo,end);if(CurrentPreview(revision))SetPlaybackButton(true);}
        catch(OperationCanceledException){}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=Localization.Format($"区间预览失败：{ex.Message}");PreviewStatus.IsVisible=true;}}finally{_playBusy=false;}
    }
    private async Task FinishSelection(double end,int revision)
    {
        if(!CurrentPreview(revision))return;_stoppingSelection=true;var token=_seek?.Token??_lifetime.Token;
        try{await _player.Stop();token.ThrowIfCancellationRequested();if(!CurrentPreview(revision))return;_previewEnd=null;_playRevision=-1;SetPlaybackButton(false);SetPosition(end);await Frame(PreviewImage,end,token,true,revision);}
        catch(OperationCanceledException){}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=Localization.Format($"区间预览失败：{ex.Message}");PreviewStatus.IsVisible=true;}}finally{if(CurrentPreview(revision))_stoppingSelection=false;}
    }
    private async Task FinishPlayback(int revision)
    {try{await _player.Stop();if(!CurrentPreview(revision))return;_playRevision=-1;SetPlaybackButton(false);SetPosition(_info?.Duration??0);}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=ex.Message;PreviewStatus.IsVisible=true;}}}
    private void StopClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>Seek(0);
    private void BackwardClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepPosition(-1);
    private void ForwardClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepPosition(1);
    private async Task StepPosition(int direction)
    {
        if(_info is null)return;
        if(PrecisionCombo.SelectedIndex!=3 || !_info.HasVideo){SeekBar.Value=Math.Clamp(SeekBar.Value+direction*Precision,0,SeekBar.Maximum);return;}
        _frameStep?.Cancel();_frameStep?.Dispose();_frameStep=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_frameStep.Token;
        try{var position=await _previewFrames.AdjacentFrameTime(_path,SeekBar.Value,direction,token,_options.VideoStreamIndex);if(!_closed&&!token.IsCancellationRequested)SeekBar.Value=position;}
        catch(OperationCanceledException){}catch(Exception ex){if(!_closed){PreviewStatus.Text=Localization.Format($"逐帧定位失败：{ex.Message}");PreviewStatus.IsVisible=true;}}
    }
    private void SoundClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){_player.Muted=!_player.Muted;SoundIcon.Kind=_player.Muted?"muted":"speaker";ToolTip.SetTip(SoundButton,_player.Muted?"取消静音":"静音");Avalonia.Automation.AutomationProperties.SetName(SoundButton,_player.Muted?"取消静音":"静音");}
    private void TabChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(CropLayer is null)return;CropLayer.Enabled=EditTabs.SelectedIndex==1;CropLayer.InvalidateVisual();UpdateDirectionPreview();
        if(QuickWorkflow && _info is not null && ReferenceEquals(e.Source,EditTabs) && EditTabs.SelectedItem==SegmentsTab)
        {try{CommitActiveSegment();SegmentError.Text="";}catch(Exception ex){SegmentError.Text=ex.Message;}}
    }
    private void DelogoChanged(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(_info is null || _updating)return;
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
    private (double Start,double End) ReadTimes()
    {
        if(_info is null || !EditorTime.TryRead(StartTime.Text,_options.Start,out var start) || !EditorTime.TryRead(EndTime.Text,_options.End,out var end) || start<0 || end<=start || end>_info.Duration+.001)throw new ArgumentException("请填写有效的开始/结束时间，结束须晚于开始且不超过媒体时长。");
        return (start,Math.Min(end,_info.Duration));
    }
    private CropArea ReadRegion()
    {
        if(_info is null || !int.TryParse(CropX.Text,out var x) || !int.TryParse(CropY.Text,out var y) || !int.TryParse(CropWidth.Text,out var w) || !int.TryParse(CropHeight.Text,out var h))throw new ArgumentException("裁剪区域请输入整数像素。");
        if(w==0 && h==0)return new(0,0,0,0);
        var area=new CropArea(x,y,w,h);
        var full=x==0 && y==0 && w==_info.Width && h==_info.Height;
        CropGeometry.Validate(area,_info,!_regionDelogo && !MediaEngine.IsImage(_options.Format) && !full);
        return area;
    }
    private void ValidateInputs()
    {
        if(TimeError is null || CropError is null || ConfirmButton is null)return;
        TimeError.Text="";CropError.Text="";
        if(_info is null){ConfirmButton.IsEnabled=false;PlaySelectionButton.IsEnabled=false;return;}
        if(_info.Duration>0)try{var range=ReadTimes();SelectionDuration.Text=TimeText(range.End-range.Start);OutputDuration.Text=TimeText((range.End-range.Start)/_options.Speed);}catch(ArgumentException ex){TimeError.Text=ex.Message;SelectionDuration.Text="—";OutputDuration.Text="—";}
        if(_info.HasVideo && !AudioEditing)try{ReadRegion();}catch(ArgumentException ex){CropError.Text=ex.Message;}
        ConfirmButton.IsEnabled=string.IsNullOrEmpty(TimeError.Text)&&string.IsNullOrEmpty(CropError.Text);
        PlaySelectionButton.IsEnabled=_info.Duration>0 && string.IsNullOrEmpty(TimeError.Text);
    }
    private void UpdateCropLayer()
    {try{var area=ReadRegion();CropLayer.Selection=new(area.X,area.Y,area.Width,area.Height);CropLayer.InvalidateVisual();}catch(ArgumentException){}ValidateInputs();}
    private void RatioChanged(object? sender,SelectionChangedEventArgs e)
    {ApplyRatio();}
    private void ApplyRatio()
    {
        if(CropLayer is null)return;
        CropLayer.AspectRatio=CropRatio.SelectedIndex switch{1=>_info is {Height:>0}?(double)_info.Width/_info.Height:0,2=>16d/9,3=>4d/3,4=>1,5=>9d/16,_=>0};
    }
    private void ApplyCropClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>UpdateCropLayer();
    private void ResetCropClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(_info is null)return;if(_regionDelogo){_options.DelogoX=_options.DelogoY=_options.DelogoWidth=_options.DelogoHeight=0;}else{_options.CropX=_options.CropY=_options.CropWidth=_options.CropHeight=0;}CropRatio.SelectedIndex=0;LoadRegion();}
    private async void OptionsClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var draft=ReadDraft();var kind=QuickWorkflow?MediaOptionsKind.ClipEdit:_mode is "input" or "input-audio"?(_info?.HasVideo==true&&!AudioEditing?MediaOptionsKind.InputVideo:MediaOptionsKind.InputAudio):_mode=="frames"?MediaOptionsKind.Frames:MediaEngine.IsAudio(draft.Format)?MediaOptionsKind.Audio:MediaEngine.IsImage(draft.Format)?MediaOptionsKind.Image:MediaOptionsKind.Video;
            var result=await new OptionsWindow(draft,kind:kind).ShowDialog<ConversionOptions?>(this);
            if(result is not null){var tracksChanged=result.VideoStreamIndex!=_options.VideoStreamIndex || result.AudioStreamIndex!=_options.AudioStreamIndex;if(tracksChanged)await _engine.Probe(_path,_lifetime.Token,result.VideoStreamIndex,result.AudioStreamIndex);_options=result;if(tracksChanged){ClearDirectionDetection();await _player.Stop();await _audioReady;await Load();}if(_info is not null && _options.End==0)_options.End=_info.Duration;RefreshOptionControls();UpdateTimes();LoadRegion();SyncDirectionControls();}
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
        if(_info.Duration>0){var (start,end)=ReadTimes();draft.Start=start;draft.End=end>=_info.Duration-.0000001?0:end;}
        else{draft.Start=0;draft.End=0;}
        draft.Speed=_speedValues[Math.Max(0,SpeedCombo.SelectedIndex)];var fadeIn=_fadeInValues[Math.Max(0,FadeInCombo.SelectedIndex)];var fadeOut=_fadeOutValues[Math.Max(0,FadeOutCombo.SelectedIndex)];if(AudioEditing){draft.AudioFadeIn=fadeIn;draft.AudioFadeOut=fadeOut;}else{draft.FadeIn=fadeIn;draft.FadeOut=fadeOut;}
        if(_info.HasVideo&&!AudioEditing)
        {
            var area=ReadRegion();int x=area.X,y=area.Y,w=area.Width,h=area.Height;
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
            if(QuickWorkflow){Close(ReadClipEdit());return;}
            var draft=ReadDraft();MediaEngine.Validate(new(){FeatureId="mp4",Inputs=[_path],Output=Path.Combine(Path.GetTempPath(),"validate-output.mp4"),Options=draft});Close(draft);
        }
        catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
    }
}
