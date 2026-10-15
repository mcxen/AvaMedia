using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public partial class JobRowView : UserControl
{
    private MainWindow? _owner;
    private JobRowDetails? _details;
    private TaskRowStatus? _status;
    private JobStageSummary? _summary;
    private TaskStageCard? _stageCard;
    private CancellationTokenSource? _load;
    private PreviewKey? _key;
    private int _refreshPosted, _fullRefresh;
    private Point? _dragPressed;
    private IPointer? _dragPointer;
    private Control? _dragSource;
    private RowDrag _dragKind;
    private bool _dragging;
    private bool _suppressCoverClick;
    private Task _previewReady = Task.CompletedTask;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public JobRowDetails? Details => _details;
    public JobRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindJob();
        AttachedToVisualTree += (_, _) => BindJob();
        DetachedFromVisualTree += (_, _) => Release();
        EnableRowDrag(OutputDragHandle);
        EnableRowDrag(CoverButton);
        EnableRowDrag(InputDragHandle);
        CoverButton.KeyDown += (_, _) => _suppressCoverClick = false;
        OutputDragHandle.DoubleTapped += (_, e) => { if (_details is { } details && _owner?.CanDragOutput(details.Job) == true) e.Handled = true; };
        ToolTip.AddToolTipOpeningHandler(StatusArea, (_, _) => UpdateStageCard());
    }
    private void EnableRowDrag(Control source)
    {
        // Let the cover button handle a normal press before preventing row selection changes.
        source.AddHandler(PointerPressedEvent, DragPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(PointerMovedEvent, DragPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            // A plain click (no drag) on a column of a multi-selected row selects just that row, like any list click.
            if (_dragPressed is not null && source != CoverButton && _details is { } details) _owner?.SelectSingleRow(details.Job);
            ResetDragPointer();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        source.PointerCaptureLost += (_, _) => ResetDragPointer();
    }
    private enum RowDrag { None, Input, Output }
    /// <summary>Which files a press on <paramref name="source"/> drags: the thumbnail and output column carry the
    /// outputs of a finished task; the thumbnail (otherwise) and the file column carry the inputs in every state.</summary>
    private RowDrag DragKindAt(Control source)
    {
        if (_owner is null || _details is null) return RowDrag.None;
        var job = _details.Job;
        var output = _owner.CanDragOutput(job);
        if (source == OutputDragHandle) return output ? RowDrag.Output : RowDrag.None;
        if (source == CoverButton && output) return RowDrag.Output;
        return _owner.CanDragInput(job) ? RowDrag.Input : RowDrag.None;
    }
    /// <summary>Design captures: show the hover affordances (row highlight and thumbnail grip) without a pointer.</summary>
    internal void PreviewHover(bool hover)
    {
        RowRoot.Classes.Set("show-grip", hover);
        this.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.Classes.Set("hover-preview", hover);
    }
    private void BindJob()
    {
        Release();
        _owner = this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault();
        if (_owner is null || DataContext is not Job job) return;
        _details = new(job);
        _details.PropertyChanged += DetailsChanged;
        RowRoot.DataContext = _details;
        job.PropertyChanged += JobChanged;
        _owner.JobDisplayChanged += Refresh;
        _owner.JobPresentationChanged += PresentationChanged;
        Refresh();
    }
    private void JobChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Live status arrives as throttled StageSummary publishes; any other property refreshes the whole row.
        if (e.PropertyName is not (nameof(Job.StageSummary) or nameof(Job.Progress) or nameof(Job.Status) or nameof(Job.ProgressDetail)
            or nameof(Job.Estimate) or nameof(Job.RemainingTimeText) or nameof(Job.Activity) or nameof(Job.DownloadSpeed))) Interlocked.Exchange(ref _fullRefresh, 1);
        if (e.PropertyName is nameof(Job.Progress) or nameof(Job.Status) or nameof(Job.ProgressDetail) or nameof(Job.Estimate)
            or nameof(Job.RemainingTimeText) or nameof(Job.Activity) or nameof(Job.DownloadSpeed)) return;
        if (Interlocked.Exchange(ref _refreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPosted, 0);
            var full = Interlocked.Exchange(ref _fullRefresh, 0) != 0;
            if (!ReferenceEquals(sender, _details?.Job)) return;
            if (full) Refresh(); else RefreshStatus();
        });
    }
    // Output sizes arrive asynchronously and feed the completed status line.
    private void DetailsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or null) || !ReferenceEquals(sender, _details)) return;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(sender, _details)) RefreshStatus(); });
    }
    private void Refresh()
    {
        if (_details is null || _owner is null) return;
        if (!_owner.IsQueuePresentationVisible) { SuspendPreview(); return; }
        _details.Refresh();
        RefreshStatus();
        Ready = Task.WhenAll(_details.MetadataReady, _previewReady);
        var job = _details.Job;
        var canDragOutput = _owner.CanDragOutput(job);
        var canDragInput = _owner.CanDragInput(job);
        RowRoot.Classes.Set("can-drag", canDragOutput || canDragInput);
        Cursor? Hand(bool on) => on ? new Cursor(StandardCursorType.Hand) : null;
        OutputDragHandle.Cursor = Hand(canDragOutput);
        InputDragHandle.Cursor = Hand(canDragInput);
        CoverButton.Cursor = Hand(canDragOutput || canDragInput);
        var tip = Localization.Text(canDragOutput ? "拖拽产出到工具或文件路由 · 单击查看任务详情"
            : canDragInput ? "拖拽源文件到工具或其他应用 · 单击" + _details.PreviewTip
            : _details.PreviewTip);
        ToolTip.SetTip(CoverButton, tip);
        AutomationProperties.SetName(CoverButton, tip);
        ToolTip.SetTip(OutputDragHandle, canDragOutput ? Localization.Text("拖拽产出到工具或文件路由") : null);

        RefreshRowActions(job);
        ViewResultButton.IsVisible = _owner.CanViewTaskDetails(job);
        CoverButton.IsEnabled = canDragOutput || canDragInput || _owner.CanViewTaskDetails(job);
        // The thumbnail is the row's fixed identity: the input media in every state (a download shows its file once done).
        var outputPreview = _details.CoverShowsOutput;
        var path = outputPreview ? job.Output : job.FeatureId == "download" ? "" : job.Inputs.FirstOrDefault() ?? "";
        var o = job.InputOptions?.FirstOrDefault() ?? job.Options;
        var directory = outputPreview && Directory.Exists(path);
        // Source stream selections and trim positions do not describe the produced file.
        var key = new PreviewKey(path, outputPreview ? 0 : o.VideoStreamIndex, outputPreview ? 0 : o.AudioStreamIndex,
            outputPreview ? 0 : o.Start, outputPreview ? 0 : o.End, directory,
            _owner.Engine.Settings.FFmpegPath, _owner.Engine.Settings.FFprobePath);
        if (key == _key) return;
        _key = key;
        _load?.Cancel(); _load = null;
        _previewReady = Task.CompletedTask; Ready = _details.MetadataReady;
        _details.SetMedia(null, "");
        var feature = Catalog.Find(job.FeatureId);
        if (path.Length == 0) { _details.SetMedia(null, job.FeatureId == "download" ? "下载完成后读取媒体信息" : "等待生成媒体"); return; }
        if (feature.Category is "文档" or "光驱设备\\DVD\\CD\\ISO" || (!directory && !IsPreviewMedia(path)))
        { _details.SetMedia(null, "文件任务"); return; }
        _details.SetMedia(null, "正在读取媒体信息…");
        _load = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _previewReady = LoadAsync(_details, _owner.Engine, key, _load);
        Ready = Task.WhenAll(_details.MetadataReady, _previewReady);
    }
    private void RefreshStatus()
    {
        if (_details is not { } details || _owner is null) return;
        var job = details.Job;
        JobStageSummaries.Attach(job);
        var summary = job.StageSummary;
        var status = TaskRowStatus.From(job, summary, details.CompletedDetail);
        _status = status; _summary = summary;
        RefreshRowActions(job);
        TaskStatePill.State = status.Pill; TaskStatePill.Text = status.PillText;
        StateText.Text = status.PillText;
        foreach (var state in Enum.GetValues<JobState>()) StateText.Classes.Set(state.ToString().ToLowerInvariant(), state == status.State);
        StageTitleText.Text = status.Title; StageTitleText.IsVisible = status.Title.Length > 0;
        StageTitleText.Classes.Set("running", status.State == JobState.Running);
        CompactStateText.Text = status.Title.Length > 0 && status.Text.Length > 0 ? "· " + status.Text : status.Text;
        CompactStateText.Classes.Set("failed", status.IsError);
        StateMetaText.Text = status.Meta; StateMetaText.IsVisible = status.Meta.Length > 0;
        if (status.Segments is { } segments)
        {
            StageBar.Update(segments); StageBar.IsVisible = true; PipelineProgress.IsVisible = false;
            ToolTip.SetTip(StatusArea, _stageCard ??= new TaskStageCard());
            if (ToolTip.GetIsOpen(StatusArea)) UpdateStageCard();
        }
        else
        {
            StageBar.IsVisible = false;
            PipelineProgress.IsVisible = status.Progress is not null;
            PipelineProgress.IsIndeterminate = status.Progress is double.NaN;
            if (status.Progress is { } value && !double.IsNaN(value)) PipelineProgress.Value = value;
            PipelineProgress.Classes.Set("paused", status.State is JobState.Paused or JobState.Stopping);
            var tip = status.Tip.Length > 0 ? status.Tip : null;
            ToolTip.SetTip(StatusArea, tip);
        }
    }
    private void UpdateStageCard()
    {
        if (_stageCard is null || _status is null || _details is null) return;
        var summary = _summary is { } s && s.State == _details.Job.State ? s : null;
        if (summary is not null) _stageCard.Update(summary, _status);
    }
    private async Task LoadAsync(JobRowDetails details, IMediaEngine engine, PreviewKey key, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var path = key.Path;
        try
        {
            if (key.Directory)
            {
                path = await Task.Run(() => Directory.EnumerateFiles(key.Path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                }).FirstOrDefault(file => { token.ThrowIfCancellationRequested(); return IsPreviewMedia(file); }) ?? "", token);
                if (path.Length == 0)
                {
                    if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(null, "文件任务");
                    return;
                }
            }
            var preview = await TaskPreviewCache.Shared.GetAsync(engine, path, key.Video, key.Audio, key.Start, key.End, token);
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(preview.Media, "", preview.Cover);
        }
        catch (FileNotFoundException error) when (string.Equals(error.FileName, Path.GetFullPath(path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(null, "媒体文件缺失或不可访问");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(details, _details) && ReferenceEquals(cancellation, _load)) details.SetMedia(null, "媒体信息读取超时");
        }
        catch (Exception)
        {
            // Inspection failures never change the job state or prevent conversion.
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(null, "媒体信息不可用，请检查外部工具或源文件");
        }
        finally
        {
            if (ReferenceEquals(_load, cancellation)) _load = null;
            cancellation.Dispose();
        }
    }
    private async void CoverClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_dragging || _suppressCoverClick) return;
        if (_owner is null || _details is null) return;
        var job = _details.Job;
        if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled) await _owner.ShowTaskDetailsAsync(job);
        else if (_owner.CanViewAiTask(job)) await _owner.ShowAiTaskAsync(job);
        else if (job.State is JobState.Waiting or JobState.Paused && _owner.CanEditTask(job)) await _owner.EditJob(job);
        else await _owner.ShowTaskDetailsAsync(job);
    }
    private async void ViewResultClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_owner is null || _details is null) return;
        await _owner.ShowRowTaskAsync(_details.Job);
        if (_details.Job.State == JobState.AwaitingUser) _owner.ShowCurrentTaskPage(_details.Job);
    }
    private void RefreshRowActions(Job job)
    {
        if (_owner is null) return;
        ViewResultButton.Content = Localization.Text(job.State == JobState.AwaitingUser ? "去处理" : "查看任务");
        RunTaskButton.IsVisible = job.State is JobState.Waiting or JobState.Paused or JobState.Failed;
        RunTaskButton.IsEnabled = _owner.CanRunRowTask(job);
        RunTaskIcon.Kind = job.State == JobState.Failed ? "rotate-cw" : "play";
        var retryStage = job.State == JobState.Failed ? _status?.RetryStageTitle ?? "" : "";
        var label = retryStage.Length > 0 ? Localization.Format($"从「{Localization.Key(retryStage)}」重试")
            : Localization.Text(job.State == JobState.Failed ? "重试" : job.State == JobState.Paused ? "恢复" : "开始任务");
        ToolTip.SetTip(RunTaskButton, label); AutomationProperties.SetName(RunTaskButton, label);
        PauseTaskButton.IsVisible = job.State is JobState.Waiting or JobState.Running;
        PauseTaskButton.IsEnabled = _owner.CanPauseRowTask(job);
        StopTaskButton.IsVisible = job.State is JobState.Waiting or JobState.Paused or JobState.Running or JobState.AwaitingUser;
        StopTaskButton.IsEnabled = _owner.CanStopRowTask(job);
    }
    private async void RunTaskClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_owner is null || _details is null) return;
        var job = _details.Job;
        if (job.State == JobState.Failed && _status is { RetryStageId.Length: > 0 } status) await _owner.RetryRowTaskFromStageAsync(job, status.RetryStageId);
        else await _owner.RunRowTaskAsync(job);
    }
    private void PauseTaskClick(object? sender, RoutedEventArgs e)
    { e.Handled = true; if (_details is not null) _owner?.PauseRowTask(_details.Job); }
    private void StopTaskClick(object? sender, RoutedEventArgs e)
    { e.Handled = true; if (_details is not null) _owner?.StopRowTask(_details.Job); }
    private void DragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _suppressCoverClick = false;
        if (_dragging || _owner is null || _details is null || sender is not Control source || e.KeyModifiers != KeyModifiers.None
            || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        var kind = DragKindAt(source);
        if (kind == RowDrag.None) return;
        _dragKind = kind;
        _dragSource = source;
        _dragPressed = e.GetPosition(source);
        _dragPointer = e.Pointer;
        e.Pointer.Capture(source);
        e.Handled = true;
        _owner.SelectRowForDrag(_details.Job);
    }
    private async void DragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPressed is not {} start || _dragSource is not {} source || _dragging || _owner is null || _details is null) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { ResetDragPointer(); return; }
        var delta = e.GetPosition(source) - start;
        if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
        var owner = _owner; var job = _details.Job; var kind = _dragKind;
        _suppressCoverClick = source == CoverButton;
        _dragging = true; _dragPressed = null; e.Handled = true;
        try
        {
            bool Begin()
            {
                var pressed = _dragPointer is not null && ReferenceEquals(_details?.Job, job);
                ResetDragPointer(); return pressed;
            }
            if (kind == RowDrag.Input)
                await owner.DragInputsAsync(job, e, new FileDragVisual(_details.Cover, FileDrag.MediaIcon(job.Inputs.FirstOrDefault() ?? "")), Begin);
            else
                await owner.DragOutputsAsync(job, e, new FileDragVisual(await OutputThumbnailAsync(job.Output), FileDrag.MediaIcon(job.Output)), Begin);
        }
        finally { ResetDragPointer(); _dragging = false; }
    }
    /// <summary>Output thumbnail for the drag ghost when it is ready quickly; otherwise the ghost shows the media-type icon.</summary>
    private async Task<Avalonia.Media.IImage?> OutputThumbnailAsync(string output)
    {
        if (_owner is null || !File.Exists(output) || !IsPreviewMedia(output)) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            var preview = await TaskPreviewCache.Shared.GetAsync(_owner.Engine, output, 0, 0, 0, 0, timeout.Token);
            if (preview.Cover is not { Length: > 0 } bytes) return null;
            using var stream = new MemoryStream(bytes);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 160);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or ArgumentException) { return null; }
    }
    private void ResetDragPointer()
    {
        _dragPressed = null;
        var pointer = _dragPointer; _dragPointer = null;
        var source = _dragSource; _dragSource = null;
        if (source is not null && pointer?.Captured == source) pointer.Capture(null);
    }
    private void PresentationChanged(bool visible) { if (visible) Refresh(); else SuspendPreview(); }
    private void SuspendPreview()
    {
        var loading = _load; _load = null; loading?.Cancel(); _key = null;
        _previewReady = Task.CompletedTask; Ready = _details?.MetadataReady ?? Task.CompletedTask;
        _details?.ReleaseCover();
    }
    private void Release()
    {
        ResetDragPointer();
        _load?.Cancel(); _load = null; _key = null;
        _previewReady = Task.CompletedTask;
        if (_owner is not null)
        { _owner.JobDisplayChanged -= Refresh; _owner.JobPresentationChanged -= PresentationChanged; }
        if (_details is not null) { _details.Job.PropertyChanged -= JobChanged; _details.PropertyChanged -= DetailsChanged; }
        RowRoot.DataContext = null;
        _status = null; _summary = null;
        _details?.Dispose(); _details = null; _owner = null;
    }
    private static bool IsPreviewMedia(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return QuickClipBatch.VideoExtensions.Contains(extension) || MediaEngine.IsAudio(extension)
            || MediaEngine.IsImage(extension) || extension is "jpeg" or "tif" or "gif";
    }
    private sealed record PreviewKey(string Path, int Video, int Audio, double Start, double End, bool Directory, string FFmpeg, string FFprobe);
}
