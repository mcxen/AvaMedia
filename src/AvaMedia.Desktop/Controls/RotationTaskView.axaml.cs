using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Reviews an existing direction task without starting another detection job.</summary>
public sealed partial class RotationTaskView : UserControl, IDisposable
{
    private readonly IMediaEngine? _engine;
    private readonly Job? _task;
    private Job Job => _task ?? throw new InvalidOperationException("Rotation task is not configured.");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RotateTransform _rotation = new();
    private readonly (ToggleButton Button, int Angle)[] _directions;
    private readonly LatestPreviewRequest _previewRequests = new();
    private PreviewDrag? _previewDrag;
    private OrientationTaskResult? _result;
    private MediaInfo? _info;
    private Bitmap? _bitmap;
    private int? _angle;
    private bool _manual, _syncing, _loadingResult, _disposed, _initialized;
    private JobState _state;
    private string? _loadError;
    private int _generation;

    public event EventHandler? ReviewChanged;
    public bool IsReview => _task?.Options.Orientation is not null;
    public bool SettingsExpanded => ExportSettings.IsExpanded;
    public void CollapseSettings() => ExportSettings.IsExpanded = false;
    public bool StartImmediately => QueueOnlyInput.IsChecked != true;
    public string ActionLabel => !IsReview ? "查看结果" : StartImmediately ? "开始旋转" : "加入队列";
    public bool CanExport => !_disposed && _info is not null && _angle is > 0 && _loadError is null
        && IsReview && Job.State is not (JobState.Waiting or JobState.Running or JobState.Stopping or JobState.Paused)
        && !string.IsNullOrWhiteSpace(OutputInput.Text);

    public RotationTaskView()
    {
        InitializeComponent();
        RotationTransform.LayoutTransform = _rotation;
        _directions = [(KeepDirection, 0), (ClockwiseDirection, 90), (CounterclockwiseDirection, 270), (UpsideDownDirection, 180)];
        foreach (var (button, _) in _directions) button.IsEnabled = false;
    }

