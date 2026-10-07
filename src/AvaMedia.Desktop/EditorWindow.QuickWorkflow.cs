using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class ClipSegmentEntry : Observable
{
    private ConversionOptions _options;
    private int _number;
    public ClipSegmentEntry(ConversionOptions options) => _options=options.Clone();
    public ConversionOptions Options { get=>_options; set { _options=value.Clone();Raise(nameof(Summary)); } }
    public int Number { get=>_number; set { _number=value;Raise(nameof(Summary)); } }
    public string Summary => Localization.Join("\n", new[] {
        Localization.Format($"片段 {Number}   ·   {Stamp(_options.Start)} → {(_options.End>0?Stamp(_options.End):Localization.Text("视频结尾"))}   ·   {MediaEngine.Number(_options.Speed)}×"),
        Localization.Join("   ·   ", new[] { _options.CropWidth>0?Localization.Format($"裁剪 {_options.CropX},{_options.CropY} / {_options.CropWidth}×{_options.CropHeight}"):"完整画面", BatchRotate.Direction(_options.Rotation), _options.Flip?"水平镜像":"" }.Where(s=>s.Length>0)) });
    private static string Stamp(double seconds)=>MediaTime.Format(seconds);
}

public partial class EditorWindow
{
    private readonly ObservableCollection<ClipSegmentEntry> _segments=[];
    private ClipSegmentEntry? _activeSegment;
    private bool _selectingSegment;
    private IVideoOrientationDetector? _directionDetector;
    private CancellationTokenSource? _directionCancellation;
    private VideoOrientationResult? _directionResult;
    private Task _directionReady=Task.CompletedTask;
    private bool QuickWorkflow=>_mode=="quick-workflow";
    public IReadOnlyList<ClipSegmentEntry> Segments=>_segments;
    public Task DirectionReady=>_directionReady;

    private void InitializeQuickWorkflow(IReadOnlyList<ConversionOptions>? segments, IVideoOrientationDetector? detector)
    {
        if(!QuickWorkflow)return;
        _directionDetector=detector??new VideoOrientationDetector(_engine);
        SegmentsTab.IsVisible=DirectionTab.IsVisible=SegmentFooter.IsVisible=true;
        EditOptionsButton.Content="其他编辑选项…";
        PreviewNote.Text="速度、淡入淡出及其他效果仅在输出时生效。";
        ConfirmButton.Content="下一步：导出选项";
        DirectionCombo.ItemsSource=new[]{"保持原方向","顺时针 90°","旋转 180°","逆时针 90°"};
        Avalonia.Automation.AutomationProperties.SetName(DirectionCombo,"当前片段旋转方向");
        Avalonia.Automation.AutomationProperties.SetName(SegmentList,"视频片段列表");
        SyncDirectionControls();
        SegmentList.ItemsSource=_segments;
        foreach(var draft in segments??[])_segments.Add(new(draft));
        if(_segments.Count==0)_segments.Add(new(_options));
        _selectingSegment=true;_activeSegment=_segments.FirstOrDefault();SegmentList.SelectedItem=_activeSegment;_selectingSegment=false;
        RefreshSegments();
        Closed+=(_,_)=>_directionCancellation?.Cancel();
    }

    public void SetWorkflowStep(int current,int total)
    {
        Localization.SetTitle(this,$"快速剪辑 · 编辑 {current}/{total} · {Path.GetFileName(_path)}");
        ConfirmButton.Content=current<total?"下一个视频 →":"下一步：导出选项 →";
    }

    private void CommitActiveSegment()
    {
        if(_activeSegment is not null)_activeSegment.Options=ReadDraft();
    }

    private void RefreshSegments()
    {
        for(var i=0;i<_segments.Count;i++)_segments[i].Number=i+1;
        SegmentCount.Text=_segments.Count==0?"当前区间导出 1 个片段":Localization.Format($"{_segments.Count} 个片段分别导出");
        RemoveSegmentButton.IsEnabled=_activeSegment is not null && _segments.Count>1;
        SegmentUpButton.IsEnabled=_activeSegment is not null && _segments.IndexOf(_activeSegment)>0;
        SegmentDownButton.IsEnabled=_activeSegment is not null && _segments.IndexOf(_activeSegment)<_segments.Count-1;
    }

