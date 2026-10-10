using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly MediaFrameView _previewView,_startView,_endView;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly PreviewRequest _seek=new(),_thumbs=new(),_frameStep=new();
    private readonly TaskCompletionSource _ready=new();
    private bool _updating;
    private bool _closed;
    private bool _regionDelogo;
    private bool _playBusy;
    private bool _previewFailed;
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
    private long _positionPublished;
    private readonly string _mode;
    private double[] _speedValues=[],_fadeInValues=[],_fadeOutValues=[];
    private bool AudioEditing=>MediaEngine.IsAudio(_options.Format)||_mode=="input-audio";
    public Task Ready=>_ready.Task;
    public Task ThumbnailsReady=>_thumbnailsReady;
    public Task PositionReady=>_positionReady;
    public Task PreviewReady=>Task.WhenAll(_previewReady,_segmentReady);
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
    public EditorWindow(IMediaEngine engine,string path,ConversionOptions options,string mode="", IReadOnlyList<ConversionOptions>? segments=null, IVideoOrientationDetector? orientationDetector=null, IMediaPreview? previewFrames=null, bool removeSelected=false)
    {
        InitializeComponent();_previewView=new(PreviewImage);_startView=new(StartImage);_endView=new(EndImage);_engine=engine;_previewFrames=previewFrames??engine;_path=path;_mode=mode;_options=options.Clone();_player=new(engine,path);Title=path;RefreshOptionControls();PlayButton.IsEnabled=false;SoundButton.IsEnabled=false;
        WindowArtwork.SetKind(this, Catalog.All.FirstOrDefault(f => f.Id == mode)?.Icon ?? (mode == "input-audio" ? "audio" : "clip"));
        InitializeQuickWorkflow(segments,orientationDetector,removeSelected);
        PrecisionCombo.ItemsSource=new[]{"0.01 s","0.1 s","1 s","1 帧"};PrecisionCombo.SelectedIndex=1;PrecisionCombo.SelectionChanged+=(_,_)=>TrimBar.Step=PrecisionCombo.SelectedIndex==3?0:Precision;
        CropRatio.ItemsSource=new[]{"自由选区","原画面比例","16:9","4:3","1:1","9:16"};CropRatio.SelectedIndex=0;
        foreach(var box in new[]{StartTime,EndTime,CropX,CropY,CropWidth,CropHeight})box.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty && !_updating){if(ReferenceEquals(box,StartTime)||ReferenceEquals(box,EndTime))_frameStep.Cancel();ValidateInputs();}};
        SpeedCombo.SelectionChanged+=(_,_)=>{if(!_updating && SpeedCombo.SelectedIndex>=0 && SpeedCombo.SelectedIndex<_speedValues.Length){_options.Speed=_speedValues[SpeedCombo.SelectedIndex];UpdateTimes();}};
        TrimBar.Changed+=(s,e)=>{_frameStep.Cancel();_options.Start=s;_options.End=e;UpdateTimes();ScheduleThumbs();};
        SeekBar.PropertyChanged+=(_,e)=>{if(e.Property==Slider.ValueProperty && !_updating && _info is not null)Seek(SeekBar.Value);};
        _player.Updated+=p=>{var revision=_playRevision;if(_closed||revision!=_previewRevision)return;SetPosition(p,false);_previewView.Show(_player.Frame);if(_previewEnd is {} end && p>=end && !_stoppingSelection)_previewReady=FinishSelection(end,revision);};
        _player.Finished+=()=>{var revision=_playRevision;if(_closed||revision!=_previewRevision)return;if(_previewEnd is {} end){if(!_stoppingSelection)_previewReady=FinishSelection(end,revision);}else _previewReady=FinishPlayback(revision);};
        _player.Error+=message=>{if(!_closed){CancelSegmentSequence();_previewFailed=true;PreviewStatus.Text=message;PreviewStatus.IsVisible=true;}};
        CropLayer.Changed+=rect=>{_updating=true;CropX.Text=((int)rect.X).ToString();CropY.Text=((int)rect.Y).ToString();CropWidth.Text=((int)rect.Width).ToString();CropHeight.Text=((int)rect.Height).ToString();_updating=false;ValidateInputs();};
        Opened+=async(_,_)=>{await Load();if(mode is "crop" or "delogo"){EditTabs.SelectedIndex=1;if(mode=="delogo")DelogoMode.IsChecked=true;}};
        if (mode == "person-exclusion") ConfigureIntervalSelection();
        PropertyChanged+=(_,e)=>{if(e.Property==WindowStateProperty||e.Property==IsVisibleProperty)_player.PresentationVisible=IsVisible&&WindowState!=WindowState.Minimized;};
        Closed+=(_,_)=>{_closed=true;_previewRevision++;_thumbnailRevision++;_playRevision=-1;_lifetime.Cancel();_seek.Dispose();_thumbs.Dispose();_frameStep.Dispose();_player.Dispose();_previewView.Dispose();_startView.Dispose();_endView.Dispose();_lifetime.Dispose();};
    }
    private async Task Load()
    {
        try
        {
            _info=await _engine.Probe(_path,_lifetime.Token,_options.VideoStreamIndex,_options.AudioStreamIndex);if(_closed)return;_options.VideoStreamIndex=_info.VideoStreamIndex;
            _previewView.Clear();_player.Configure(_info);
            TrimBar.Duration=Math.Max(0,_info.Duration);if(_options.End==0)_options.End=TrimBar.Duration;_updating=true;SeekBar.Maximum=Math.Max(0,_info.Duration);SetPosition(double.IsFinite(_options.Start)?Math.Clamp(_options.Start,0,SeekBar.Maximum):0);TotalTime.Text=EditorTime.Format(_info.Duration);UpdateTimes();
            CropLayer.SourceWidth=Math.Max(1,_info.Width);CropLayer.SourceHeight=Math.Max(1,_info.Height);ApplyRatio();
            CropLayer.PixelStep=MediaEngine.IsImage(_options.Format)?1:2;
            CropX.Text=_options.CropX.ToString();CropY.Text=_options.CropY.ToString();CropWidth.Text=(_options.CropWidth>0?_options.CropWidth:_info.Width).ToString();CropHeight.Text=(_options.CropHeight>0?_options.CropHeight:_info.Height).ToString();UpdateCropLayer();
            Localization.SetText(MediaDescription,$"{Path.GetFileName(_path)}\n时长: {EditorTime.Format(_info.Duration)}    画面: {_info.Width} × {_info.Height}\n视频: {_info.VideoCodec}    音频: {_info.AudioCodec}");
            if(_info.HasVideo){_previewReady=Frame(_previewView,_options.Start,_lifetime.Token);await _previewReady;_thumbnailsReady=Thumbnails(_lifetime.Token);await _thumbnailsReady;PreviewStatus.IsVisible=false;}else PreviewStatus.Text="♫ 音频预览";
            SoundButton.IsEnabled=_info.HasAudio;PlayButton.IsEnabled=_info.Duration>0;((TabItem)EditTabs.Items[0]!).IsVisible=_info.Duration>0;((TabItem)EditTabs.Items[1]!).IsVisible=_info.HasVideo&&!AudioEditing;if(_info.Duration<=0)EditTabs.SelectedIndex=1;
            if (_mode == "person-exclusion") ConfigureIntervalSelection();
            AudioStatus.Text=_info.HasAudio?"音频预览已就绪":"此文件不含音轨";
            RefreshSegments();ValidateInputs();TrimBar.Step=PrecisionCombo.SelectedIndex==3?0:Precision;_ready.TrySetResult();
        }
        catch(OperationCanceledException){_ready.TrySetResult();}
        catch(Exception e){PreviewStatus.Text=Localization.Format($"媒体读取失败：{e.Message}");_ready.TrySetResult();if(!_closed)await Ui.Message(this,"媒体读取失败",e.Message);}
    }
    private async Task Frame(MediaFrameView target,double seconds,CancellationToken ct,bool endExclusive=false,int? revision=null,int? thumbnailRevision=null)
    {
        var info=_info;if(info?.HasVideo!=true)return;var expectedRevision=revision??_previewRevision;var expectedThumbnailRevision=thumbnailRevision??_thumbnailRevision;var streamIndex=_options.VideoStreamIndex;var time=info.Duration>0&&double.IsFinite(seconds)?Math.Clamp(seconds,0,info.Duration):0;var data=await _previewFrames.Thumbnail(_path,time,960,540,ct,pad:false,videoStreamIndex:streamIndex,endExclusive:endExclusive || info.Duration>0 && time>=info.Duration);ct.ThrowIfCancellationRequested();if(_closed||!ReferenceEquals(info,_info)||streamIndex!=_options.VideoStreamIndex||(ReferenceEquals(target,_previewView)?expectedRevision!=_previewRevision:expectedThumbnailRevision!=_thumbnailRevision))return;
        if(ReferenceEquals(target,_previewView))PreviewStatus.IsVisible=false;target.Show(data);
    }
    private (int Revision,CancellationToken Token) BeginPreview(bool keepSequence=false)
    {
        if(!keepSequence)CancelSegmentSequence();_previewFailed=false;
        var token=_seek.Restart(_lifetime.Token);
        _frameStep.Cancel();_playRevision=-1;_previewEnd=null;_stoppingSelection=false;
        return (++_previewRevision,token);
    }
    private bool CurrentPreview(int revision)=>!_closed&&revision==_previewRevision;
    private void SetPosition(double seconds,bool immediate=true)
    {_position=Math.Clamp(seconds,0,SeekBar.Maximum);if(!EditorTime.ShouldRefresh(ref _positionPublished,immediate))return;_updating=true;SeekBar.Value=_position;_updating=false;CurrentTime.Text=EditorTime.Format(_position);if(QuickWorkflow){SegmentTrack.SetPosition(_position);UpdateSegmentActions();}}
    private void Seek(double seconds,bool keepSequence=false)
    {
        if(_closed||_info is null)return;var (revision,token)=BeginPreview(keepSequence);SetPosition(seconds);
        async Task Run()
        {
            try{await _player.Stop();token.ThrowIfCancellationRequested();if(!CurrentPreview(revision))return;SetPlaybackButton(false);await Task.Delay(120,token);await Frame(_previewView,seconds,token,revision:revision);}catch(OperationCanceledException){}catch(Exception e){if(CurrentPreview(revision)){PreviewStatus.Text=e.Message;PreviewStatus.IsVisible=true;}}
        }
        _previewReady=Run();
    }
    private Task Thumbnails(CancellationToken ct)=>Thumbnails(_options.Clone(),++_thumbnailRevision,ct);
    private async Task Thumbnails(ConversionOptions options,int revision,CancellationToken ct)
    {if(_info?.HasVideo==true){await Frame(_startView,options.Start,ct,thumbnailRevision:revision);await Frame(_endView,options.End,ct,endExclusive:options.End>0,thumbnailRevision:revision);}}
    private void ScheduleThumbs(){var token=_thumbs.Restart(_lifetime.Token);var revision=++_thumbnailRevision;var options=_options.Clone();async Task Run(){try{await Task.Delay(180,token);await Thumbnails(options,revision,token);}catch(OperationCanceledException){}catch(Exception e){if(!_closed&&revision==_thumbnailRevision){PreviewStatus.Text=Localization.Format($"边界预览失败：{e.Message}");PreviewStatus.IsVisible=true;}}}_thumbnailsReady=Run();}
    private void UpdateTimes(){_updating=true;StartTime.Text=EditorTime.Format(_options.Start);EndTime.Text=EditorTime.Format(_options.End);_updating=false;TrimBar.Start=double.IsFinite(_options.Start)?Math.Clamp(_options.Start,0,TrimBar.Duration):0;TrimBar.End=double.IsFinite(_options.End)?Math.Clamp(_options.End,TrimBar.Start,TrimBar.Duration):TrimBar.Duration;TrimBar.InvalidateVisual();ValidateInputs();}
    private void SetPlaybackButton(bool playing)
    {
        PlayIcon.Kind=playing?"pause":"play";
        ToolTip.SetTip(PlayButton,playing?"暂停":"播放");
        Avalonia.Automation.AutomationProperties.SetName(PlayButton,playing?"暂停":"播放");
    }
    private double Precision=>PrecisionCombo.SelectedIndex switch{0=>.01,2=>1,3=>1/(_info?.FrameRate>0?_info.FrameRate:25),_=>.1};
    private void ShiftStart(double delta){if(_info is null)return;_frameStep.Cancel();_options.Start=Math.Clamp(_options.Start+delta,0,Math.Max(0,_options.End-Math.Min(.01,_info.Duration)));UpdateTimes();ScheduleThumbs();}
    private void ShiftEnd(double delta){if(_info is null)return;_frameStep.Cancel();_options.End=Math.Clamp(_options.End+delta,Math.Min(_info.Duration,_options.Start+Math.Min(.01,_info.Duration)),_info.Duration);UpdateTimes();ScheduleThumbs();}
    private void StartMinus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(true,-1);
    private void StartPlus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(true,1);
    private void EndMinus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(false,-1);
    private void EndPlus(object? s,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepBoundary(false,1);
    private async Task StepBoundary(bool start,int direction)
    {
        if(_info is null)return;
        if(PrecisionCombo.SelectedIndex!=3 || !_info.HasVideo){if(start)ShiftStart(direction*Precision);else ShiftEnd(direction*Precision);return;}
        var token=_frameStep.Restart(_lifetime.Token);
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
        if (_player.IsPlaying) { _player.Pause(); SetPosition(_position); SetPlaybackButton(false); return; }
        if (_player.IsPaused) { _player.Resume(); SetPosition(_position); SetPlaybackButton(true); return; }
        if(QuickWorkflow){if(_removeSelected)await StartSegmentSequence(fromPosition:true);else await PlaySelection(fromPosition:true);return;}
        if(_info is null||_playBusy)return;var (revision,token)=BeginPreview();_playBusy=true;
        try{await _player.Stop();token.ThrowIfCancellationRequested();if(!CurrentPreview(revision))return;SetPlaybackButton(false);if(_position>=_info.Duration-.04)SetPosition(0);_playRevision=revision;await _player.Play(_position,_info.HasVideo,_info.Duration);if(CurrentPreview(revision))SetPlaybackButton(true);}
        catch(OperationCanceledException){}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=Localization.Format($"播放失败：{ex.Message}");PreviewStatus.IsVisible=true;}}finally{_playBusy=false;}
    }
    private void PlaySelectionClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_playbackReady=PlaySelection();
    private async Task<bool> PlaySelection(bool fromPosition=false,bool keepSequence=false)
    {
        if(_info is null||_playBusy)return false;
        _playBusy=true;var revision=_previewRevision;
        try
        {
            var (start,end)=ReadTimes();_options.Start=start;_options.End=end;UpdateTimes();ScheduleThumbs();
            var request=BeginPreview(keepSequence);revision=request.Revision;
            await _player.Stop();request.Token.ThrowIfCancellationRequested();
            if(!CurrentPreview(revision))return false;
            var position=fromPosition&&_position>=start&&_position<end?_position:start;
            SetPosition(position);_previewEnd=end;_playRevision=revision;_player.Speed=QuickWorkflow&&!_removeSelected?_options.Speed:1;
            PreviewStatus.IsVisible=false;await _player.Play(position,_info.HasVideo,end);
            if(!CurrentPreview(revision))return false;
            SetPlaybackButton(true);return true;
        }
        catch(OperationCanceledException){return false;}
        catch(Exception ex)
        {
            if(CurrentPreview(revision)){CancelSegmentSequence();_previewFailed=true;PreviewStatus.Text=Localization.Format($"区间预览失败：{ex.Message}");PreviewStatus.IsVisible=true;}
            return false;
        }
        finally{_playBusy=false;}
    }
    private async Task FinishSelection(double end,int revision)
    {
        if(!CurrentPreview(revision))return;_stoppingSelection=true;var token=_seek.Token;
        try{await _player.Stop();token.ThrowIfCancellationRequested();if(!CurrentPreview(revision))return;_previewEnd=null;_playRevision=-1;SetPlaybackButton(false);if(await ContinueSegmentPreview())return;if(!CurrentPreview(revision))return;SetPosition(end);await Frame(_previewView,end,token,true,revision);}
        catch(OperationCanceledException){}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=Localization.Format($"区间预览失败：{ex.Message}");PreviewStatus.IsVisible=true;}}finally{if(CurrentPreview(revision))_stoppingSelection=false;}
    }
    private async Task FinishPlayback(int revision)
    {try{await _player.Stop();if(!CurrentPreview(revision))return;_playRevision=-1;SetPlaybackButton(false);SetPosition(_info?.Duration??0);}catch(Exception ex){if(CurrentPreview(revision)){PreviewStatus.Text=ex.Message;PreviewStatus.IsVisible=true;}}}
    private void StopClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>Seek(QuickWorkflow?_options.Start:0);
    private void BackwardClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepPosition(-1);
    private void ForwardClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_positionReady=StepPosition(1);
    private async Task StepPosition(int direction)
    {
        if(_info is null)return;
        if(PrecisionCombo.SelectedIndex!=3 || !_info.HasVideo){SeekBar.Value=Math.Clamp(SeekBar.Value+direction*Precision,0,SeekBar.Maximum);return;}
        var token=_frameStep.Restart(_lifetime.Token);
        try{var position=await _previewFrames.AdjacentFrameTime(_path,SeekBar.Value,direction,token,_options.VideoStreamIndex);if(!_closed&&!token.IsCancellationRequested)SeekBar.Value=position;}
        catch(OperationCanceledException){}catch(Exception ex){if(!_closed){PreviewStatus.Text=Localization.Format($"逐帧定位失败：{ex.Message}");PreviewStatus.IsVisible=true;}}
    }
    private void SoundClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){_player.Muted=!_player.Muted;SoundIcon.Kind=_player.Muted?"muted":"speaker";ToolTip.SetTip(SoundButton,_player.Muted?"取消静音":"静音");Avalonia.Automation.AutomationProperties.SetName(SoundButton,_player.Muted?"取消静音":"静音");}
    private void TabChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(CropLayer is null)return;CropLayer.Enabled=EditTabs.SelectedIndex==1;CropLayer.InvalidateVisual();UpdateDirectionPreview();
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
        if(_info.Duration>0)try{_=ReadTimes();}catch(ArgumentException ex){TimeError.Text=ex.Message;}
        if(_info.HasVideo && !AudioEditing)try{ReadRegion();}catch(ArgumentException ex){CropError.Text=ex.Message;}
        ConfirmButton.IsEnabled=string.IsNullOrEmpty(TimeError.Text)&&string.IsNullOrEmpty(CropError.Text);
        PlaySelectionButton.IsEnabled=_info.Duration>0 && string.IsNullOrEmpty(TimeError.Text);
        ValidateSegmentOutput();
        ScheduleSegmentUpdate();
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
            var result=await new OptionsWindow(draft,kind:kind,previewEngine:_engine,previewSource:_path).ShowDialog<ConversionOptions?>(this);
            if(result is not null){var tracksChanged=result.VideoStreamIndex!=_options.VideoStreamIndex || result.AudioStreamIndex!=_options.AudioStreamIndex;if(tracksChanged)await _engine.Probe(_path,_lifetime.Token,result.VideoStreamIndex,result.AudioStreamIndex);_options=result;if(tracksChanged){ClearDirectionDetection();await _player.Stop();await Load();}if(_info is not null && _options.End==0)_options.End=_info.Duration;RefreshOptionControls();UpdateTimes();LoadRegion();SyncDirectionControls();}
        }catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
    }
    private void RefreshOptionControls()
    {
        var updating=_updating;_updating=true;
        _speedValues=new[]{1,.25,.5,.75,1.25,1.5,2,4}.Append(_options.Speed).Distinct().ToArray();SpeedCombo.ItemsSource=_speedValues.Select(v=>v==1?"默认":MediaEngine.Number(v)+"×").ToArray();SpeedCombo.SelectedIndex=Array.IndexOf(_speedValues,_options.Speed);
        var fadeIn=AudioEditing?_options.AudioFadeIn??_options.FadeIn:_options.FadeIn;var fadeOut=AudioEditing?_options.AudioFadeOut??_options.FadeOut:_options.FadeOut;
        _fadeInValues=new[]{0,.5,1,2,3}.Append(fadeIn).Distinct().ToArray();_fadeOutValues=new[]{0,.5,1,2,3}.Append(fadeOut).Distinct().ToArray();FadeInCombo.ItemsSource=_fadeInValues.Select(v=>v==0?"关闭":MediaEngine.Number(v)+" s").ToArray();FadeOutCombo.ItemsSource=_fadeOutValues.Select(v=>v==0?"关闭":MediaEngine.Number(v)+" s").ToArray();FadeInCombo.SelectedIndex=Array.IndexOf(_fadeInValues,fadeIn);FadeOutCombo.SelectedIndex=Array.IndexOf(_fadeOutValues,fadeOut);
        _updating=updating;
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
    private void ConfigureIntervalSelection()
    {
        Title = Localization.Text("免检测区间") + " · " + Path.GetFileName(_path);
        ConfirmButton.Content = "保存区间";
        ((TabItem)EditTabs.Items[0]!).Header = "免检测区间";
        foreach (var tab in EditTabs.Items.OfType<TabItem>().Skip(1)) tab.IsVisible = false;
        EditTabs.SelectedIndex = 0;
        if (FadeInCombo.Parent is Control fadeIn) fadeIn.IsVisible = false;
        if (FadeOutCombo.Parent is Control fadeOut) fadeOut.IsVisible = false;
        SpeedCombo.IsEnabled = false;
        if (SpeedCombo.Parent is Grid times)
            foreach (var control in times.Children.Where(control => Grid.GetColumn(control) is 2 or 3))
                control.IsVisible = false;
    }
    private void CancelClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>Close(null);
    private async void ConfirmClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if(QuickWorkflow){ToolExecution.Complete(this,ReadClipEdit());return;}
            var draft=ReadDraft();var feature=Catalog.All.FirstOrDefault(item=>item.Id==_mode)??Catalog.Find("mp4");
            MediaEngine.ValidateEdits(new(){FeatureId=feature.Id,Inputs=[_path],Output=Path.Combine(Path.GetTempPath(),"validate-output."+draft.Format),Options=draft},[_info!]);Close(draft);
        }
        catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
    }
}
