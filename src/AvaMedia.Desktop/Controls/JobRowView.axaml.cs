using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public partial class JobRowView : UserControl
{
    // Limit background FFmpeg processes even when many rows become visible at once.
    private static readonly SemaphoreSlim PreviewSlots = new(2);
    private MainWindow? _owner;
    private JobRowDetails? _details;
    private CancellationTokenSource? _load;
    private PreviewKey? _key;
    private int _refreshPosted, _fullRefresh;
    private Point? _outputPressed;
    private IPointer? _outputPointer;
    private bool _draggingOutput;
    private Task _previewReady = Task.CompletedTask;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public JobRowDetails? Details => _details;
    public JobRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindJob();
        AttachedToVisualTree += (_, _) => BindJob();
        DetachedFromVisualTree += (_, _) => Release();
        OutputDragHandle.AddHandler(PointerPressedEvent, OutputPointerPressed, RoutingStrategies.Tunnel);
        OutputDragHandle.PointerMoved += OutputPointerMoved;
        OutputDragHandle.PointerReleased += (_, _) => ResetOutputPointer();
        OutputDragHandle.PointerCaptureLost += (_, _) => ResetOutputPointer();
        OutputDragHandle.DoubleTapped += (_, e) => { if (OutputDragGrip.IsVisible) e.Handled = true; };
    }
    private void BindJob()
    {
        Release();
        _owner = this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault();
        if (_owner is null || DataContext is not Job job) return;
        _details = new(job);
        RowRoot.DataContext = _details;
        job.PropertyChanged += JobChanged;
        _owner.JobDisplayChanged += Refresh;
        _owner.JobPresentationChanged += PresentationChanged;
        Refresh();
    }
    private void JobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Job.Progress) or nameof(Job.Status) or nameof(Job.ProgressDetail)
            or nameof(Job.Estimate) or nameof(Job.RemainingTimeText) or nameof(Job.Activity))) Interlocked.Exchange(ref _fullRefresh, 1);
        if (Interlocked.Exchange(ref _refreshPosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPosted, 0);
            var full = Interlocked.Exchange(ref _fullRefresh, 0) != 0;
            if (!ReferenceEquals(sender, _details?.Job)) return;
            if (full) Refresh(); else { _details?.RefreshProgress(); RefreshActivity(); }
        });
    }
    private void Refresh()
    {
        if (_details is null || _owner is null) return;
        if (!_owner.IsQueuePresentationVisible) { SuspendPreview(); return; }
        _details.Refresh();
        RefreshActivity();
        Ready = Task.WhenAll(_details.MetadataReady, _previewReady);
        foreach (var state in Enum.GetValues<JobState>()) StateText.Classes.Set(state.ToString().ToLowerInvariant(), state == _details.Job.State);
        var job = _details.Job;
        var canDrag = _owner.CanDragOutput(job);
        OutputDragGrip.IsVisible = canDrag;
        OutputDragHandle.Cursor = canDrag ? new Cursor(StandardCursorType.Hand) : null;
        ToolTip.SetTip(OutputDragGrip, Localization.Text("拖到左侧工具继续处理"));
        ToolTip.SetTip(OutputDragHandle, canDrag ? Localization.Text("拖到左侧工具继续处理") : null);
        ViewResultButton.IsVisible = _owner.CanViewSummaryResult(job);
        CoverButton.IsEnabled = _owner.CanEditTask(job);
        var path = job.FeatureId=="download" ? job.State == JobState.Completed ? job.Output : "" : job.Inputs.FirstOrDefault() ?? "";
        var o = job.InputOptions?.FirstOrDefault() ?? job.Options;
        var key = new PreviewKey(path, o.VideoStreamIndex, o.AudioStreamIndex, o.Start, o.End, _owner.Engine.Settings.FFmpegPath, _owner.Engine.Settings.FFprobePath);
        if (key == _key) return;
        _key = key;
        _load?.Cancel(); _load = null;
        _previewReady = Task.CompletedTask; Ready = _details.MetadataReady;
        _details.SetMedia(null, "");
        var feature = Catalog.Find(job.FeatureId);
        if (path.Length == 0) { _details.SetMedia(null, job.FeatureId == "download" ? "下载完成后读取媒体信息" : "等待生成媒体"); return; }
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (feature.Category is "文档" or "光驱设备\\DVD\\CD\\ISO" || !(QuickClipBatch.VideoExtensions.Contains(extension) || MediaEngine.IsAudio(extension) || MediaEngine.IsImage(extension) || extension is "jpeg" or "tif" or "gif"))
        { _details.SetMedia(null, "文件任务"); return; }
        _details.SetMedia(null, "正在读取媒体信息…");
        _load = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _previewReady = LoadAsync(_details, _owner.Engine, key, _load);
        Ready = Task.WhenAll(_details.MetadataReady, _previewReady);
    }
    private void RefreshActivity()
    {
        var job = _details?.Job;
        ActivityView.Update(_owner?.IsQueuePresentationVisible == true ? job?.Activity : null);
        PipelineProgress.IsVisible = job?.Activity is null;
        StateLine.IsVisible = job is not { State: JobState.Running, Activity: not null };
    }
    private async Task LoadAsync(JobRowDetails details, IMediaEngine engine, PreviewKey key, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool acquired = false;
        MediaInfo? media = null;
        try
        {
            await PreviewSlots.WaitAsync(token); acquired = true;
            if (!await Task.Run(() => File.Exists(key.Path), token))
            { if (ReferenceEquals(details, _details)) details.SetMedia(null, "源文件缺失或不可访问"); return; }
            media = await engine.Probe(key.Path, token, key.Video, key.Audio);
            byte[]? cover = null;
            if (media.HasVideo)
            {
                var end = key.End > key.Start ? Math.Min(key.End, media.Duration) : media.Duration;
                var position = media.Duration > 0 ? Math.Clamp(key.Start + Math.Min(1, Math.Max(0, end - key.Start) * .1), 0, Math.Max(0, media.Duration - .05)) : 0;
                cover = await engine.Thumbnail(key.Path, position, 228, 144, token, pad: false, videoStreamIndex: key.Video);
            }
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(media, "", cover);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(details, _details) && ReferenceEquals(cancellation, _load)) details.SetMedia(media, "媒体信息读取超时");
        }
        catch (Exception)
        {
            // Inspection failures never change the job state or prevent conversion.
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(media, "媒体信息不可用，请检查外部工具或源文件");
        }
        finally
        {
            if (acquired) PreviewSlots.Release();
            if (ReferenceEquals(_load, cancellation)) _load = null;
            cancellation.Dispose();
        }
    }
    private async void CoverClick(object? sender, RoutedEventArgs e)
    { e.Handled = true; if (_owner is not null && _details is not null) await _owner.EditJob(_details.Job); }
    private async void ViewResultClick(object? sender, RoutedEventArgs e)
    { e.Handled = true; if (_owner is not null && _details is not null) await _owner.ShowSummaryResultAsync(_details.Job); }
    private void OutputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_draggingOutput || _owner is null || _details is null || !_owner.CanDragOutput(_details.Job)
            || e.KeyModifiers != KeyModifiers.None || !e.GetCurrentPoint(OutputDragHandle).Properties.IsLeftButtonPressed) return;
        _outputPressed = e.GetPosition(OutputDragHandle);
        _outputPointer = e.Pointer;
        e.Pointer.Capture(OutputDragHandle);
        e.Handled = true;
        _owner.SelectOutputForDrag(_details.Job);
    }
    private async void OutputPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_outputPressed is not {} start || _draggingOutput || _owner is null || _details is null) return;
        if (!e.GetCurrentPoint(OutputDragHandle).Properties.IsLeftButtonPressed) { ResetOutputPointer(); return; }
        var delta = e.GetPosition(OutputDragHandle) - start;
        if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
        var owner = _owner; var job = _details.Job;
        _draggingOutput = true; _outputPressed = null; e.Handled = true;
        try
        {
            await owner.DragOutputsAsync(job, e, () =>
            {
                var pressed = _outputPointer is not null;
                ResetOutputPointer(); return pressed;
            });
        }
        finally { ResetOutputPointer(); _draggingOutput = false; }
    }
    private void ResetOutputPointer()
    {
        _outputPressed = null;
        var pointer = _outputPointer; _outputPointer = null;
        if (pointer?.Captured == OutputDragHandle) pointer.Capture(null);
    }
    private void PresentationChanged(bool visible) { if (visible) Refresh(); else SuspendPreview(); }
    private void SuspendPreview()
    {
        ActivityView.Update(null);
        var loading = _load; _load = null; loading?.Cancel(); _key = null;
        _previewReady = Task.CompletedTask; Ready = _details?.MetadataReady ?? Task.CompletedTask;
        _details?.ReleaseCover();
    }
    private void Release()
    {
        ResetOutputPointer();
        _load?.Cancel(); _load = null; _key = null;
        _previewReady = Task.CompletedTask;
        if (_owner is not null)
        { _owner.JobDisplayChanged -= Refresh; _owner.JobPresentationChanged -= PresentationChanged; }
        if (_details is not null) _details.Job.PropertyChanged -= JobChanged;
        RowRoot.DataContext = null;
        ActivityView.Update(null);
        _details?.Dispose(); _details = null; _owner = null;
    }
    private sealed record PreviewKey(string Path, int Video, int Audio, double Start, double End, string FFmpeg, string FFprobe);
}
