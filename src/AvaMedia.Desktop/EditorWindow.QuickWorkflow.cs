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
    private double _sourceDuration;
    public ClipSegmentEntry(ConversionOptions options, string sourcePath = "") { _options = options.Clone(); SourcePath = sourcePath; }
    public string SourcePath { get; }
    public string SourceName => Path.GetFileName(SourcePath);
    public ConversionOptions Options
    {
        get => _options;
        set { _options = value.Clone(); Raise(nameof(Options)); RefreshLabels(); }
    }
    public int Number { get => _number; set { if (_number == value) return; _number = value; RefreshLabels(); } }
    public double SourceDuration { get => _sourceDuration; set { if (_sourceDuration == value) return; _sourceDuration = value; RefreshLabels(); } }
    public double End => _options.End > 0 ? _options.End : _sourceDuration;
    public double OutputDuration => Math.Max(0, End - _options.Start) / Math.Max(.25, _options.Speed);
    public string Title => Localization.Format($"片段 {Number}");
    public string DurationLabel => MediaTime.Format(OutputDuration) + " · " + MediaEngine.Number(_options.Speed) + "×";
    public string TimeRange => MediaTime.Format(_options.Start) + " → " + (End > 0 ? MediaTime.Format(End) : Localization.Text("视频结尾"));
    public string EditSummary => Localization.Join(" · ", new[] {
        _options.CropWidth > 0 ? Localization.Format($"裁剪 {_options.CropWidth}×{_options.CropHeight}") : "",
        _options.Rotation != 0 ? BatchRotate.Direction(_options.Rotation) : "",
        _options.Flip ? "水平镜像" : "",
        _options.DelogoWidth > 0 ? "去水印" : ""
    }.Where(s => s.Length > 0));
    public bool HasEdits => EditSummary.Length > 0;
    public string Summary => Title + " · " + TimeRange + "\n" + DurationLabel + (HasEdits ? "\n" + EditSummary : "");
    private void RefreshLabels()
    {
        foreach (var property in new[] { nameof(Title), nameof(DurationLabel), nameof(TimeRange), nameof(EditSummary), nameof(HasEdits), nameof(Summary) }) Raise(property);
    }
}

public partial class EditorWindow
{
    private readonly ObservableCollection<ClipSegmentEntry> _segments = [];
    private ClipSegmentEntry? _activeSegment;
    private bool _selectingSegment;
    private IVideoOrientationDetector? _directionDetector;
    private CancellationTokenSource? _directionCancellation;
    private VideoOrientationResult? _directionResult;
    private Task _directionReady = Task.CompletedTask;
    private bool QuickWorkflow => _mode == "quick-workflow";
    public IReadOnlyList<ClipSegmentEntry> Segments => _segments;
    public Task DirectionReady => _directionReady;
    public void SetWorkflowCompletion(string action) => ConfirmButton.Content=Localization.Text(action);

    private void InitializeQuickWorkflow(IReadOnlyList<ConversionOptions>? segments, IVideoOrientationDetector? detector)
    {
        if (!QuickWorkflow) return;
        _directionDetector = detector ?? new VideoOrientationDetector(_engine);
        DirectionTab.IsVisible = SegmentFooter.IsVisible = SegmentPane.IsVisible = SegmentPaneSplitter.IsVisible = SegmentTrackScroll.IsVisible = LoopSegmentCheck.IsVisible = true;
        EditorLayout.ColumnDefinitions[1].Width = new Avalonia.Controls.GridLength(10);
        EditorLayout.ColumnDefinitions[2].Width = new Avalonia.Controls.GridLength(320);
        EditorLayout.ColumnDefinitions[0].MinWidth = 620;
        EditorLayout.ColumnDefinitions[2].MinWidth = 290;
        EditorLayout.ColumnDefinitions[2].MaxWidth = 450;
        TotalTime.IsVisible = false;
        Width = 1380;
        EditOptionsButton.Content = "其他编辑选项…";
        PreviewNote.Text = "速度与方向可预览；裁剪、淡入淡出及其他效果在输出时生效。";
        ConfirmButton.Content = "下一步：导出选项";
        DirectionCombo.ItemsSource = new[] { "保持原方向", "顺时针 90°", "旋转 180°", "逆时针 90°" };
        Avalonia.Automation.AutomationProperties.SetName(DirectionCombo, "当前片段旋转方向");
        Avalonia.Automation.AutomationProperties.SetName(SegmentList, "视频片段列表");
        Avalonia.Automation.AutomationProperties.SetName(SegmentTrack, "片段顺序与播放位置");
        SegmentList.ItemsSource = _segments;
        foreach (var draft in segments ?? []) _segments.Add(new(draft, _path));
        if (_segments.Count == 0) _segments.Add(new(_options, _path));
        _activeSegment = _segments[0]; _options = _activeSegment.Options.Clone();
        _selectingSegment = true; SegmentList.SelectedItem = _activeSegment; _selectingSegment = false;
        SyncDirectionControls(); InitializeSegmentEditing(); RefreshSegments();
        Closed += (_, _) => { _directionCancellation?.Cancel(); _segmentEditTimer.Stop(); };
    }

    public void SetWorkflowStep(int current, int total)
    {
        Localization.SetTitle(this, $"快速剪辑 · 编辑 {current}/{total} · {Path.GetFileName(_path)}");
        ConfirmButton.Content = current < total ? "下一个视频 →" : "下一步：导出选项 →";
    }

    private void CommitActiveSegment()
    {
        _segmentEditTimer.Stop();
        if (_activeSegment is null) return;
        var draft = ReadDraft();
        if (SameSegmentOptions(_activeSegment.Options, draft)) return;
        RememberSegmentEdit(); _activeSegment.Options = draft;
        _options = draft.Clone(); if (_options.End == 0 && _info is not null) _options.End = _info.Duration;
        TrimBar.Start = _options.Start; TrimBar.End = _options.End; TrimBar.InvalidateVisual(); RefreshSegments();
    }

