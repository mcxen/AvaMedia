using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed partial class SubtitleStyleEditor
{
    private Point? _customPosition;
    private Vector _dragOffset;
    private bool _dragging, _changingTime, _disposed;
    private readonly Slider _seek = new() { Name = "SubtitlePreviewSeek", Minimum = 0, Maximum = 1 };
    private readonly TextBox _time = new() { Name = "SubtitlePreviewTime", Width = 140 };
    private readonly StackPanel _timeline = new() { Spacing = 6, IsVisible = false };
    private readonly TextBlock _previewNotice = Ui.Text("", "caption");
    private readonly TextBlock _totalTime = Ui.Text("", "caption");
    private CancellationTokenSource? _videoSource, _seekFrame;
    private IMediaEngine? _previewEngine;
    private string? _previewPath;
    private MediaInfo? _previewInfo;
    private int _videoStream, _videoRevision, _frameRevision;
    private int _previewReferenceHeight;
    private double _initialPreviewTime;
    public Task PreviewReady { get; private set; } = Task.CompletedTask;

    private void SetupPreview(StackPanel preview, int referenceHeight, double initialTime)
    {
        _previewReferenceHeight = referenceHeight; _initialPreviewTime = initialTime;
        _screen.Name = "SubtitlePreviewScreen"; _screen.Focusable = true; _screen.Cursor = new(StandardCursorType.SizeAll);
        _screen.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_screen).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(_screen); var center = CaptionCenter();
            var size = _caption.Bounds.Size;
            _dragOffset = new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height).Contains(point) ? point - center : default;
            _dragging = true; e.Pointer.Capture(_screen); _screen.Focus(); PlaceCaption(point - _dragOffset); e.Handled = true;
        };
        _screen.PointerMoved += (_, e) => { if (_dragging) { PlaceCaption(e.GetPosition(_screen) - _dragOffset); e.Handled = true; } };
        _screen.PointerReleased += (_, e) => { if (_dragging) { _dragging = false; e.Pointer.Capture(null); e.Handled = true; } };
        _screen.PointerCaptureLost += (_, _) => _dragging = false;
        _screen.KeyDown += (_, e) =>
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var delta = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => default };
            if (delta == default) return;
            PlaceCaption(CaptionCenter() + delta); e.Handled = true;
        };
        AutomationProperties.SetHelpText(_screen, "拖动字幕调整位置");
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(_seek); Grid.SetColumn(_time, 1); row.Children.Add(_time);
        ToolTip.SetTip(_seek, "预览时间"); ToolTip.SetTip(_time, "预览时间");
        _time.Width = 110;
        _totalTime.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        _timeline.Children.Add(row); _timeline.Children.Add(_totalTime); preview.Children.Add(_timeline);
        _previewNotice.IsVisible = false; preview.Children.Add(_previewNotice);
        _seek.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !_changingTime) PreviewReady = SeekAsync(_seek.Value, true); };
        _time.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ReadPreviewTime(); e.Handled = true; } };
        _time.LostFocus += (_, _) => ReadPreviewTime();
    }

    private Point CaptionCenter() => new(Canvas.GetLeft(_caption) + _caption.Width / 2, Canvas.GetTop(_caption) + _caption.Height / 2);
    private void PlaceCaption(Point point)
    {
        var halfWidth = Math.Min(_caption.Bounds.Width / 2, _screen.Width / 2);
        var halfHeight = Math.Min(_caption.Bounds.Height / 2, _screen.Height / 2);
        _customPosition = new(Math.Clamp(point.X, halfWidth, _screen.Width - halfWidth) / _screen.Width,
            Math.Clamp(point.Y, halfHeight, _screen.Height - halfHeight) / _screen.Height);
        _alignment = 5; foreach (var button in _positions) button.IsChecked = false; UpdatePreview();
    }

    public Task SetVideoAsync(IMediaEngine engine, string? path, int videoStreamIndex = 0, CancellationToken ct = default)
    {
        _videoSource?.Cancel(); _seekFrame?.Cancel(); _videoSource?.Dispose();
        _videoSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var revision = ++_videoRevision;
        _previewEngine = engine; _previewPath = path; _videoStream = videoStreamIndex; _previewInfo = null;
        _timeline.IsVisible = false; _previewNotice.IsVisible = false;
        _frame.Source = null; _bitmap?.Dispose(); _bitmap = null;
        _previewAspect = 16d / 9; _referenceHeight = _previewReferenceHeight; FitPreview(); UpdatePreview();
        PreviewReady = LoadVideoAsync(revision, _videoSource.Token); return PreviewReady;
    }

    private async Task LoadVideoAsync(int revision, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_previewPath) || _disposed) return;
        try
        {
            var info = await _previewEngine!.Probe(_previewPath, ct, _videoStream);
            if (ct.IsCancellationRequested || _disposed || revision != _videoRevision || !info.HasVideo) return;
            _previewInfo = info;
            _changingTime = true;
            _seek.Maximum = Math.Max(0, info.Duration); _seek.Value = Math.Clamp(_initialPreviewTime, 0, _seek.Maximum);
            _time.Text = EditorTime.Format(_seek.Value); _totalTime.Text = EditorTime.Format(info.Duration);
            _changingTime = false; _timeline.IsVisible = info.Duration > 0;
            await SeekAsync(_seek.Value, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed && revision == _videoRevision) ShowPreviewError(error.Message); }
    }

    private void ReadPreviewTime()
    {
        if (_changingTime || _previewInfo is null || _disposed) return;
        if (!EditorTime.TryRead(_time.Text, _seek.Value, out var seconds) || seconds < 0 || seconds > _seek.Maximum)
        { ShowPreviewError("请输入视频时长内的预览时间。"); return; }
        if (seconds == _seek.Value) return;
        PreviewReady = SeekAsync(seconds, false);
    }

    private async Task SeekAsync(double seconds, bool debounce)
    {
        if (_previewInfo is not {} info || _videoSource is null || _disposed) return;
        _seekFrame?.Cancel(); _seekFrame?.Dispose(); _seekFrame = CancellationTokenSource.CreateLinkedTokenSource(_videoSource.Token);
        var ct = _seekFrame.Token; var revision = ++_frameRevision; var videoRevision = _videoRevision;
        var path = _previewPath!; var engine = _previewEngine!; var stream = _videoStream;
        _changingTime = true; _seek.Value = seconds; _time.Text = EditorTime.Format(seconds); _changingTime = false;
        try
        {
            if (debounce) await Task.Delay(120, ct);
            var frame = await engine.Thumbnail(path, seconds, 960, 540, ct, pad: false, videoStreamIndex: stream, endExclusive: info.Duration > 0 && seconds >= info.Duration);
            if (ct.IsCancellationRequested || _disposed || revision != _frameRevision || videoRevision != _videoRevision) return;
            SetFrame(frame, info.Width, info.Height, sourceResolution: _previewReferenceHeight != 288); _previewNotice.IsVisible = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed && revision == _frameRevision && videoRevision == _videoRevision) ShowPreviewError(error.Message); }
    }

    private void ShowPreviewError(string message)
    { _previewNotice.Text = Localization.Format($"预览失败：{message}"); _previewNotice.IsVisible = true; }
}
