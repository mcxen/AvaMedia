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
    private CancellationTokenSource? _load;
    private PreviewKey? _key;
    private int _refreshPosted, _fullRefresh;
    private Point? _outputPressed;
    private IPointer? _outputPointer;
    private Control? _outputDragSource;
    private bool _draggingOutput;
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
        EnableOutputDrag(OutputDragHandle);
        EnableOutputDrag(CoverButton);
        CoverButton.KeyDown += (_, _) => _suppressCoverClick = false;
        OutputDragHandle.DoubleTapped += (_, e) => { if (OutputDragGrip.IsVisible) e.Handled = true; };
    }
    private void EnableOutputDrag(Control source)
    {
        // Let the cover button handle a normal press before preventing row selection changes.
        source.AddHandler(PointerPressedEvent, OutputPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(PointerMovedEvent, OutputPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        source.AddHandler(PointerReleasedEvent, (_, _) => ResetOutputPointer(), RoutingStrategies.Bubble, handledEventsToo: true);
        source.PointerCaptureLost += (_, _) => ResetOutputPointer();
    }
    private void BindJob()
    {
        Release();
        _owner = this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault();
        if (_owner is null || DataContext is not Job job) return;
        _details = new(job);
        ProgressSlot.Height = _owner.CanViewAiTask(job) ? 54 : 5;
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
        CoverDragGrip.IsVisible = canDrag;
        OutputDragHandle.Cursor = canDrag ? new Cursor(StandardCursorType.Hand) : null;
        CoverButton.Cursor = canDrag ? new Cursor(StandardCursorType.Hand) : null;
        var tip = Localization.Text(canDrag
            ? _owner.CanViewAiTask(job) ? "拖拽产出到工具或文件路由 · 单击查看任务" : "拖拽产出到工具或文件路由 · 单击编辑任务"
            : _details.PreviewTip);
        ToolTip.SetTip(CoverButton, tip);
        AutomationProperties.SetName(CoverButton, tip);
        ToolTip.SetTip(OutputDragHandle, canDrag ? Localization.Text("拖拽产出到工具或文件路由") : null);

        ViewResultButton.IsVisible = _owner.CanViewAiTask(job);
        ViewResultButton.Content = Localization.Text(_owner.CanViewAiTask(job) ? "查看任务" : "查看结果");
        CoverButton.IsEnabled = canDrag || _owner.CanEditTask(job) || _owner.CanViewAiTask(job);
        var outputPreview = _details.HasOutputPreview;
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
        if (_draggingOutput || _suppressCoverClick) return;
        if (_owner is null || _details is null) return;
        if (_owner.CanViewAiTask(_details.Job)) await _owner.ShowAiTaskAsync(_details.Job);
        else await _owner.EditJob(_details.Job);
    }
    private async void ViewResultClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_owner is null || _details is null) return;
        await _owner.ShowAiTaskAsync(_details.Job);
    }
    private void OutputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _suppressCoverClick = false;
        if (_draggingOutput || _owner is null || _details is null || !_owner.CanDragOutput(_details.Job)
            || sender is not Control source || e.KeyModifiers != KeyModifiers.None || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
        _outputDragSource = source;
        _outputPressed = e.GetPosition(source);
        _outputPointer = e.Pointer;
        e.Pointer.Capture(source);
        e.Handled = true;
        _owner.SelectOutputForDrag(_details.Job);
    }
    private async void OutputPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_outputPressed is not {} start || _outputDragSource is not {} source || _draggingOutput || _owner is null || _details is null) return;
        if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) { ResetOutputPointer(); return; }
        var delta = e.GetPosition(source) - start;
        if (delta.X * delta.X + delta.Y * delta.Y < 36) return;
        var owner = _owner; var job = _details.Job;
        _suppressCoverClick = source == CoverButton;
        _draggingOutput = true; _outputPressed = null; e.Handled = true;
        try
        {
            await owner.DragOutputsAsync(job, e, () =>
            {
                var pressed = _outputPointer is not null && ReferenceEquals(_details?.Job, job);
                ResetOutputPointer(); return pressed;
            });
        }
        finally { ResetOutputPointer(); _draggingOutput = false; }
    }
    private void ResetOutputPointer()
    {
        _outputPressed = null;
        var pointer = _outputPointer; _outputPointer = null;
        var source = _outputDragSource; _outputDragSource = null;
        if (source is not null && pointer?.Captured == source) pointer.Capture(null);
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
    private static bool IsPreviewMedia(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return QuickClipBatch.VideoExtensions.Contains(extension) || MediaEngine.IsAudio(extension)
            || MediaEngine.IsImage(extension) || extension is "jpeg" or "tif" or "gif";
    }
    private sealed record PreviewKey(string Path, int Video, int Audio, double Start, double End, bool Directory, string FFmpeg, string FFprobe);
}