    private void RefreshSegments()
    {
        if (!QuickWorkflow) return;
        for (var i = 0; i < _segments.Count; i++) { _segments[i].Number = i + 1; _segments[i].SourceDuration = _info?.Duration ?? 0; }
        SegmentCount.Text = Localization.Format($"{_segments.Count} 个片段");
        SegmentTotalDuration.Text = Localization.Format($"总输出时长：{MediaTime.Format(_segments.Sum(s => s.OutputDuration))}");
        ActiveSegmentLabel.Text = _activeSegment?.Title ?? "";
        var index = _activeSegment is null ? -1 : _segments.IndexOf(_activeSegment);
        RemoveSegmentButton.IsEnabled = index >= 0 && _segments.Count > 1;
        SegmentUpButton.IsEnabled = index > 0;
        SegmentDownButton.IsEnabled = index >= 0 && index < _segments.Count - 1;
        SegmentTrack.SetItems(_segments.Select(s => new Controls.SegmentTimelineItem(s.Number, s.Options.Start, s.End, s.Options.Speed)).ToArray(), index);
        UpdateSegmentActions();
    }

    private void AddSegmentClick(object? sender, RoutedEventArgs e) => InsertSegment(duplicate: false);
    private void DuplicateSegmentClick(object? sender, RoutedEventArgs e) => InsertSegment(duplicate: true);

    private void InsertSegment(bool duplicate)
    {
        if (!QuickWorkflow || _closed || _info?.Duration is not > 0 || _activeSegment is null) return;
        try
        {
            CommitActiveSegment();
            var draft = _activeSegment.Options.Clone();
            if (!duplicate)
            {
                draft.Start = double.IsFinite(_position) && _position >= 0 && _position < _info.Duration ? _position : 0;
                draft.End = 0;
            }
            RememberSegmentEdit();
            var entry = new ClipSegmentEntry(draft, _path);
            _selectingSegment = true; _segments.Insert(_segments.IndexOf(_activeSegment) + 1, entry); _selectingSegment = false;
            _segmentReady = SelectSegment(entry); SegmentError.Text = "";
        }
        catch (Exception ex) { SegmentError.Text = ex.Message; }
    }

    private void SegmentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_selectingSegment || !QuickWorkflow || _info is null || ReferenceEquals(SegmentList.SelectedItem, _activeSegment)) return;
        if (SegmentList.SelectedItem is ClipSegmentEntry entry) _segmentReady = SelectSegment(entry);
    }

    private void RemoveSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (_activeSegment is null || _segments.Count <= 1) return;
        try
        {
            CommitActiveSegment(); RememberSegmentEdit();
            var index = _segments.IndexOf(_activeSegment);
            _selectingSegment = true; _segments.RemoveAt(index); _activeSegment = null; _selectingSegment = false;
            _segmentReady = SelectSegment(_segments[Math.Min(index, _segments.Count - 1)], skipCommit: true);
        }
        catch (Exception ex) { SegmentError.Text = ex.Message; }
    }

    private void MoveSegment(int delta)
    {
        if (_activeSegment is null) return;
        try
        {
            CommitActiveSegment(); var index = _segments.IndexOf(_activeSegment); var target = index + delta;
            if (target < 0 || target >= _segments.Count) return;
            CancelSegmentSequence(); RememberSegmentEdit();
            _selectingSegment = true; _segments.Move(index, target); SegmentList.SelectedItem = _activeSegment; _selectingSegment = false;
            SegmentList.ScrollIntoView(_activeSegment); RefreshSegments(); SegmentError.Text = "";
        }
        catch (Exception ex) { SegmentError.Text = ex.Message; }
    }
    private void SegmentUpClick(object? sender, RoutedEventArgs e) => MoveSegment(-1);
    private void SegmentDownClick(object? sender, RoutedEventArgs e) => MoveSegment(1);

    private async void SplitSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (_info is null || _activeSegment is null) return;
        try
        {
            CommitActiveSegment(); Seek(_position); var entry = _activeSegment;
            var parts = await new ClipSplitWindow(entry.Options, _info.Duration).ShowDialog<IReadOnlyList<ConversionOptions>?>(this);
            if (parts is null || _closed) return;
            var index = _segments.IndexOf(entry); if (index < 0) return;
            ReplaceSegment(index, parts);
        }
        catch (Exception ex) { SegmentError.Text = ex.Message; }
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
        _options.Rotation=DirectionCombo.SelectedIndex*90;UpdateDirectionPreview();ScheduleSegmentUpdate();
        if(_directionResult is not null)Localization.SetText(DirectionStatus,$"识别建议：{Localization.Key(_directionResult.Description)}；当前片段：{Localization.Key(BatchRotate.Direction(_options.Rotation))}\n{Localization.OrientationReason(_directionResult)}");
    }
    private void MirrorChanged(object? sender,RoutedEventArgs e)
    {
        if(!QuickWorkflow || _updating)return;_options.Flip=MirrorCheck.IsChecked==true;UpdateDirectionPreview();ScheduleSegmentUpdate();
    }
    private void UpdateDirectionPreview()
    {
        if(PreviewTransform is null)return;
        if(!QuickWorkflow || CropLayer.Enabled){PreviewTransform.LayoutTransform=null;return;}
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
        _options.Rotation=_directionResult.Rotation.Value;SyncDirectionControls();ScheduleSegmentUpdate();
        Localization.SetText(DirectionStatus,$"已应用到当前片段：{Localization.Key(_directionResult.Description)}\n{Localization.OrientationReason(_directionResult)}");
    }
}