    private void AddSegmentClick(object? sender,RoutedEventArgs e)
    {
        if(!QuickWorkflow || _info is null)return;
        try
        {
            var draft=ReadDraft();CommitActiveSegment();var entry=new ClipSegmentEntry(draft);_segments.Add(entry);
            _selectingSegment=true;_activeSegment=entry;SegmentList.SelectedItem=entry;_selectingSegment=false;
            SegmentError.Text="";RefreshSegments();
        }
        catch(Exception ex){SegmentError.Text=ex.Message;EditTabs.SelectedItem=SegmentsTab;}
    }

    private async void SegmentChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(_selectingSegment || !QuickWorkflow || _info is null || ReferenceEquals(SegmentList.SelectedItem,_activeSegment))return;
        try
        {
            CommitActiveSegment();
            if(SegmentList.SelectedItem is not ClipSegmentEntry entry)return;
            BeginPreview();_ = _player.Stop();
            // All workflow segments use the selected source streams. Other editing settings are independent.
            var tracksChanged=entry.Options.VideoStreamIndex!=_options.VideoStreamIndex || entry.Options.AudioStreamIndex!=_options.AudioStreamIndex;
            _activeSegment=entry;_options=entry.Options.Clone();
            if(tracksChanged){ClearDirectionDetection();await _player.Stop();await _audioReady;await Load();}
            if(_closed)return;
            if(_options.End==0)_options.End=_info.Duration;
            RefreshOptionControls();UpdateTimes();
            _updating=true;_regionDelogo=false;DelogoMode.IsChecked=false;_updating=false;LoadRegion();SyncDirectionControls();Seek(_options.Start);ScheduleThumbs();
            SegmentError.Text="";RefreshSegments();
        }
        catch(Exception ex)
        {
            _selectingSegment=true;SegmentList.SelectedItem=_activeSegment;_selectingSegment=false;SegmentError.Text=ex.Message;
        }
    }

    private void RemoveSegmentClick(object? sender,RoutedEventArgs e)
    {
        if(_activeSegment is null || _segments.Count<=1)return;
        var index=_segments.IndexOf(_activeSegment);
        _selectingSegment=true;_segments.Remove(_activeSegment);_activeSegment=null;SegmentList.SelectedItem=null;_selectingSegment=false;
        if(_segments.Count>0)SegmentList.SelectedItem=_segments[Math.Min(index,_segments.Count-1)];
        RefreshSegments();
    }

    private void MoveSegment(int delta)
    {
        if(_activeSegment is null)return;
        try
        {
            CommitActiveSegment();var index=_segments.IndexOf(_activeSegment);var target=index+delta;
            if(target<0 || target>=_segments.Count)return;
            _selectingSegment=true;_segments.Move(index,target);SegmentList.SelectedItem=_activeSegment;_selectingSegment=false;RefreshSegments();SegmentError.Text="";
        }
        catch(Exception ex){SegmentError.Text=ex.Message;}
    }
    private void SegmentUpClick(object? sender,RoutedEventArgs e)=>MoveSegment(-1);
    private void SegmentDownClick(object? sender,RoutedEventArgs e)=>MoveSegment(1);

    private async void SplitSegmentClick(object? sender,RoutedEventArgs e)
    {
        if(_info is null || _activeSegment is null)return;
        try
        {
            CommitActiveSegment();var entry=_activeSegment;
            var parts=await new ClipSplitWindow(entry.Options,_info.Duration).ShowDialog<IReadOnlyList<ConversionOptions>?>(this);
            if(parts is null || _closed)return;
            var index=_segments.IndexOf(entry);if(index<0)return;
            _selectingSegment=true;_segments.RemoveAt(index);
            foreach(var part in parts.Reverse())_segments.Insert(index,new(part));
            _activeSegment=null;SegmentList.SelectedItem=null;_selectingSegment=false;
            SegmentList.SelectedItem=_segments[index];RefreshSegments();SegmentError.Text="";
        }
        catch(Exception ex){SegmentError.Text=ex.Message;}
    }

    public ClipEditResult ReadClipEdit()
    {
        if(!QuickWorkflow || _info?.HasVideo!=true || _info.Duration<=0)throw new ArgumentException("请先选择有效视频。");
        CommitActiveSegment();
        var drafts=_segments.Count==0?new[]{ReadDraft()}:_segments.Select(s=>s.Options.Clone()).ToArray();
        foreach(var draft in drafts)
        {
            // Export format is chosen in the next step. Validate edits with a re-encoding draft here.
            draft.CopyStreams=false;draft.Format="mp4";
            var job=new Job{FeatureId="clip",Inputs=[_path],Output=Path.Combine(Path.GetTempPath(),"validate-clip.mp4"),Options=draft};
            MediaEngine.Validate(job);MediaEngine.ValidateEdits(job,[_info]);
        }
        return new(_path,_info,drafts);
    }

    private void SyncDirectionControls()
    {
        if(!QuickWorkflow || DirectionCombo is null)return;
        var updating=_updating;_updating=true;DirectionCombo.SelectedIndex=_options.Rotation/90;MirrorCheck.IsChecked=_options.Flip;_updating=updating;
        UpdateDirectionPreview();
    }
    private void DirectionChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(!QuickWorkflow || _updating || DirectionCombo.SelectedIndex<0)return;
        _options.Rotation=DirectionCombo.SelectedIndex*90;UpdateDirectionPreview();
        if(_directionResult is not null)Localization.SetText(DirectionStatus,$"识别建议：{Localization.Key(_directionResult.Description)}；当前片段：{Localization.Key(BatchRotate.Direction(_options.Rotation))}\n{Localization.OrientationReason(_directionResult)}");
    }
    private void MirrorChanged(object? sender,RoutedEventArgs e)
    {
        if(!QuickWorkflow || _updating)return;_options.Flip=MirrorCheck.IsChecked==true;UpdateDirectionPreview();
    }
    private void UpdateDirectionPreview()
    {
        if(PreviewTransform is null)return;
        if(!QuickWorkflow || EditTabs.SelectedItem!=DirectionTab){PreviewTransform.LayoutTransform=null;return;}
        var transforms=new TransformGroup();transforms.Children.Add(new RotateTransform(_options.Rotation));
        if(_options.Flip)transforms.Children.Add(new ScaleTransform(-1,1));PreviewTransform.LayoutTransform=transforms;
    }
    private void DetectDirectionClick(object? sender,RoutedEventArgs e)=>_directionReady=DetectDirection();
    private async Task DetectDirection()
    {
        if(_info?.HasVideo!=true || _directionDetector is null || _directionCancellation is not null)return;
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_directionCancellation=cancellation;
        DetectDirectionButton.IsEnabled=false;CancelDetectionButton.IsVisible=true;ApplyDirectionButton.IsEnabled=false;_directionResult=null;
        DirectionStatus.Text="正在分析视频人脸方向…";
        try
        {
            var progress=new Progress<OrientationDetectionProgress>(p=>{if(!_closed && !cancellation.IsCancellationRequested)Localization.SetText(DirectionStatus,$"正在分析 {p.CompletedFrames}/{p.TotalFrames} 帧…");});
            var result=await _directionDetector.DetectAsync(_path,_info,progress,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();if(_closed)return;
            _directionResult=result;ApplyDirectionButton.IsEnabled=result.IsCertain;
            DirectionStatus.Text=Localization.Join("\n", new[] { result.IsCertain ? Localization.Format($"建议：{Localization.Key(result.Description)}") : "无法确定方向", Localization.OrientationReason(result) });
        }
        catch(OperationCanceledException){if(!_closed)DirectionStatus.Text="识别已取消";}
        catch(Exception ex){if(!_closed)DirectionStatus.Text=Localization.Format($"方向识别失败：{ex.Message}");}
        finally
        {
            if(ReferenceEquals(_directionCancellation,cancellation))_directionCancellation=null;
            cancellation.Dispose();if(!_closed){DetectDirectionButton.IsEnabled=true;CancelDetectionButton.IsVisible=false;}
        }
    }
    private void CancelDetectionClick(object? sender,RoutedEventArgs e)=>_directionCancellation?.Cancel();
    private void ClearDirectionDetection(){_directionCancellation?.Cancel();_directionResult=null;if(ApplyDirectionButton is not null)ApplyDirectionButton.IsEnabled=false;}
    private void ApplyDirectionClick(object? sender,RoutedEventArgs e)
    {
        if(_directionResult?.Rotation is not (0 or 90 or 180 or 270))return;
        _options.Rotation=_directionResult.Rotation.Value;SyncDirectionControls();
        Localization.SetText(DirectionStatus,$"已应用到当前片段：{Localization.Key(_directionResult.Description)}\n{Localization.OrientationReason(_directionResult)}");
    }
}