    public RotationTaskView(IMediaEngine engine, Job job) : this()
    {
        _engine = engine; _task = job; _state = job.State;
        foreach (var (button, angle) in _directions)
            button.Click += (_, _) => { if (_syncing) return; _manual = true; SetAngle(angle); };
        var spec = job.Options.Orientation;
        FormatCombo.ItemsSource = new[] { SourceVideoExport.Original, SourceVideoExport.FastRotation, "mp4", "mkv", "webm", "mov", "avi", "ts" };
        FormatCombo.SelectedItem = spec?.Format ?? job.Options.Format;
        OutputInput.Text = spec?.OutputFolder ?? Path.GetDirectoryName(job.Output);
        SourceOutputInput.IsChecked = spec?.OutputToSource; SettingNameInput.IsChecked = spec?.SettingName;
        ExportSettings.IsVisible = IsReview;
        if (!IsReview) _angle = job.Options.LosslessRotation ?? job.Options.Rotation;
        FormatCombo.SelectionChanged += (_, _) => RefreshExport();
        SourceOutputInput.IsCheckedChanged += (_, _) => RefreshExport();
        QueueOnlyInput.IsCheckedChanged += (_, _) => ReviewChanged?.Invoke(this, EventArgs.Empty);
        ExportSettings.PropertyChanged += (_, args) =>
        { if (args.Property == Expander.IsExpandedProperty) ReviewChanged?.Invoke(this, EventArgs.Empty); };
        OutputInput.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) RefreshExport(); };
        _previewDrag = new(PreviewSeek, () => { if (!_disposed) _ = LoadPreviewAsync(); }); _previewDrag.Attach();
        PreviewSeek.PropertyChanged += (_, args) =>
        {
            if (args.Property != Slider.ValueProperty || _syncing || _info is null) return;
            PreviewTime.Text = Time(PreviewSeek.Value); _ = LoadPreviewAsync(true);
        };
        Localization.Changed += LanguageChanged;
        RefreshExport(); Update();
    }

    public async Task InitializeAsync()
    {
        if (_engine is null || _task is null) return;
        try
        {
            await LoadResultAsync();
            var info = _info ?? await _engine.Probe(Job.Inputs[0], _lifetime.Token, Job.Options.VideoStreamIndex);
            _ = BatchRotate.OutputSize(info, 0);
            if (_disposed) return;
            _info = info; RefreshTimeline(reset: true); _initialized = true;
            RefreshDirection(); await LoadPreviewAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_disposed) return;
            _loadError = error.Message; ShowNotice(Localization.Format($"预览失败：{error.Message}"));
            RefreshDirection();
        }
    }

    public void Update()
    {
        if (_disposed || _task is null) return;
        if (!IsReview) { RefreshDirection(); return; }
        if (_state != Job.State)
        {
            _state = Job.State;
            if (_state is JobState.Waiting or JobState.Running)
            { ++_generation; _manual = false; _angle = null; _result = null; _loadError = null; }
        }
        if (Job.OrientationResult is { } result && !ReferenceEquals(_result, result)) ApplyResult(result);
        else if (Job.State == JobState.Completed && _result is null && !_loadingResult && _loadError is null)
            _ = LoadResultAsync();
        RefreshDirection();
    }

    private async Task LoadResultAsync()
    {
        if (!IsReview || _loadingResult || _result is not null || Job.State != JobState.Completed) return;
        _loadingResult = true; var generation = _generation;
        try
        {
            var result = Job.OrientationResult ?? await AiTaskResults.LoadAsync<OrientationTaskResult>(Job.Output, _lifetime.Token);
            if (_disposed || generation != _generation || Job.State != JobState.Completed) return;
            if (result is null) throw new IOException("方向检测结果缺失，请重新检测。");
            ApplyResult(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        { if (!_disposed && generation == _generation) { _loadError = error.Message; RefreshDirection(); } }
        finally { _loadingResult = false; }
    }

    private void ApplyResult(OrientationTaskResult result)
    {
        var source = new FileInfo(Job.Inputs[0]);
        if (!source.Exists || source.Length != result.SourceLength || source.LastWriteTimeUtc != result.SourceWriteUtc)
        { _loadError = "源视频发生变化，请重新检测方向。"; RefreshDirection(); return; }
        try { _ = BatchRotate.OutputSize(result.Info, result.Detection.Rotation ?? 0); }
        catch (ArgumentException error) { _loadError = error.Message; RefreshDirection(); return; }
        _result = result; _info = result.Info; _loadError = null;
        if (!_manual) _angle = result.Detection.Rotation;
        RefreshDirection();
        if (_initialized) { RefreshTimeline(reset: false); _ = LoadPreviewAsync(); }
    }

    private void RefreshTimeline(bool reset)
    {
        if (_info is null) return;
        _syncing = true;
        PreviewSeek.Maximum = Math.Max(0, _info.Duration - .04);
        PreviewSeek.Value = Math.Clamp(reset ? _info.Duration * .05 : PreviewSeek.Value, 0, PreviewSeek.Maximum);
        _syncing = false;
        PreviewSeek.IsEnabled = true; DurationText.Text = Time(_info.Duration); PreviewTime.Text = Time(PreviewSeek.Value);
    }

    private void SetAngle(int angle)
    { _angle = angle; RefreshDirection(); }

    private void RefreshDirection()
    {
        var active = Job.State is JobState.Waiting or JobState.Running or JobState.Stopping or JobState.Paused;
        _syncing = true;
        foreach (var (button, angle) in _directions)
        { button.IsChecked = angle == _angle; button.IsEnabled = IsReview && _info is not null && !active && _loadError is null; }
        _syncing = false;
        _rotation.Angle = _angle ?? 0;
        DirectionNotice.IsVisible = IsReview && _angle is null;
        DirectionNotice.Text = Localization.Text(active ? "正在检测方向…" : "请选择方向");
        ResultImage.Opacity = DirectionNotice.IsVisible ? .4 : 1;
        if (_info is { } info)
        {
            SourceSize.Text = $"{info.Width} × {info.Height}";
            var size = BatchRotate.OutputSize(info, _angle ?? 0); ResultSize.Text = $"{size.Width} × {size.Height}";
        }
        var detection = _result?.Detection;
        DetectionSummary.Text = _loadError ?? (!IsReview ? Localization.Text(BatchRotate.Direction(_angle ?? 0))
            : active ? Localization.Text("正在检测方向…")
            : _manual && _angle == 0 ? Localization.Text("无需旋转")
            : _manual ? Localization.Text("手动调整")
            : detection is { IsCertain: true } ? Localization.Join(" · ", [Localization.Text(detection.Description), Localization.OrientationReason(detection)])
            : detection is not null ? Localization.OrientationReason(detection) : Localization.Text("请选择方向"));
        DetectionSummary.Classes.Set("error", _loadError is not null);
        UseDetectionButton.IsVisible = _manual && detection?.Rotation is not null && detection.Rotation != _angle && !active && _loadError is null;
        ReviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshExport()
    {
        var format = FormatCombo.SelectedItem as string ?? SourceVideoExport.Original;
        ExportSettings.Header = Localization.Format($"输出设置 · {Localization.Key(format)}");
        OutputInput.IsEnabled = BrowseOutputButton.IsEnabled = SourceOutputInput.IsChecked != true;
        ExportHint.Text = Localization.Text(format == SourceVideoExport.FastRotation ? SourceVideoExport.FastHint
            : format == SourceVideoExport.Original ? SourceVideoExport.OriginalHint : "按所选格式重新编码视频和音频。");
        ReviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public BatchRotateRequest CreateRequest()
    {
        if (!CanExport) throw new ArgumentException("请先确认旋转方向和输出目录。");
        // Recheck at export time; a preview may have remained open while the file changed.
        if (_result is { } result)
        {
            var source = new FileInfo(Job.Inputs[0]);
            if (!source.Exists || source.Length != result.SourceLength || source.LastWriteTimeUtc != result.SourceWriteUtc)
            { _loadError = "源视频发生变化，请重新检测方向。"; RefreshDirection(); throw new IOException(_loadError); }
        }
        var format = FormatCombo.SelectedItem as string ?? SourceVideoExport.Original;
        return new([new(Job.Inputs[0], _info!, _angle)], 0, format, Path.GetFullPath(OutputInput.Text!),
            SourceOutputInput.IsChecked == true, SettingNameInput.IsChecked == true ? format.ToUpperInvariant() : "");
    }

    private Task LoadPreviewAsync(bool debounce = false)
    {
        var seconds = PreviewSeek.Value;
        var size = _previewDrag?.IsDragging == true ? VideoPreviewSizes.Scrub : VideoPreviewSizes.Preview;
        return _previewRequests.Request(token => RenderPreviewAsync(seconds, size, token), _lifetime.Token, immediate: !debounce);
    }

    private async Task RenderPreviewAsync(double seconds, VideoPreviewSize size, CancellationToken token)
    {
        if (_bitmap is null) ShowNotice(Localization.Text("正在读取预览…"));
        try
        {
            if (_info is null || _engine is null) return;
            size = VideoPreviewSizes.Bound(_info, size);
            var bytes = await _engine.Thumbnail(Job.Inputs[0], seconds, size.Width, size.Height, token, pad: false, videoStreamIndex: _info.VideoStreamIndex);
            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes); var bitmap = new Bitmap(stream);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || token.IsCancellationRequested) { bitmap.Dispose(); return; }
                var previous = _bitmap; _bitmap = bitmap;
                SourceImage.Source = ResultImage.Source = bitmap;
                ResultImage.Width = bitmap.Size.Width; ResultImage.Height = bitmap.Size.Height;
                previous?.Dispose(); PreviewNotice.IsVisible = false;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (!_disposed && !token.IsCancellationRequested) ShowNotice(Localization.Format($"预览失败：{error.Message}")); }
    }

    private void ShowNotice(string text)
    { PreviewStatus.Text = text; PreviewNotice.IsVisible = true; }
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");
    private void UseDetectionClick(object? sender, RoutedEventArgs args)
    { if (_result?.Detection.Rotation is { } angle) { _manual = false; SetAngle(angle); } }
    private async void BrowseOutputClick(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && await Ui.Folder(owner, "选择旋转输出目录") is { } folder && !_disposed)
            OutputInput.Text = folder;
    }
    private void LanguageChanged(object? sender, EventArgs args) { RefreshDirection(); RefreshExport(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Localization.Changed -= LanguageChanged;
        _lifetime.Cancel(); _previewRequests.Dispose();
        SourceImage.Source = ResultImage.Source = null; _bitmap?.Dispose(); _bitmap = null;
    }
}
